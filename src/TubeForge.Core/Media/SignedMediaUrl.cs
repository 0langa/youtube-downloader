namespace TubeForge.Core.Media;

/// <summary>
/// Reads the expiry stamp that the media provider embeds in every signed stream URL. A URL that
/// has expired is rejected with HTTP 403 regardless of how much of the transfer already
/// succeeded, so anything holding a URL for later use has to check it first.
/// </summary>
public static class SignedMediaUrl
{
    /// <summary>
    /// Treat a URL as unusable slightly before its stated expiry: a transfer that starts in the
    /// last moments of the window fails partway through instead of before it begins.
    /// </summary>
    public static readonly TimeSpan SafetyMargin = TimeSpan.FromMinutes(10);

    public static DateTimeOffset? GetExpiry(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri)
        {
            return null;
        }

        var value = ReadQueryValue(url.Query, "expire");
        if (value is null || !long.TryParse(value, out var unixSeconds))
        {
            return null;
        }

        // Reject stamps outside a sane window rather than trusting arbitrary provider input.
        if (unixSeconds is < 1_000_000_000 or > 99_999_999_999)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }

    /// <summary>
    /// True when the URL carries no expiry stamp, or its stamp is far enough in the future for a
    /// transfer to start. A URL without a stamp is assumed usable: the caller finds out from the
    /// response, which is the same outcome as before this check existed.
    /// </summary>
    public static bool IsUsable(Uri url, DateTimeOffset now)
    {
        var expiry = GetExpiry(url);
        return expiry is null || expiry.Value - SafetyMargin > now;
    }

    private static string? ReadQueryValue(string query, string name)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (pair.AsSpan(0, separator).SequenceEqual(name))
            {
                return pair[(separator + 1)..];
            }
        }

        return null;
    }
}
