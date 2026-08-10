using System.Globalization;
using System.Text;
using TubeForge.Core.Errors;
using TubeForge.Core.Results;

namespace TubeForge.Core.Media;

public static class SubRipTimelineTrimmer
{
    private const int MaximumCharacters = 16 * 1024 * 1024;

    public static Result<string> Trim(string content, MediaTrimRange trim)
    {
        if (!trim.IsValid)
        {
            return Failure();
        }

        return Transform(content, (start, end) =>
        {
            var clippedStart = start < trim.Start ? trim.Start : start;
            var clippedEnd = end > trim.End ? trim.End : end;
            return clippedEnd <= clippedStart
                ? null
                : (clippedStart - trim.Start, clippedEnd - trim.Start);
        });
    }

    public static Result<string> RemoveSegments(
        string content,
        IReadOnlyList<MediaTrimRange> removedSegments)
    {
        ArgumentNullException.ThrowIfNull(removedSegments);
        if (removedSegments.Count > 1_000 || removedSegments.Any(range => !range.IsValid) ||
            removedSegments.Zip(removedSegments.Skip(1)).Any(pair => pair.First.End > pair.Second.Start))
        {
            return Failure();
        }

        return Transform(content, (start, end) =>
        {
            var cursor = start;
            TimeSpan? firstKept = null;
            TimeSpan? lastKept = null;
            foreach (var removed in removedSegments)
            {
                if (removed.End <= cursor)
                {
                    continue;
                }

                if (removed.Start >= end)
                {
                    break;
                }

                if (removed.Start > cursor)
                {
                    firstKept ??= cursor;
                    lastKept = removed.Start < end ? removed.Start : end;
                }

                if (removed.End > cursor)
                {
                    cursor = removed.End;
                }

                if (cursor >= end)
                {
                    break;
                }
            }

            if (cursor < end)
            {
                firstKept ??= cursor;
                lastKept = end;
            }

            if (firstKept is not { } keptStart || lastKept is not { } keptEnd || keptEnd <= keptStart)
            {
                return null;
            }

            var adjustedStart = CollapseRemovedTime(keptStart, removedSegments);
            var adjustedEnd = CollapseRemovedTime(keptEnd, removedSegments);
            return adjustedEnd <= adjustedStart ? null : (adjustedStart, adjustedEnd);
        });
    }

    private static Result<string> Transform(
        string content,
        Func<TimeSpan, TimeSpan, (TimeSpan Start, TimeSpan End)?> transform)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > MaximumCharacters)
        {
            return Failure();
        }

        var normalized = content.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
        var blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var output = new StringBuilder(Math.Min(content.Length, MaximumCharacters));
        var outputIndex = 0;
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            if (lines.Length < 3 || !int.TryParse(lines[0].Trim(), out var sourceIndex) || sourceIndex <= 0 ||
                !TryParseTiming(lines[1], out var start, out var end) || end <= start)
            {
                return Failure();
            }

            var adjusted = transform(start, end);
            if (adjusted is not { } timeline)
            {
                continue;
            }

            outputIndex++;
            output.Append(outputIndex).Append("\r\n")
                .Append(Format(timeline.Start))
                .Append(" --> ")
                .Append(Format(timeline.End))
                .Append("\r\n");
            for (var lineIndex = 2; lineIndex < lines.Length; lineIndex++)
            {
                if (lines[lineIndex].Any(character => char.IsControl(character) && character != '\t'))
                {
                    return Failure();
                }

                output.Append(lines[lineIndex]).Append("\r\n");
            }

            output.Append("\r\n");
        }

        return outputIndex > 0 ? Result<string>.Success(output.ToString()) : Failure();
    }

    private static TimeSpan CollapseRemovedTime(
        TimeSpan value,
        IReadOnlyList<MediaTrimRange> removedSegments)
    {
        var removedDuration = TimeSpan.Zero;
        foreach (var removed in removedSegments)
        {
            if (value <= removed.Start)
            {
                break;
            }

            removedDuration += value >= removed.End
                ? removed.Duration
                : value - removed.Start;
            if (value < removed.End)
            {
                break;
            }
        }

        return value - removedDuration;
    }

    private static bool TryParseTiming(string value, out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;
        var separator = value.IndexOf(" --> ", StringComparison.Ordinal);
        return separator > 0 && separator == value.LastIndexOf(" --> ", StringComparison.Ordinal) &&
            TryParseTime(value[..separator], out start) &&
            TryParseTime(value[(separator + 5)..], out end);
    }

    private static bool TryParseTime(string value, out TimeSpan time)
    {
        time = default;
        var parts = value.Trim().Split([':', ',']);
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) ||
            hours is < 0 or > 167 || minutes is < 0 or > 59 || seconds is < 0 or > 59 ||
            milliseconds is < 0 or > 999)
        {
            return false;
        }

        time = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) +
            TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    private static string Format(TimeSpan value)
    {
        var totalHours = (long)value.TotalHours;
        return $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
    }

    private static Result<string> Failure() => Result<string>.Failure(new TubeForgeError(
        "Caption.InvalidSubRipTimeline",
        "TubeForge could not safely align the selected subtitles to the edited media timeline."));
}
