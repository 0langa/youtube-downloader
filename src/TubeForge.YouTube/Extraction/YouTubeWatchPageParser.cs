using System.Globalization;
using System.Text.Json;
using TubeForge.Core.Errors;
using TubeForge.Core.Media;
using TubeForge.Core.Results;
using TubeForge.Core.YouTube;
using TubeForge.YouTube.Thumbnails;

namespace TubeForge.YouTube.Extraction;

public static class YouTubeWatchPageParser
{
    private const string PlayerResponseMarker = "ytInitialPlayerResponse";
    private const string InitialDataMarker = "ytInitialData";
    public const int LiveHlsFormatId = 1_000_001;
    private static readonly Uri YouTubeOrigin = new("https://www.youtube.com/");

    public static Result<WatchPageData> Parse(
        string? html,
        Func<string, Uri?>? signatureCipherResolver = null,
        Func<Uri, Uri?>? mediaUrlResolver = null)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return Failure("The watch page was empty.");
        }

        var searchIndex = 0;
        while (TryFindNextJsonObject(html, PlayerResponseMarker, searchIndex, out var json, out var nextIndex))
        {
            searchIndex = nextIndex;
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });

                var mapped = MapPlayerResponse(
                    document.RootElement,
                    html,
                    signatureCipherResolver,
                    mediaUrlResolver);
                if (mapped is not null)
                {
                    var result = mapped.Value;
                    return !result.IsSuccess || result.Value.Metadata.Chapters.Count > 0
                        ? result
                        : Result<WatchPageData>.Success(MergeInitialDataChapters(result.Value, html));
                }
            }
            catch (JsonException)
            {
                // Another occurrence may contain the actual assigned player response.
            }
        }

        return Failure("The watch page did not contain a supported player response.");
    }

    private static WatchPageData MergeInitialDataChapters(WatchPageData data, string html)
    {
        var searchIndex = 0;
        while (TryFindNextJsonObject(html, InitialDataMarker, searchIndex, out var json, out var nextIndex))
        {
            searchIndex = nextIndex;
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
                var chapters = MapChapters(document.RootElement, data.Metadata.Duration);
                if (chapters.Count > 0)
                {
                    return data with
                    {
                        Metadata = data.Metadata with { Chapters = chapters }
                    };
                }
            }
            catch (JsonException)
            {
                // Another occurrence may contain the assigned initial-data payload.
            }
        }

        return data;
    }

    internal static IReadOnlyList<string> ExtractSignatureCiphers(string html)
    {
        var ciphers = new List<string>();
        var searchIndex = 0;
        while (TryFindNextJsonObject(html, PlayerResponseMarker, searchIndex, out var json, out var nextIndex))
        {
            searchIndex = nextIndex;
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
                var root = document.RootElement;
                if (!root.TryGetProperty("streamingData", out var streamingData))
                {
                    continue;
                }

                CollectCiphers(streamingData, "formats", ciphers);
                CollectCiphers(streamingData, "adaptiveFormats", ciphers);
                if (ciphers.Count > 0)
                {
                    return ciphers;
                }
            }
            catch (JsonException)
            {
                // Try the next marker occurrence.
            }
        }

        return ciphers;
    }

    internal static string? ExtractConfigurationValue(string html, string property)
    {
        var searchIndex = 0;
        while ((searchIndex = html.IndexOf($"\"{property}\"", searchIndex, StringComparison.Ordinal)) >= 0)
        {
            var colon = html.IndexOf(':', searchIndex + property.Length + 2);
            if (colon < 0)
            {
                return null;
            }

            var quoteStart = colon + 1;
            while (quoteStart < html.Length && char.IsWhiteSpace(html[quoteStart]))
            {
                quoteStart++;
            }

            if (quoteStart < html.Length && html[quoteStart] == '"' &&
                TryReadJsonString(html, quoteStart, out var token, out searchIndex))
            {
                try
                {
                    return JsonSerializer.Deserialize<string>(token);
                }
                catch (JsonException)
                {
                    // Try another configuration occurrence.
                }
            }
            else
            {
                searchIndex += property.Length + 2;
            }
        }

        return null;
    }

    private static Result<WatchPageData>? MapPlayerResponse(
        JsonElement root,
        string html,
        Func<string, Uri?>? signatureCipherResolver,
        Func<Uri, Uri?>? mediaUrlResolver)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("videoDetails", out var details) ||
            details.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var microformat = PlayerMicroformat(root);
        var liveBroadcast = microformat is { } renderer &&
                            renderer.TryGetProperty("liveBroadcastDetails", out var broadcast) &&
                            broadcast.ValueKind == JsonValueKind.Object
            ? broadcast
            : (JsonElement?)null;
        var liveStartedAtUtc = ParseTimestamp(liveBroadcast is { } liveStart
            ? ReadString(liveStart, "startTimestamp")
            : null);
        var liveEndedAtUtc = ParseTimestamp(liveBroadcast is { } liveEnd
            ? ReadString(liveEnd, "endTimestamp")
            : null);
        var isLiveNow = ReadBoolean(details, "isLive") ||
                        liveBroadcast is { } liveNow && ReadBoolean(liveNow, "isLiveNow");
        var status = ReadString(root, "playabilityStatus", "status") ?? "UNKNOWN";
        var rawId = ReadString(details, "videoId");
        if (!YouTubeVideoId.TryCreate(rawId, out var videoId))
        {
            return Failure("The player response contained an invalid video ID.");
        }

        var title = ReadString(details, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return Failure("The player response did not contain a video title.");
        }

        if (!status.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            var reason = ReadString(root, "playabilityStatus", "reason") ??
                         "YouTube reported that this video is unavailable.";
            if (status.Equals("LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase))
            {
                return Result<WatchPageData>.Failure(new TubeForgeError(
                    "Video.AuthenticationUnsupported",
                    "Authenticated and access-controlled media are intentionally unsupported in TubeForge v2."));
            }

            if (status.Equals("LIVE_STREAM_OFFLINE", StringComparison.OrdinalIgnoreCase) ||
                liveStartedAtUtc is not null && liveEndedAtUtc is null)
            {
                var pendingFormat = LiveFormat(
                    new Uri(YouTubeOrigin, $"watch?v={Uri.EscapeDataString(videoId.Value)}"),
                    manifestPending: true);
                return Result<WatchPageData>.Success(new WatchPageData(
                    LiveMetadata(
                        root,
                        details,
                        videoId,
                        title,
                        liveStartedAtUtc,
                        liveEndedAtUtc,
                        VideoContentKind.LiveUpcoming,
                        pendingFormat),
                    ParsePlayerScriptUrl(html),
                    0,
                    status,
                    new ExtractionDiagnostics("LiveUpcoming")));
            }

            return Result<WatchPageData>.Failure(new TubeForgeError("Video.Unavailable", reason));
        }

        var formats = new List<StreamFormat>();
        var cipheredCount = 0;
        if (root.TryGetProperty("streamingData", out var streamingData) &&
            streamingData.ValueKind == JsonValueKind.Object)
        {
            MapFormatArray(
                streamingData,
                "formats",
                formats,
                signatureCipherResolver,
                mediaUrlResolver,
                ref cipheredCount);
            MapFormatArray(
                streamingData,
                "adaptiveFormats",
                formats,
                signatureCipherResolver,
                mediaUrlResolver,
                ref cipheredCount);
        }

        if (isLiveNow)
        {
            if (!TryParseHlsManifest(root, out var manifestUri))
            {
                var pendingFormat = LiveFormat(
                    new Uri(YouTubeOrigin, $"watch?v={Uri.EscapeDataString(videoId.Value)}"),
                    manifestPending: true,
                    qualityLabel: "Active live · resolving manifest");
                return Result<WatchPageData>.Success(new WatchPageData(
                    LiveMetadata(
                        root,
                        details,
                        videoId,
                        title,
                        liveStartedAtUtc,
                        liveEndedAtUtc,
                        VideoContentKind.LiveActive,
                        pendingFormat),
                    ParsePlayerScriptUrl(html),
                    0,
                    status,
                    new ExtractionDiagnostics("LiveManifestClientFallback")));
            }

            return Result<WatchPageData>.Success(new WatchPageData(
                LiveMetadata(
                    root,
                    details,
                    videoId,
                    title,
                    liveStartedAtUtc,
                    liveEndedAtUtc,
                    VideoContentKind.LiveActive,
                    LiveFormat(manifestUri, manifestPending: false)),
                ParsePlayerScriptUrl(html),
                0,
                status,
                new ExtractionDiagnostics("LiveHlsResolved")));
        }

        var duration = ParseDuration(ReadString(details, "lengthSeconds"));
        var isLiveContent = ReadBoolean(details, "isLiveContent") || liveBroadcast is not null;
        var isShort = ReadBoolean(details, "isShortsEligible") ||
                      microformat is { } shortRenderer && ReadBoolean(shortRenderer, "isShortsEligible");
        var metadata = new VideoMetadata
        {
            Id = videoId,
            Title = title.Trim(),
            Channel = ReadString(details, "author")?.Trim() ?? string.Empty,
            Duration = duration,
            ThumbnailUrl = ParseThumbnail(details),
            Availability = VideoAvailability.Available,
            ContentKind = isLiveContent
                ? VideoContentKind.LiveReplay
                : isShort ? VideoContentKind.Short : VideoContentKind.Standard,
            LiveStartedAtUtc = liveStartedAtUtc,
            LiveEndedAtUtc = liveEndedAtUtc,
            Formats = SelectPreferredVariants(formats),
            CaptionTracks = MapCaptionTracks(root),
            Chapters = MapChapters(root, duration)
        };

        return Result<WatchPageData>.Success(new WatchPageData(
            metadata,
            ParsePlayerScriptUrl(html),
            cipheredCount,
            status));
    }

    private static VideoMetadata LiveMetadata(
        JsonElement root,
        JsonElement details,
        YouTubeVideoId videoId,
        string title,
        DateTimeOffset? liveStartedAtUtc,
        DateTimeOffset? liveEndedAtUtc,
        VideoContentKind contentKind,
        StreamFormat format) => new()
        {
            Id = videoId,
            Title = title.Trim(),
            Channel = ReadString(details, "author")?.Trim() ?? string.Empty,
            Duration = null,
            ThumbnailUrl = ParseThumbnail(details),
            Availability = contentKind == VideoContentKind.LiveUpcoming
            ? VideoAvailability.LiveStreamOffline
            : VideoAvailability.Available,
            ContentKind = contentKind,
            LiveStartedAtUtc = liveStartedAtUtc,
            LiveEndedAtUtc = liveEndedAtUtc,
            Formats = [format],
            CaptionTracks = MapCaptionTracks(root),
            Chapters = []
        };

    private static StreamFormat LiveFormat(
        Uri uri,
        bool manifestPending,
        string? qualityLabel = null) => new()
        {
            FormatId = LiveHlsFormatId,
            Url = uri,
            Container = MediaContainer.Mkv,
            Kind = StreamKind.Progressive,
            VideoCodec = VideoCodec.Unknown,
            AudioCodec = AudioCodec.Unknown,
            QualityLabel = qualityLabel ??
                       (manifestPending ? "Upcoming live · wait and record" : "Active live · record from now"),
            IsLiveHls = true,
            IsLiveManifestPending = manifestPending
        };

    private static bool TryParseHlsManifest(JsonElement root, out Uri uri)
    {
        uri = null!;
        var value = ReadString(root, "streamingData", "hlsManifestUrl");
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8_192 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            (!parsed.Host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase) &&
             !parsed.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static IReadOnlyList<CaptionTrack> MapCaptionTracks(JsonElement root)
    {
        if (!root.TryGetProperty("captions", out var captions) ||
            !captions.TryGetProperty("playerCaptionsTracklistRenderer", out var renderer) ||
            !renderer.TryGetProperty("captionTracks", out var tracks) ||
            tracks.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var output = new List<CaptionTrack>();
        foreach (var element in tracks.EnumerateArray())
        {
            var languageCode = ReadString(element, "languageCode")?.Trim();
            var name = ReadLocalizedText(element, "name")?.Trim();
            if (!IsSafeLanguageCode(languageCode) ||
                string.IsNullOrWhiteSpace(name) ||
                name.Length > 256 ||
                !TryParseCaptionUrl(ReadString(element, "baseUrl"), out var url))
            {
                continue;
            }

            var vssId = ReadString(element, "vssId")?.Trim() ?? string.Empty;
            if (vssId.Length > 64 || vssId.Any(char.IsControl))
            {
                vssId = string.Empty;
            }

            output.Add(new CaptionTrack
            {
                Url = url,
                LanguageCode = languageCode!,
                Name = name,
                VssId = vssId,
                IsAutoGenerated = string.Equals(
                    ReadString(element, "kind"),
                    "asr",
                    StringComparison.OrdinalIgnoreCase),
                IsTranslatable = ReadBoolean(element, "isTranslatable")
            });
        }

        return output
            .DistinctBy(track => (track.VssId, track.LanguageCode, track.IsAutoGenerated))
            .ToArray();
    }

    private static IReadOnlyList<VideoChapter> MapChapters(JsonElement root, TimeSpan? duration)
    {
        const int maximumChapters = 1_000;
        if (!root.TryGetProperty("playerOverlays", out var overlays) ||
            !overlays.TryGetProperty("playerOverlayRenderer", out var overlayRenderer) ||
            !overlayRenderer.TryGetProperty("decoratedPlayerBarRenderer", out var decorated) ||
            !decorated.TryGetProperty("decoratedPlayerBarRenderer", out var decoratedRenderer) ||
            !decoratedRenderer.TryGetProperty("playerBar", out var playerBar) ||
            !playerBar.TryGetProperty("multiMarkersPlayerBarRenderer", out var markersRenderer) ||
            !markersRenderer.TryGetProperty("markersMap", out var markersMap) ||
            markersMap.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var chapters = new List<VideoChapter>();
        foreach (var markerGroup in markersMap.EnumerateArray())
        {
            if (!string.Equals(ReadString(markerGroup, "key"), "DESCRIPTION_CHAPTERS", StringComparison.Ordinal) ||
                !markerGroup.TryGetProperty("value", out var value) ||
                !value.TryGetProperty("chapters", out var chapterElements) ||
                chapterElements.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var chapterElement in chapterElements.EnumerateArray())
            {
                if (chapters.Count >= maximumChapters ||
                    !chapterElement.TryGetProperty("chapterRenderer", out var renderer))
                {
                    break;
                }

                var title = ReadLocalizedText(renderer, "title")?.Trim();
                var startMilliseconds = ReadInt64(renderer, "timeRangeStartMillis");
                if (string.IsNullOrWhiteSpace(title) ||
                    title.Length > 512 ||
                    title.Any(char.IsControl) ||
                    startMilliseconds is null or < 0 ||
                    startMilliseconds > TimeSpan.MaxValue.TotalMilliseconds ||
                    (duration is not null && startMilliseconds >= duration.Value.TotalMilliseconds))
                {
                    continue;
                }

                chapters.Add(new VideoChapter
                {
                    Title = title,
                    StartTime = TimeSpan.FromMilliseconds(startMilliseconds.Value)
                });
            }
        }

        return chapters
            .OrderBy(chapter => chapter.StartTime)
            .DistinctBy(chapter => chapter.StartTime)
            .ToArray();
    }

    private static void MapFormatArray(
        JsonElement streamingData,
        string propertyName,
        ICollection<StreamFormat> output,
        Func<string, Uri?>? signatureCipherResolver,
        Func<Uri, Uri?>? mediaUrlResolver,
        ref int cipheredCount)
    {
        if (!streamingData.TryGetProperty(propertyName, out var formats) ||
            formats.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var element in formats.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            Uri? resolvedUrl = null;
            if (element.TryGetProperty("url", out var urlProperty))
            {
                TryParseMediaUrl(urlProperty.GetString(), out resolvedUrl);
            }
            else if (TryReadCipher(element, out var cipher))
            {
                cipheredCount++;
                if (signatureCipherResolver is not null)
                {
                    resolvedUrl = signatureCipherResolver(cipher);
                }
            }

            if (resolvedUrl is not null && mediaUrlResolver is not null)
            {
                resolvedUrl = mediaUrlResolver(resolvedUrl);
            }

            if (resolvedUrl is null || !TryMapFormat(element, resolvedUrl, out var format))
            {
                continue;
            }

            output.Add(format);
        }
    }

    private static void CollectCiphers(
        JsonElement streamingData,
        string propertyName,
        ICollection<string> output)
    {
        if (!streamingData.TryGetProperty(propertyName, out var formats) ||
            formats.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var format in formats.EnumerateArray())
        {
            if (TryReadCipher(format, out var cipher))
            {
                output.Add(cipher);
            }
        }
    }

    private static bool TryReadCipher(JsonElement element, out string cipher)
    {
        cipher = string.Empty;
        foreach (var property in new[] { "signatureCipher", "cipher" })
        {
            if (element.TryGetProperty(property, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                cipher = value.GetString()!;
                return true;
            }
        }

        return false;
    }

    private static bool TryMapFormat(JsonElement element, Uri url, out StreamFormat format)
    {
        format = null!;
        if (!TryReadInt32(element, "itag", out var formatId) ||
            !element.TryGetProperty("mimeType", out var mimeProperty) ||
            mimeProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var mimeType = mimeProperty.GetString()!;
        var container = ParseContainer(mimeType);
        var codecs = ParseCodecs(mimeType);
        var videoCodec = ParseVideoCodec(codecs);
        var audioCodec = ParseAudioCodec(codecs);
        var mimeIsVideo = mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        var mimeIsAudio = mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
        var hasVideo = mimeIsVideo || videoCodec is not VideoCodec.None and not VideoCodec.Unknown;
        var hasAudio = mimeIsAudio || audioCodec is not AudioCodec.None and not AudioCodec.Unknown;
        var kind = (hasVideo, hasAudio) switch
        {
            (true, true) => StreamKind.Progressive,
            (true, false) => StreamKind.VideoOnly,
            (false, true) => StreamKind.AudioOnly,
            _ => mimeIsVideo ? StreamKind.VideoOnly : StreamKind.AudioOnly
        };

        format = new StreamFormat
        {
            FormatId = formatId,
            Url = url,
            Container = container,
            Kind = kind,
            VideoCodec = hasVideo ? videoCodec : VideoCodec.None,
            AudioCodec = hasAudio ? audioCodec : AudioCodec.None,
            Width = ReadInt32(element, "width"),
            Height = ReadInt32(element, "height"),
            FramesPerSecond = ReadInt32(element, "fps"),
            Bitrate = ReadInt64(element, "bitrate"),
            ContentLength = ReadStringInt64(element, "contentLength"),
            AudioSampleRate = ReadStringInt32(element, "audioSampleRate"),
            AudioChannels = ReadInt32(element, "audioChannels"),
            IsDrc = ReadBoolean(element, "isDrc"),
            AudioLanguage = ReadAudioTrackLanguage(element),
            IsOriginalAudio = ReadAudioTrackIsOriginal(element),
            IsHdr = ReadString(element, "qualityLabel")?.Contains("HDR", StringComparison.OrdinalIgnoreCase) == true ||
                    IsWideColourGamut(element),
            QualityLabel = ReadString(element, "qualityLabel") ?? ReadString(element, "audioQuality") ?? string.Empty
        };
        return true;
    }

    private static bool TryFindNextJsonObject(
        string source,
        string marker,
        int startIndex,
        out string json,
        out int nextIndex)
    {
        json = string.Empty;
        nextIndex = source.Length;
        var markerIndex = source.IndexOf(marker, startIndex, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        var objectStart = source.IndexOf('{', markerIndex + marker.Length);
        if (objectStart < 0 || objectStart - markerIndex > 256)
        {
            nextIndex = markerIndex + marker.Length;
            return TryFindNextJsonObject(source, marker, nextIndex, out json, out nextIndex);
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = objectStart; index < source.Length; index++)
        {
            var character = source[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                json = source[objectStart..(index + 1)];
                nextIndex = index + 1;
                return true;
            }
        }

        nextIndex = markerIndex + marker.Length;
        return false;
    }

    private static Uri? ParsePlayerScriptUrl(string html)
    {
        foreach (var property in new[] { "jsUrl", "PLAYER_JS_URL" })
        {
            var searchIndex = 0;
            while ((searchIndex = html.IndexOf($"\"{property}\"", searchIndex, StringComparison.Ordinal)) >= 0)
            {
                var colon = html.IndexOf(':', searchIndex + property.Length + 2);
                if (colon < 0)
                {
                    break;
                }

                var quoteStart = colon + 1;
                while (quoteStart < html.Length && char.IsWhiteSpace(html[quoteStart]))
                {
                    quoteStart++;
                }

                if (quoteStart >= html.Length || html[quoteStart] != '"')
                {
                    searchIndex += property.Length + 2;
                    continue;
                }

                if (TryReadJsonString(html, quoteStart, out var rawToken, out searchIndex))
                {
                    try
                    {
                        var decoded = JsonSerializer.Deserialize<string>(rawToken);
                        if (!string.IsNullOrWhiteSpace(decoded) &&
                            Uri.TryCreate(YouTubeOrigin, decoded, out var uri) &&
                            uri.Scheme == Uri.UriSchemeHttps &&
                            uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase))
                        {
                            return uri;
                        }
                    }
                    catch (JsonException)
                    {
                        // Try another configuration occurrence.
                    }
                }
            }
        }

        return null;
    }

    private static bool TryReadJsonString(
        string source,
        int quoteStart,
        out string token,
        out int nextIndex)
    {
        token = string.Empty;
        var escaped = false;
        for (var index = quoteStart + 1; index < source.Length; index++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (source[index] == '\\')
            {
                escaped = true;
            }
            else if (source[index] == '"')
            {
                token = source[quoteStart..(index + 1)];
                nextIndex = index + 1;
                return true;
            }
        }

        nextIndex = source.Length;
        return false;
    }

    private static bool TryParseMediaUrl(string? candidate, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) &&
               parsed.Scheme == Uri.UriSchemeHttps &&
               (parsed.Host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
                parsed.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase)) &&
               (uri = parsed) is not null;
    }

    private static bool TryParseCaptionUrl(string? candidate, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) &&
               parsed.Scheme == Uri.UriSchemeHttps &&
               (parsed.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                parsed.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)) &&
               parsed.AbsolutePath.Equals("/api/timedtext", StringComparison.OrdinalIgnoreCase) &&
               (uri = parsed) is not null;
    }

    private static Uri? ParseThumbnail(JsonElement details)
    {
        if (!details.TryGetProperty("thumbnail", out var thumbnail) ||
            !thumbnail.TryGetProperty("thumbnails", out var thumbnails) ||
            thumbnails.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return YouTubeThumbnailSelector.SelectBest(thumbnails);
    }

    private static TimeSpan? ParseDuration(string? secondsText)
    {
        if (!long.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static MediaContainer ParseContainer(string mimeType) => mimeType.Split(';')[0].Trim() switch
    {
        "video/mp4" or "audio/mp4" => MediaContainer.Mp4,
        "video/webm" or "audio/webm" => MediaContainer.WebM,
        "video/3gpp" or "audio/3gpp" => MediaContainer.ThreeGp,
        _ => MediaContainer.Unknown
    };

    private static string[] ParseCodecs(string mimeType)
    {
        var marker = mimeType.IndexOf("codecs=\"", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return [];
        }

        var start = marker + "codecs=\"".Length;
        var end = mimeType.IndexOf('"', start);
        return end < 0
            ? []
            : mimeType[start..end]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Collapses provider variants that share a format identifier — loudness-compressed (DRC)
    /// duplicates and dubbed audio tracks — keeping the original-language, uncompressed variant.
    /// Ordering by declared bitrate alone would pick the DRC duplicate, which always reports a
    /// marginally higher bitrate than the original it was derived from.
    /// </summary>
    internal static StreamFormat[] SelectPreferredVariants(IEnumerable<StreamFormat> formats)
    {
        var preferred = new Dictionary<int, StreamFormat>();
        var order = new List<int>();
        foreach (var format in formats)
        {
            if (!preferred.TryGetValue(format.FormatId, out var existing))
            {
                preferred.Add(format.FormatId, format);
                order.Add(format.FormatId);
                continue;
            }

            if (IsPreferredVariant(format, existing))
            {
                preferred[format.FormatId] = format;
            }
        }

        return order.Select(id => preferred[id]).ToArray();
    }

    private static bool IsPreferredVariant(StreamFormat candidate, StreamFormat existing)
    {
        if (candidate.IsDrc != existing.IsDrc)
        {
            return !candidate.IsDrc;
        }

        if (candidate.IsOriginalAudio != existing.IsOriginalAudio)
        {
            return candidate.IsOriginalAudio;
        }

        return (candidate.Bitrate ?? 0) > (existing.Bitrate ?? 0);
    }

    private static string? ReadAudioTrackLanguage(JsonElement element)
    {
        if (!element.TryGetProperty("audioTrack", out var track) || track.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = ReadString(track, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // Identifiers look like "en.4" or "de-DE.3"; only the language part is meaningful here.
        var separator = id.IndexOf('.');
        var language = separator > 0 ? id[..separator] : id;
        return language.Length is > 0 and <= 32 ? language : null;
    }

    private static bool ReadAudioTrackIsOriginal(JsonElement element)
    {
        if (!element.TryGetProperty("audioTrack", out var track) || track.ValueKind != JsonValueKind.Object)
        {
            // A video with a single audio track exposes no audioTrack object at all.
            return true;
        }

        if (ReadBoolean(track, "audioIsDefault"))
        {
            return true;
        }

        var displayName = ReadString(track, "displayName");
        return displayName?.Contains("original", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Detects BT.2020 primaries, which identify an HDR ladder entry even when the provider omits
    /// the "HDR" suffix from the quality label.
    /// </summary>
    private static bool IsWideColourGamut(JsonElement element) =>
        element.TryGetProperty("colorInfo", out var colour) &&
        colour.ValueKind == JsonValueKind.Object &&
        ReadString(colour, "primaries")?.Equals("COLOR_PRIMARIES_BT2020", StringComparison.OrdinalIgnoreCase) == true;

    private static VideoCodec ParseVideoCodec(IEnumerable<string> codecs)
    {
        foreach (var codec in codecs)
        {
            if (codec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase)) return VideoCodec.H264;
            if (codec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase) ||
                codec.StartsWith("vp09", StringComparison.OrdinalIgnoreCase)) return VideoCodec.Vp9;
            if (codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase)) return VideoCodec.Av1;
        }

        return VideoCodec.None;
    }

    private static AudioCodec ParseAudioCodec(IEnumerable<string> codecs)
    {
        foreach (var codec in codecs)
        {
            if (codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase)) return AudioCodec.Aac;
            if (codec.StartsWith("opus", StringComparison.OrdinalIgnoreCase)) return AudioCodec.Opus;
            if (codec.StartsWith("vorbis", StringComparison.OrdinalIgnoreCase)) return AudioCodec.Vorbis;
        }

        return AudioCodec.None;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadString(JsonElement element, string parent, string property) =>
        element.TryGetProperty(parent, out var parentElement)
            ? ReadString(parentElement, property)
            : null;

    private static string? ReadLocalizedText(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var text) || text.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var simpleText = ReadString(text, "simpleText");
        if (!string.IsNullOrWhiteSpace(simpleText))
        {
            return simpleText;
        }

        if (!text.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = runs.EnumerateArray()
            .Select(run => ReadString(run, "text"))
            .Where(value => value is not null)
            .ToArray();
        return parts.Length == 0 ? null : string.Concat(parts);
    }

    private static bool ReadBoolean(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind is JsonValueKind.True;

    private static JsonElement? PlayerMicroformat(JsonElement root)
    {
        if (!root.TryGetProperty("microformat", out var microformat) ||
            microformat.ValueKind != JsonValueKind.Object ||
            !microformat.TryGetProperty("playerMicroformatRenderer", out var renderer) ||
            renderer.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return renderer;
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp) ||
            timestamp.Year is < 2005 or > 2200)
        {
            return null;
        }

        return timestamp;
    }

    private static bool IsSafeLanguageCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 35 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static int? ReadInt32(JsonElement element, string property) =>
        TryReadInt32(element, property, out var value) ? value : null;

    private static bool TryReadInt32(JsonElement element, string property, out int value)
    {
        value = 0;
        return element.TryGetProperty(property, out var jsonValue) &&
               jsonValue.ValueKind == JsonValueKind.Number &&
               jsonValue.TryGetInt32(out value);
    }

    private static long? ReadInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static long? ReadStringInt64(JsonElement element, string property) =>
        long.TryParse(ReadString(element, property), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static int? ReadStringInt32(JsonElement element, string property) =>
        int.TryParse(ReadString(element, property), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static Result<WatchPageData> Failure(string detail) =>
        Result<WatchPageData>.Failure(new TubeForgeError(
            "Extractor.PageChanged",
            "TubeForge could not read this YouTube watch page.",
            detail));

    private static Result<WatchPageData> UnsupportedLive(string code, string message) =>
        Result<WatchPageData>.Failure(new TubeForgeError(code, message));
}
