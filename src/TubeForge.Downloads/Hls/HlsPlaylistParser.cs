using System.Globalization;
using TubeForge.Core.Errors;
using TubeForge.Core.Results;

namespace TubeForge.Downloads.Hls;

public static class HlsPlaylistParser
{
    public const int MaximumCharacters = 8 * 1024 * 1024;
    public const int MaximumSegments = 5_000;
    private const int MaximumLines = 20_000;

    public static Result<HlsPlaylist> Parse(string? content, Uri playlistUri)
    {
        ArgumentNullException.ThrowIfNull(playlistUri);
        if (!IsSafeUri(playlistUri) || string.IsNullOrWhiteSpace(content) ||
            content.Length > MaximumCharacters || content.Any(character => character == '\0'))
        {
            return Invalid("The HLS playlist is empty, oversized, or has an unsafe source URI.");
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        if (lines.Length is 0 or > MaximumLines || lines[0] != "#EXTM3U")
        {
            return Invalid("The HLS playlist header or line count is invalid.");
        }

        var variants = new List<HlsVariant>();
        var segments = new List<HlsSegment>();
        var sequence = 0L;
        var nextSequence = 0L;
        var targetDuration = TimeSpan.FromSeconds(6);
        TimeSpan? pendingDuration = null;
        long? pendingBandwidth = null;
        Uri? initializationUri = null;
        var endList = false;

        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line[(line.IndexOf(':') + 1)..]);
                if (!attributes.TryGetValue("METHOD", out var method) || method != "NONE")
                {
                    return Failure(
                        "Hls.EncryptedUnsupported",
                        "Encrypted or DRM-protected HLS playlists are intentionally unsupported.");
                }

                continue;
            }

            if (line.StartsWith("#EXT-X-BYTERANGE", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-I-FRAMES-ONLY", StringComparison.Ordinal))
            {
                return Failure("Hls.UnsupportedPlaylist", "This HLS playlist uses an unsupported segment mode.");
            }

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                if (!long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], NumberStyles.None,
                        CultureInfo.InvariantCulture, out sequence) || sequence < 0)
                {
                    return Invalid("The HLS media sequence is invalid.");
                }

                nextSequence = sequence;
                continue;
            }

            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                if (!int.TryParse(line["#EXT-X-TARGETDURATION:".Length..], NumberStyles.None,
                        CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 120)
                {
                    return Invalid("The HLS target duration is invalid.");
                }

                targetDuration = TimeSpan.FromSeconds(seconds);
                continue;
            }

            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line["#EXT-X-STREAM-INF:".Length..]);
                if (!attributes.TryGetValue("BANDWIDTH", out var value) ||
                    !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bandwidth) ||
                    bandwidth is <= 0 or > 1_000_000_000)
                {
                    return Invalid("The HLS variant bandwidth is invalid.");
                }

                pendingBandwidth = bandwidth;
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var value = line["#EXTINF:".Length..].Split(',', 2)[0];
                if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                        out var seconds) || !double.IsFinite(seconds) || seconds is <= 0 or > 120)
                {
                    return Invalid("An HLS segment duration is invalid.");
                }

                pendingDuration = TimeSpan.FromMilliseconds(Math.Ceiling(seconds * 1_000));
                continue;
            }

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line["#EXT-X-MAP:".Length..]);
                if (!attributes.TryGetValue("URI", out var value) ||
                    !TryResolve(playlistUri, value, out initializationUri))
                {
                    return Invalid("The HLS initialization segment URI is invalid.");
                }

                continue;
            }

            if (line == "#EXT-X-ENDLIST")
            {
                endList = true;
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            if (!TryResolve(playlistUri, line, out var uri))
            {
                return Invalid("An HLS child URI is invalid.");
            }

            if (pendingBandwidth is { } bandwidthValue)
            {
                variants.Add(new HlsVariant(uri, bandwidthValue));
                pendingBandwidth = null;
                continue;
            }

            if (pendingDuration is not { } duration)
            {
                return Invalid("An HLS media URI has no segment duration.");
            }

            if (segments.Count >= MaximumSegments)
            {
                return Failure("Hls.PlaylistTooLarge", "The HLS playlist contains too many segments.");
            }

            segments.Add(new HlsSegment(nextSequence++, uri, duration, initializationUri));
            pendingDuration = null;
        }

        if (pendingBandwidth is not null || pendingDuration is not null ||
            variants.Count > 0 && segments.Count > 0 || variants.Count == 0 && segments.Count == 0)
        {
            return Invalid("The HLS playlist structure is incomplete or ambiguous.");
        }

        return Result<HlsPlaylist>.Success(new HlsPlaylist(
            variants.Count > 0,
            endList,
            targetDuration,
            variants.OrderByDescending(variant => variant.Bandwidth).ToArray(),
            segments));
    }

    private static Dictionary<string, string> ParseAttributes(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        while (index < value.Length)
        {
            var equals = value.IndexOf('=', index);
            if (equals <= index)
            {
                return [];
            }

            var key = value[index..equals].Trim();
            index = equals + 1;
            string item;
            if (index < value.Length && value[index] == '"')
            {
                var end = value.IndexOf('"', index + 1);
                if (end < 0)
                {
                    return [];
                }

                item = value[(index + 1)..end];
                index = end + 1;
            }
            else
            {
                var comma = value.IndexOf(',', index);
                var end = comma < 0 ? value.Length : comma;
                item = value[index..end].Trim();
                index = end;
            }

            if (key.Length == 0 || item.Length == 0 || !result.TryAdd(key, item))
            {
                return [];
            }

            if (index < value.Length)
            {
                if (value[index] != ',')
                {
                    return [];
                }

                index++;
            }
        }

        return result;
    }

    private static bool TryResolve(Uri parent, string value, out Uri uri)
    {
        uri = null!;
        if (value.Length is not (> 0 and <= 8_192) ||
            !Uri.TryCreate(parent, value, out var resolved) || !IsSafeUri(resolved))
        {
            return false;
        }

        uri = resolved;
        return true;
    }

    private static bool IsSafeUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

    private static Result<HlsPlaylist> Invalid(string detail) =>
        Failure("Hls.InvalidPlaylist", "TubeForge could not safely parse this HLS playlist.", detail);

    private static Result<HlsPlaylist> Failure(string code, string message, string? detail = null) =>
        Result<HlsPlaylist>.Failure(new TubeForgeError(code, message, detail));
}
