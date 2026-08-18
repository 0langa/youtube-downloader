using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TubeForge.Core.Errors;
using TubeForge.Core.Media;
using TubeForge.Core.Networking;
using TubeForge.Core.Results;
using TubeForge.Core.YouTube;
using TubeForge.YouTube.Extraction;
using TubeForge.YouTube.Player;

namespace TubeForge.YouTube;

public sealed class YouTubeMetadataResolver
{
    private const int MaximumWatchPageCharacters = 8 * 1024 * 1024;
    private const int MaximumPlayerScriptCharacters = 6 * 1024 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly Uri DefaultOrigin = new("https://www.youtube.com/");
    private static readonly PlayerTransformCache TransformCache = new();

    /// <summary>
    /// Clients queried together on every analysis. Their ladders are complementary rather than
    /// redundant, so both are resolved and merged instead of stopping at the first answer.
    /// </summary>
    private static readonly YouTubeClientProfile[] PrimaryProfiles =
    [
        YouTubeClientProfile.VisionOs,
        YouTubeClientProfile.AndroidVr
    ];

    /// <summary>
    /// Tried one at a time only when no primary client answers. These carry narrower ladders or
    /// depend on provider attestation that is frequently unavailable, so they exist for
    /// resilience rather than quality.
    /// </summary>
    private static readonly YouTubeClientProfile[] FallbackProfiles =
    [
        YouTubeClientProfile.Ios,
        YouTubeClientProfile.Tv,
        YouTubeClientProfile.WebEmbedded,
        YouTubeClientProfile.Android
    ];
    private readonly HttpClient _httpClient;
    private readonly Uri _origin;
    private readonly TimeSpan _requestTimeout;

    public YouTubeMetadataResolver(HttpClient httpClient, TimeSpan? requestTimeout = null)
        : this(httpClient, DefaultOrigin, requestTimeout)
    {
    }

