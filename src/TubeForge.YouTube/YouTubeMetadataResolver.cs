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

            var clientResult = await TryResolveWithDirectClientsAsync(
                html,
                watchResult.Value,
                url,
                timeoutSource.Token).ConfigureAwait(false);
            if (clientResult is not null && clientResult.Metadata.Formats.Count > 0)
            {
                return Result<WatchPageData>.Success(MergeClientFormats(watchResult.Value, clientResult));
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

    private static WatchPageData MergeClientFormats(WatchPageData watchPage, WatchPageData client)
    {
        if (watchPage.Metadata.Formats.Count == 0)
        {
            return client;
        }

        var clientFormatIds = client.Metadata.Formats
            .Select(format => format.FormatId)
            .ToHashSet();
        var formats = client.Metadata.Formats
            .Concat(watchPage.Metadata.Formats.Where(format => !clientFormatIds.Contains(format.FormatId)))
            .ToArray();
        return client with
        {
            Metadata = client.Metadata with { Formats = formats },
            PlayerScriptUrl = watchPage.PlayerScriptUrl ?? client.PlayerScriptUrl,
            CipheredFormatCount = Math.Max(watchPage.CipheredFormatCount, client.CipheredFormatCount),
            Diagnostics = new ExtractionDiagnostics(
                (client.Diagnostics?.Stage ?? "ClientResolved") + "+WatchPage")
        };
    }

    private async Task<WatchPageData?> TryResolveWithDirectClientsAsync(
        string html,
        WatchPageData fallback,
        Uri watchUrl,
        CancellationToken cancellationToken)
    {
        var requiresResolvedLiveManifest = fallback.Metadata.ContentKind == VideoContentKind.LiveActive;
        foreach (var profile in new[]
                 {
                     YouTubeClientProfile.AndroidVr,
                     YouTubeClientProfile.WebEmbedded,
                     YouTubeClientProfile.Tv,
                     YouTubeClientProfile.Android
                 })
        {
            var result = await TryResolveWithClientAsync(
                html,
                fallback,
                profile,
                cancellationToken).ConfigureAwait(false);
            if (result is not null && result.Metadata.Formats.Count > 0 &&
                (!requiresResolvedLiveManifest || result.Metadata.Formats.Any(format =>
                    format.IsLiveHls && !format.IsLiveManifestPending)) &&
                await HasAccessibleMediaAsync(result, watchUrl, cancellationToken).ConfigureAwait(false))
            {
                return result;
            }
        }

        return null;
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

        foreach (var format in probes)
        {
            var lastByte = Math.Max(0, (format.ContentLength ?? 1) - 1);
            using var probe = new HttpRequestMessage(HttpMethod.Get, format.Url);
            if (!HttpUserAgentHeader.TryApply(probe, format.HttpUserAgent))
            {
                return false;
            }

            probe.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            probe.Headers.Range = new RangeHeaderValue(lastByte, lastByte);
            probe.Headers.Referrer = watchUrl;
            using var response = await _httpClient.SendAsync(
                probe,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? format.Url;
            if (!response.IsSuccessStatusCode ||
                finalUri.Scheme != Uri.UriSchemeHttps ||
                (!finalUri.Host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase) &&
                 !finalUri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase)))
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
        using var probe = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
        AddBrowserHeaders(probe);
        probe.Headers.Accept.Clear();
        probe.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        probe.Headers.Range = new RangeHeaderValue(0, 0);
        probe.Headers.Referrer = watchUrl;
        using var response = await _httpClient.SendAsync(
            probe,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri ?? mediaUrl;
        return response.IsSuccessStatusCode &&
               finalUri.Scheme == Uri.UriSchemeHttps &&
               (finalUri.Host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
                finalUri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase));
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