    internal YouTubeMetadataResolver(HttpClient httpClient, Uri origin, TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri || origin.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(origin.IdnHost) || origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment))
        {
            throw new ArgumentException("The metadata origin must be an HTTP(S) origin.", nameof(origin));
        }

        _origin = origin;
        _requestTimeout = ValidateRequestTimeout(requestTimeout);
    }

    private static TimeSpan ValidateRequestTimeout(TimeSpan? value)
    {
        var timeout = value ?? DefaultRequestTimeout;
        if (timeout < TimeSpan.FromSeconds(5) || timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return timeout;
    }

    public async Task<Result<WatchPageData>> ResolveAsync(
        YouTubeVideoId videoId,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_requestTimeout);

        try
        {
            var url = new Uri(_origin, $"watch?v={Uri.EscapeDataString(videoId.Value)}&hl=en");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddBrowserHeaders(request);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return HttpFailure(response);
            }

            if (response.Content.Headers.ContentLength is > MaximumWatchPageCharacters)
            {
                return ExtractorFailure("The watch page exceeded the safe size limit.");
            }

            var html = await ReadBoundedTextAsync(
                response.Content,
                MaximumWatchPageCharacters,
                timeoutSource.Token).ConfigureAwait(false);
            var watchResult = YouTubeWatchPageParser.Parse(html);
            if (!watchResult.IsSuccess)
            {
                return watchResult;
            }

            var unresolvedActiveLive = watchResult.Value.Metadata.ContentKind == VideoContentKind.LiveActive &&
                                       watchResult.Value.Metadata.Formats.Any(format =>
                                           format.IsLiveHls && format.IsLiveManifestPending);
            if (watchResult.Value.Metadata.ContentKind == VideoContentKind.LiveUpcoming ||
                watchResult.Value.Metadata.Formats.Any(format =>
                    format.IsLiveHls && !format.IsLiveManifestPending))
            {
                return watchResult;
            }

            var clientOutcomes = new List<ClientProbeOutcome>();
            var clientResults = await TryResolveWithDirectClientsAsync(
                html,
                watchResult.Value,
                url,
                clientOutcomes,
                timeoutSource.Token).ConfigureAwait(false);
            if (clientResults.Count > 0)
            {
                return Result<WatchPageData>.Success(
                    MergeClientFormats(watchResult.Value, clientResults, clientOutcomes));
            }

            if (unresolvedActiveLive)
            {
                return Result<WatchPageData>.Failure(new TubeForgeError(
                    "Video.LiveManifestUnavailable",
                    "YouTube did not provide a trusted public HLS manifest for this active stream."));
            }

            if (watchResult.Value.Metadata.Formats.Count > 0)
            {
                return watchResult.Value.PlayerScriptUrl is not null &&
                       (HasThrottlingParameter(watchResult.Value) ||
                        watchResult.Value.CipheredFormatCount > 0)
                    ? await TryResolvePlayerTransformsAsync(
                        html,
                        watchResult.Value,
                        url,
                        timeoutSource.Token).ConfigureAwait(false)
                    : watchResult;
            }

            if (watchResult.Value.CipheredFormatCount == 0 ||
                watchResult.Value.PlayerScriptUrl is null)
            {
                return watchResult;
            }

            return await TryResolvePlayerTransformsAsync(
                html,
                watchResult.Value,
                url,
                timeoutSource.Token).ConfigureAwait(false);
        }
        catch (ContentTooLargeException)
        {
            return ExtractorFailure("The watch page exceeded the safe size limit.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<WatchPageData>.Failure(new TubeForgeError(
                "Operation.Cancelled",
                "Video analysis was cancelled."));
        }
        catch (OperationCanceledException)
        {
            return Result<WatchPageData>.Failure(new TubeForgeError(
                "Network.Timeout",
                "YouTube did not respond before the analysis timeout.",
                IsTransient: true));
        }
        catch (HttpRequestException exception)
        {
            return Result<WatchPageData>.Failure(new TubeForgeError(
                "Network.RequestFailed",
                "TubeForge could not connect to YouTube.",
                exception.GetType().Name,
                IsTransient: true));
        }
        catch (IOException exception)
        {
            return Result<WatchPageData>.Failure(new TubeForgeError(
                "Network.ReadFailed",
                "TubeForge could not read YouTube's response.",
                exception.GetType().Name,
                IsTransient: true));
        }
    }

    /// <summary>
    /// Combines every verified client ladder with any usable watch-page format into a single
    /// offer set. No individual client exposes the complete ladder: the visionOS client carries
    /// the highest video tiers while the Android VR client is the only one publishing the
    /// high-bitrate and multichannel audio tiers, so taking the first client that answers loses
    /// real quality the provider is willing to serve.
    /// </summary>
    private static WatchPageData MergeClientFormats(
        WatchPageData watchPage,
        IReadOnlyList<WatchPageData> clients,
        IReadOnlyList<ClientProbeOutcome> outcomes)
    {
        var primary = clients[0];
        if (clients.Count == 1 && watchPage.Metadata.Formats.Count == 0)
        {
            return primary with
            {
                Diagnostics = new ExtractionDiagnostics(
                    StageName(primary),
                    ClientOutcomes: outcomes)
            };
        }

        var formats = new List<StreamFormat>(primary.Metadata.Formats);
        var known = formats.Select(format => format.FormatId).ToHashSet();
        var contributors = new List<string> { StageName(primary) };

        foreach (var client in clients.Skip(1))
        {
            var added = false;
            foreach (var format in client.Metadata.Formats.Where(format => known.Add(format.FormatId)))
            {
                formats.Add(format);
                added = true;
            }

            if (added)
            {
                contributors.Add(StageName(client));
            }
        }

        // Watch-page entries are only safe to offer when they need no player transform; a
        // throttled URL downloads at a fraction of line speed instead of failing visibly.
        var watchPageFormats = watchPage.Metadata.Formats
            .Where(format => known.Add(format.FormatId) && !ThrottlingUrl.RequiresTransform(format.Url))
            .ToArray();
        if (watchPageFormats.Length > 0)
        {
            formats.AddRange(watchPageFormats);
            contributors.Add("WatchPage");
        }

        return primary with
        {
            Metadata = primary.Metadata with { Formats = formats.ToArray() },
            PlayerScriptUrl = watchPage.PlayerScriptUrl ?? primary.PlayerScriptUrl,
            CipheredFormatCount = Math.Max(watchPage.CipheredFormatCount, primary.CipheredFormatCount),
            Diagnostics = new ExtractionDiagnostics(
                string.Join('+', contributors),
                ClientOutcomes: outcomes)
        };
    }

    private static string StageName(WatchPageData data) => data.Diagnostics?.Stage ?? "ClientResolved";

    /// <summary>
    /// Resolves the primary clients concurrently and keeps every one whose media is reachable,
    /// then falls back to the secondary clients one at a time only when no primary answered.
    /// </summary>
    private async Task<IReadOnlyList<WatchPageData>> TryResolveWithDirectClientsAsync(
        string html,
        WatchPageData fallback,
        Uri watchUrl,
        List<ClientProbeOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        var primary = await Task.WhenAll(PrimaryProfiles.Select(profile =>
            TryResolveVerifiedClientAsync(html, fallback, profile, watchUrl, cancellationToken)))
            .ConfigureAwait(false);
        outcomes.AddRange(primary.Select(entry => entry.Outcome));
        var accepted = primary
            .Select(entry => entry.Data)
            .Where(data => data is not null)
            .Cast<WatchPageData>()
            .ToList();
        if (accepted.Count > 0)
        {
            return accepted;
        }

        foreach (var profile in FallbackProfiles)
        {
            var (data, outcome) = await TryResolveVerifiedClientAsync(
                html,
                fallback,
                profile,
                watchUrl,
                cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);
            if (data is not null)
            {
                return [data];
            }
        }

        return [];
    }

    private async Task<(WatchPageData? Data, ClientProbeOutcome Outcome)> TryResolveVerifiedClientAsync(
        string html,
        WatchPageData fallback,
        YouTubeClientProfile profile,
        Uri watchUrl,
        CancellationToken cancellationToken)
    {
        var requiresResolvedLiveManifest = fallback.Metadata.ContentKind == VideoContentKind.LiveActive;
        var result = await TryResolveWithClientAsync(
            html,
            fallback,
            profile,
            cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return (null, new ClientProbeOutcome(profile.Name, ClientProbeResult.NoResponse));
        }

        var formatCount = result.Metadata.Formats.Count;
        if (formatCount == 0)
        {
            return (null, new ClientProbeOutcome(profile.Name, ClientProbeResult.NoFormats));
        }

        if (requiresResolvedLiveManifest && !result.Metadata.Formats.Any(format =>
                format.IsLiveHls && !format.IsLiveManifestPending))
        {
            return (null, new ClientProbeOutcome(
                profile.Name,
                ClientProbeResult.LiveManifestMissing,
                formatCount));
        }

        if (!await HasAccessibleMediaAsync(result, watchUrl, cancellationToken).ConfigureAwait(false))
        {
            // The client published a ladder its media servers will not actually serve over ranged
            // reads. Offering it would produce downloads that stall partway through.
            return (null, new ClientProbeOutcome(
                profile.Name,
                ClientProbeResult.MediaUnreachable,
                formatCount));
        }

        return (result, new ClientProbeOutcome(profile.Name, ClientProbeResult.Accepted, formatCount));
    }

    private async Task<WatchPageData?> TryResolveWithClientAsync(
        string html,
        WatchPageData fallback,
        YouTubeClientProfile profile,
        CancellationToken cancellationToken)
    {
        var apiKey = YouTubeWatchPageParser.ExtractConfigurationValue(html, "INNERTUBE_API_KEY");
        if (!IsSafeConfigurationToken(apiKey))
        {
            return null;
        }

        var visitorData = YouTubeWatchPageParser.ExtractConfigurationValue(html, "VISITOR_DATA");
        var client = new Dictionary<string, object?>
        {
            ["clientName"] = profile.Name,
            ["clientVersion"] = profile.Version,
            ["hl"] = "en",
            ["gl"] = "US"
        };
        if (!string.IsNullOrWhiteSpace(visitorData))
        {
            client["visitorData"] = visitorData;
        }

        if (profile.AndroidSdkVersion is not null)
        {
            client["androidSdkVersion"] = profile.AndroidSdkVersion;
        }

        if (profile.DeviceMake is not null)
        {
            client["deviceMake"] = profile.DeviceMake;
        }

        if (profile.DeviceModel is not null)
        {
            client["deviceModel"] = profile.DeviceModel;
        }

        if (profile.OsName is not null)
        {
            client["osName"] = profile.OsName;
        }

        if (profile.OsVersion is not null)
        {
            client["osVersion"] = profile.OsVersion;
        }

        var context = new Dictionary<string, object?> { ["client"] = client };
        if (profile.IsEmbedded)
        {
            context["thirdParty"] = new { embedUrl = "https://www.youtube.com/" };
        }

        var payload = new
        {
            context,
            videoId = fallback.Metadata.Id.Value,
            contentCheckOk = true,
            racyCheckOk = true
        };

        try
        {
            var endpoint = new Uri(
                $"https://www.youtube.com/youtubei/v1/player?prettyPrint=false&key={Uri.EscapeDataString(apiKey!)}");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (!HttpUserAgentHeader.TryApply(request, profile.UserAgent))
            {
                return null;
            }

            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", profile.NumericId);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", profile.Version);
            if (!string.IsNullOrWhiteSpace(visitorData))
            {
                request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);
            }

            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength is > MaximumWatchPageCharacters)
            {
                return null;
            }

            var json = await ReadBoundedTextAsync(
                response.Content,
                MaximumWatchPageCharacters,
                cancellationToken).ConfigureAwait(false);
            var parsed = YouTubeWatchPageParser.Parse("ytInitialPlayerResponse=" + json + ";");
            if (!parsed.IsSuccess || parsed.Value.Metadata.Id != fallback.Metadata.Id)
            {
                return null;
            }

            return parsed.Value with
            {
                Metadata = parsed.Value.Metadata with
                {
                    Formats = parsed.Value.Metadata.Formats
                        .Select(format => format with { HttpUserAgent = profile.UserAgent })
                        .ToArray(),
                    ContentKind = fallback.Metadata.ContentKind,
                    LiveStartedAtUtc = fallback.Metadata.LiveStartedAtUtc,
                    LiveEndedAtUtc = fallback.Metadata.LiveEndedAtUtc,
                    CaptionTracks = parsed.Value.Metadata.CaptionTracks.Count > 0
                        ? parsed.Value.Metadata.CaptionTracks
                        : fallback.Metadata.CaptionTracks,
                    Chapters = parsed.Value.Metadata.Chapters.Count > 0
                        ? parsed.Value.Metadata.Chapters
                        : fallback.Metadata.Chapters
                },
                PlayerScriptUrl = fallback.PlayerScriptUrl,
                Diagnostics = new ExtractionDiagnostics($"ClientResolved:{profile.Name}")
            };
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ContentTooLargeException)
        {
            return null;
        }
    }

    private async Task<bool> HasAccessibleMediaAsync(
        WatchPageData data,
        Uri watchUrl,
        CancellationToken cancellationToken)
    {
        var formats = data.Metadata.Formats;
        var video = formats
            .Where(format => format.HasVideo)
            .OrderByDescending(format => format.Height ?? 0)
            .ThenByDescending(format => format.Bitrate ?? 0)
            .FirstOrDefault();
        var audio = formats
            .Where(format => format.Kind == StreamKind.AudioOnly)
            .OrderByDescending(format => format.Bitrate ?? 0)
            .FirstOrDefault();
        var probes = new[] { video, audio }
            .Where(format => format is not null)
            .Cast<StreamFormat>()
            .DistinctBy(format => format.Url)
            .ToArray();
        if (probes.Length == 0)
        {
            return false;
        }

        var requestNumber = 0;
        foreach (var format in probes)
        {
            var lastByte = Math.Max(0, (format.ContentLength ?? 1) - 1);
            // Match the download engines: some adaptive Googlevideo URLs reject HTTP Range headers.
            var usesRangeQuery = !format.IsLiveHls && IsGoogleVideo(format.Url);
            var probeUri = usesRangeQuery
                ? AddRangeQuery(format.Url, lastByte, lastByte, requestNumber++)
                : format.Url;
            using var probe = new HttpRequestMessage(HttpMethod.Get, probeUri);
            if (!HttpUserAgentHeader.TryApply(probe, format.HttpUserAgent))
            {
                return false;
            }

            probe.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            if (!usesRangeQuery)
            {
                probe.Headers.Range = new RangeHeaderValue(lastByte, lastByte);
            }

            probe.Headers.Referrer = watchUrl;
            using var response = await _httpClient.SendAsync(
                probe,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? format.Url;
            if (!response.IsSuccessStatusCode ||
                finalUri.Scheme != Uri.UriSchemeHttps ||
                !IsGoogleVideo(finalUri))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<Result<WatchPageData>> TryResolvePlayerTransformsAsync(
        string html,
        WatchPageData fallback,
        Uri watchUrl,
        CancellationToken cancellationToken)
    {
        var playerScriptUrl = fallback.PlayerScriptUrl!;
        try
        {
            using var scriptRequest = new HttpRequestMessage(HttpMethod.Get, playerScriptUrl);
            AddBrowserHeaders(scriptRequest);
            scriptRequest.Headers.Accept.Clear();
            scriptRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/javascript"));
            using var scriptResponse = await _httpClient.SendAsync(
                scriptRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!scriptResponse.IsSuccessStatusCode ||
                scriptResponse.Content.Headers.ContentLength is > MaximumPlayerScriptCharacters)
            {
                return WithDiagnostics(fallback, "PlayerScriptUnavailable");
            }

            var script = await ReadBoundedTextAsync(
                scriptResponse.Content,
                MaximumPlayerScriptCharacters,
                cancellationToken).ConfigureAwait(false);
            if (TransformCache.TryGet(script, out var cachedPlans))
            {
                var cachedResult = ParseWithPlans(html, cachedPlans);
                if (IsUsableTransformedResult(cachedResult, fallback))
                {
                    return cachedResult;
                }

                TransformCache.Remove(script);
            }

            var signatureCandidates = SignatureTransformExtractor.Extract(script);
            var throttlingCandidates = ThrottlingTransformExtractor
                .Extract(script, signatureCandidates)
                .Take(4)
                .ToArray();
            var cipher = YouTubeWatchPageParser.ExtractSignatureCiphers(html).FirstOrDefault();
            if (cipher is null)
            {
                return await TryResolveThrottledFormatsAsync(
                    html,
                    fallback,
                    watchUrl,
                    script,
                    throttlingCandidates,
                    cancellationToken).ConfigureAwait(false);
            }

            var probeAttempts = 0;
            foreach (var signaturePlan in signatureCandidates.Take(8))
            {
                var signedCandidate = SignatureCipherUrl.Resolve(cipher, signaturePlan);
                if (signedCandidate is null)
                {
                    continue;
                }

                var nPlans = ThrottlingUrl.RequiresTransform(signedCandidate)
                    ? throttlingCandidates.Cast<SignatureTransformPlan?>()
                    : [null];
                foreach (var throttlingPlan in nPlans)
                {
                    var candidate = throttlingPlan is null
                        ? signedCandidate
                        : ThrottlingUrl.Resolve(signedCandidate, throttlingPlan);
                    if (candidate is null)
                    {
                        continue;
                    }

                    probeAttempts++;
                    if (!await ProbeMediaUrlAsync(candidate, watchUrl, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    var plans = new PlayerTransformPlans(signaturePlan, throttlingPlan);
                    var resolved = ParseWithPlans(html, plans);
                    if (IsUsableTransformedResult(resolved, fallback))
                    {
                        TransformCache.Store(script, plans);
                        return resolved;
                    }
                }
            }

            return WithDiagnostics(
                fallback,
                signatureCandidates.Count == 0 ? "TransformPlanMissing" : "TransformPlanRejected",
                signatureCandidates.Count,
                probeAttempts);
        }
        catch (ContentTooLargeException)
        {
            return WithDiagnostics(fallback, "PlayerScriptTooLarge");
        }
        catch (HttpRequestException)
        {
            return WithDiagnostics(fallback, "PlayerScriptRequestFailed");
        }
        catch (IOException)
        {
            return WithDiagnostics(fallback, "PlayerScriptReadFailed");
        }
    }

    private async Task<Result<WatchPageData>> TryResolveThrottledFormatsAsync(
        string html,
        WatchPageData fallback,
        Uri watchUrl,
        string script,
        IReadOnlyList<SignatureTransformPlan> throttlingCandidates,
        CancellationToken cancellationToken)
    {
        if (!HasThrottlingParameter(fallback))
        {
            return WithDiagnostics(fallback, "CipherMissing");
        }

        var probeAttempts = 0;
        foreach (var throttlingPlan in throttlingCandidates)
        {
            var plans = new PlayerTransformPlans(null, throttlingPlan);
            var resolved = ParseWithPlans(html, plans);
            var candidate = resolved.IsSuccess
                ? resolved.Value.Metadata.Formats.FirstOrDefault()?.Url
                : null;
            if (candidate is null)
            {
                continue;
            }

            probeAttempts++;
            if (!await ProbeMediaUrlAsync(candidate, watchUrl, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            TransformCache.Store(script, plans);
            return resolved;
        }

        return WithDiagnostics(
            fallback,
            throttlingCandidates.Count == 0 ? "ThrottlingPlanMissing" : "ThrottlingPlanRejected",
            throttlingCandidates.Count,
            probeAttempts);
    }

    private async Task<bool> ProbeMediaUrlAsync(
        Uri mediaUrl,
        Uri watchUrl,
        CancellationToken cancellationToken)
    {
        var usesRangeQuery = IsGoogleVideo(mediaUrl);
        var probeUri = usesRangeQuery ? AddRangeQuery(mediaUrl, 0, 0, 0) : mediaUrl;
        using var probe = new HttpRequestMessage(HttpMethod.Get, probeUri);
        AddBrowserHeaders(probe);
        probe.Headers.Accept.Clear();
        probe.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        if (!usesRangeQuery)
        {
            probe.Headers.Range = new RangeHeaderValue(0, 0);
        }

        probe.Headers.Referrer = watchUrl;
        using var response = await _httpClient.SendAsync(
            probe,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri ?? mediaUrl;
        return response.IsSuccessStatusCode && IsGoogleVideo(finalUri);
    }

    private static bool IsGoogleVideo(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase));

    private static Uri AddRangeQuery(Uri source, long from, long to, int requestNumber)
    {
        var separator = string.IsNullOrEmpty(source.Query) ? "?" : "&";
        return new Uri(
            source.AbsoluteUri + separator +
            $"range={from}-{to}&rn={requestNumber}&rbuf=0",
            UriKind.Absolute);
    }

    private static Result<WatchPageData> ParseWithPlans(string html, PlayerTransformPlans plans)
    {
        Func<string, Uri?>? signatureResolver = plans.Signature is null
            ? null
            : cipher => SignatureCipherUrl.Resolve(cipher, plans.Signature);
        Func<Uri, Uri?>? mediaUrlResolver = plans.Throttling is null
            ? null
            : url => ThrottlingUrl.Resolve(url, plans.Throttling);
        return YouTubeWatchPageParser.Parse(html, signatureResolver, mediaUrlResolver);
    }

    private static bool IsUsableTransformedResult(
        Result<WatchPageData> result,
        WatchPageData fallback) =>
        result.IsSuccess &&
        result.Value.Metadata.Formats.Count > 0 &&
        (fallback.CipheredFormatCount == 0 ||
         result.Value.Metadata.Formats.Count > fallback.Metadata.Formats.Count);

    private static bool HasThrottlingParameter(WatchPageData data) =>
        data.Metadata.Formats.Any(format => ThrottlingUrl.RequiresTransform(format.Url));

    private static Result<WatchPageData> WithDiagnostics(
        WatchPageData fallback,
        string stage,
        int planCount = 0,
        int probeCount = 0) =>
        Result<WatchPageData>.Success(fallback with
        {
            Diagnostics = new ExtractionDiagnostics(stage, planCount, probeCount)
        });

    private static void AddBrowserHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    private static bool IsSafeConfigurationToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static async Task<string> ReadBoundedTextAsync(
        HttpContent content,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024,
            leaveOpen: false);
        var buffer = new char[16 * 1024];
        var builder = new StringBuilder(Math.Min(maximumCharacters, 512 * 1024));

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return builder.ToString();
            }

            if (builder.Length > maximumCharacters - count)
            {
                throw new ContentTooLargeException();
            }

            builder.Append(buffer, 0, count);
        }
    }

    private static Result<WatchPageData> HttpFailure(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.TooManyRequests => Result<WatchPageData>.Failure(new TubeForgeError(
            "Network.RateLimited",
            "YouTube temporarily rate-limited this device.",
            IsTransient: true,
            RetryAfter: HttpRetryAfterParser.Parse(response.Headers))),
        HttpStatusCode.Forbidden => Result<WatchPageData>.Failure(new TubeForgeError(
            "Network.Forbidden", "YouTube refused the video analysis request.")),
        _ => Result<WatchPageData>.Failure(new TubeForgeError(
            "Network.HttpError",
            $"YouTube returned HTTP {(int)response.StatusCode} while analyzing the video.",
            IsTransient: (int)response.StatusCode >= 500))
    };

    private static Result<WatchPageData> ExtractorFailure(string detail) =>
        Result<WatchPageData>.Failure(new TubeForgeError(
            "Extractor.PageChanged",
            "TubeForge could not safely process the YouTube watch page.",
            detail));

    private sealed class ContentTooLargeException : Exception;
}
