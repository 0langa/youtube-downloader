using System.Net;
using System.Buffers.Binary;
using TubeForge.Core.Media;
using TubeForge.Core.Networking;
using TubeForge.Core.Settings;
using TubeForge.Core.YouTube;
using TubeForge.YouTube;
using TubeForge.YouTube.Collections;
using TubeForge.YouTube.Diagnostics;

if (args.Length == 2 && args[0].Equals("canary", StringComparison.OrdinalIgnoreCase))
{
    return await RunCanarySetAsync(args[1]);
}

if (args.Length is 2 or 3 && args[0].Equals("collection", StringComparison.OrdinalIgnoreCase))
{
    var maximumItems = 150;
    if (args.Length == 3 &&
        (!int.TryParse(args[2], out maximumItems) || maximumItems is < 1 or > 10_000))
    {
        Console.Error.WriteLine("Collection.InvalidLimit: maximum items must be from 1 through 10000.");
        return 2;
    }

    return await RunCollectionAsync(args[1], maximumItems);
}

if (args.Length != 2 || !args[0].Equals("analyze", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet run --project tools/TubeForge.Smoke -- analyze <youtube-url>");
    Console.Error.WriteLine("  dotnet run --project tools/TubeForge.Smoke -- canary <local-url-list.txt>");
    Console.Error.WriteLine("  dotnet run --project tools/TubeForge.Smoke -- collection <playlist-or-channel-url> [maximum-items]");
    return 2;
}

var parsed = YouTubeUrlParser.ParseVideoId(args[1]);
if (!parsed.IsSuccess)
{
    Console.Error.WriteLine($"{parsed.Error!.Code}: {parsed.Error.Message}");
    return 2;
}

using var handler = CreateSystemNetworkHandler();
using var client = new HttpClient(handler)
{
    Timeout = Timeout.InfiniteTimeSpan
};
var resolver = new YouTubeMetadataResolver(client);
var result = await resolver.ResolveAsync(parsed.Value);
if (!result.IsSuccess)
{
    Console.Error.WriteLine($"{result.Error!.Code}: {result.Error.Message}");
    if (!string.IsNullOrWhiteSpace(result.Error.TechnicalDetail))
    {
        Console.Error.WriteLine(result.Error.TechnicalDetail);
    }

    return 1;
}

var data = result.Value;
var progressive = data.Metadata.Formats.Count(format => format.Kind == StreamKind.Progressive);
var audioOnly = data.Metadata.Formats.Count(format => format.Kind == StreamKind.AudioOnly);
var videoOnly = data.Metadata.Formats.Count(format => format.Kind == StreamKind.VideoOnly);
var maximumCombinedHeight = data.Metadata.Formats
    .Where(format => format.Kind == StreamKind.Progressive)
    .Select(format => format.Height ?? 0)
    .DefaultIfEmpty(0)
    .Max();
var maximumVideoOnlyHeight = data.Metadata.Formats
    .Where(format => format.Kind == StreamKind.VideoOnly)
    .Select(format => format.Height ?? 0)
    .DefaultIfEmpty(0)
    .Max();

Console.WriteLine($"Video ID: {data.Metadata.Id}");
Console.WriteLine($"Title: {data.Metadata.Title}");
Console.WriteLine($"Channel: {data.Metadata.Channel}");
Console.WriteLine($"Duration: {data.Metadata.Duration}");
Console.WriteLine($"Content kind: {data.Metadata.ContentKind}");
Console.WriteLine($"Direct formats: {data.Metadata.Formats.Count} ({progressive} progressive, {audioOnly} audio-only, {videoOnly} video-only)");
Console.WriteLine($"Maximum heights: {maximumCombinedHeight}p combined, {maximumVideoOnlyHeight}p video-only");
Console.WriteLine($"Caption tracks: {data.Metadata.CaptionTracks.Count} ({string.Join(", ", data.Metadata.CaptionTracks.Select(track => track.LanguageCode).Distinct(StringComparer.OrdinalIgnoreCase))})");
Console.WriteLine($"Ciphered formats: {data.CipheredFormatCount}");
Console.WriteLine($"Player script detected: {data.PlayerScriptUrl is not null}");
if (data.Diagnostics is not null)
{
    Console.WriteLine($"Extractor stage: {data.Diagnostics.Stage}");
    Console.WriteLine($"Transform plans/probes: {data.Diagnostics.TransformPlanCount}/{data.Diagnostics.ProbeAttemptCount}");
    if (data.Diagnostics.ClientOutcomes is { Count: > 0 } outcomes)
    {
        Console.WriteLine("Client outcomes: " + string.Join(
            ", ",
            outcomes.Select(outcome => $"{outcome.Client}={outcome.Result}({outcome.FormatCount})")));
    }
}

var best = AdaptiveFormatSelector.SelectBest(data.Metadata.Formats);
if (best is not null)
{
    Console.WriteLine(best.RequiresMuxing
        ? $"Best A/V selection: video {best.Video.FormatId} ({best.Video.Height}p) + audio {best.Audio!.FormatId} ({best.Audio.Bitrate} bps)"
        : $"Best A/V selection: progressive {best.Video.FormatId} ({best.Video.Height}p)");
    Console.WriteLine($"Video URL parameters: {ParameterNames(best.Video.Url)}");
    Console.WriteLine($"Video layout prefix: {await ProbeLayoutAsync(client, best.Video)}");
    if (best.Audio is not null)
    {
        Console.WriteLine($"Audio URL parameters: {ParameterNames(best.Audio.Url)}");
        Console.WriteLine($"Audio layout prefix: {await ProbeLayoutAsync(client, best.Audio)}");
    }
}

return data.Metadata.Formats.Count > 0 ? 0 : 1;

static string ParameterNames(Uri uri) => string.Join(
    ",",
    uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(parameter => parameter.Split('=', 2)[0])
        .Distinct(StringComparer.Ordinal));

static async Task<string> ProbeLayoutAsync(HttpClient client, StreamFormat format)
{
    const int maximumProbeBytes = 64 * 1024;
    using var request = new HttpRequestMessage(HttpMethod.Get, format.Url);
    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, maximumProbeBytes - 1);
    request.Headers.UserAgent.ParseAdd(format.HttpUserAgent ??
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/138.0.0.0");
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    if (!response.IsSuccessStatusCode)
    {
        return $"HTTP {(int)response.StatusCode}";
    }

    await using var stream = await response.Content.ReadAsStreamAsync();
    var bytes = new byte[maximumProbeBytes];
    var length = 0;
    while (length < bytes.Length)
    {
        var read = await stream.ReadAsync(bytes.AsMemory(length));
        if (read == 0)
        {
            break;
        }

        length += read;
    }

    ReadOnlySpan<byte> ebmlMagic = [0x1A, 0x45, 0xDF, 0xA3];
    if (length >= 4 && bytes.AsSpan(0, 4).SequenceEqual(ebmlMagic))
    {
        return ProbeEbmlPrefix(bytes.AsSpan(0, length));
    }

    var boxes = new List<string>();
    var offset = 0;
    while (offset + 8 <= length && boxes.Count < 16)
    {
        var shortSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
        var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
        var headerSize = 8;
        ulong size = shortSize;
        if (shortSize == 1)
        {
            if (offset + 16 > length)
            {
                boxes.Add(type + "(truncated header)");
                break;
            }

            size = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 8, 8));
            headerSize = 16;
        }

        if (shortSize == 0)
        {
            boxes.Add(type + "(to EOF)");
            break;
        }

        if (size < (ulong)headerSize || size > int.MaxValue)
        {
            boxes.Add(type + "(invalid size)");
            break;
        }

        boxes.Add($"{type}({size})");
        if ((ulong)(length - offset) < size)
        {
            boxes[^1] += "…";
            break;
        }

        offset += (int)size;
    }

    return boxes.Count == 0 ? "unrecognized" : string.Join(" → ", boxes);
}

static string ProbeEbmlPrefix(ReadOnlySpan<byte> bytes)
{
    var header = ReadEbmlElement(bytes, 0);
    if (header is null)
    {
        return "EBML (truncated header)";
    }

    var segmentOffset = checked(header.Value.Offset + header.Value.HeaderSize + (int)header.Value.DataSize);
    var segment = ReadEbmlElement(bytes, segmentOffset);
    if (segment is null || segment.Value.Id != 0x18538067)
    {
        return "EBML → Segment (not visible in probe)";
    }

    var names = new List<string> { "EBML", "Segment" };
    var cursor = segment.Value.Offset + segment.Value.HeaderSize;
    while (cursor < bytes.Length && names.Count < 16)
    {
        var child = ReadEbmlElement(bytes, cursor);
        if (child is null)
        {
            names.Add("truncated");
            break;
        }

        var name = child.Value.Id switch
        {
            0x114D9B74 => "SeekHead",
            0x1549A966 => "Info",
            0x1654AE6B => "Tracks",
            0x1F43B675 => "Cluster",
            0x1C53BB6B => "Cues",
            0x1254C367 => "Tags",
            0xEC => "Void",
            _ => $"0x{child.Value.Id:X}"
        };
        names.Add($"{name}({child.Value.DataSize})");
        if (child.Value.DataSize > int.MaxValue ||
            (ulong)child.Value.HeaderSize + child.Value.DataSize > (ulong)(bytes.Length - cursor))
        {
            names[^1] += "…";
            break;
        }

        cursor = checked(cursor + child.Value.HeaderSize + (int)child.Value.DataSize);
    }

    return string.Join(" → ", names);
}

static (ulong Id, int Offset, int HeaderSize, ulong DataSize)? ReadEbmlElement(
    ReadOnlySpan<byte> bytes,
    int offset)
{
    if (offset < 0 || offset >= bytes.Length)
    {
        return null;
    }

    var idWidth = VintWidth(bytes[offset], 4);
    if (idWidth == 0 || offset + idWidth >= bytes.Length)
    {
        return null;
    }

    ulong id = 0;
    for (var index = 0; index < idWidth; index++)
    {
        id = (id << 8) | bytes[offset + index];
    }

    var sizeOffset = offset + idWidth;
    var sizeWidth = VintWidth(bytes[sizeOffset], 8);
    if (sizeWidth == 0 || sizeOffset + sizeWidth > bytes.Length)
    {
        return null;
    }

    var marker = 1 << (8 - sizeWidth);
    ulong size = (ulong)(bytes[sizeOffset] & (marker - 1));
    for (var index = 1; index < sizeWidth; index++)
    {
        size = (size << 8) | bytes[sizeOffset + index];
    }

    var unknown = size == ((1UL << (7 * sizeWidth)) - 1);
    return (id, offset, idWidth + sizeWidth, unknown ? ulong.MaxValue : size);
}

static int VintWidth(byte firstByte, int maximum)
{
    for (var width = 1; width <= maximum; width++)
    {
        if ((firstByte & (0x80 >> (width - 1))) != 0)
        {
            return width;
        }
    }

    return 0;
}

static async Task<int> RunCanarySetAsync(string inputPath)
{
    const long maximumInputBytes = 64 * 1024;
    string[] lines;
    try
    {
        var fullPath = Path.GetFullPath(inputPath);
        var information = new FileInfo(fullPath);
        if (!information.Exists || information.Length is <= 0 or > maximumInputBytes)
        {
            Console.Error.WriteLine("Canary.InputInvalid: canary file is missing, empty, or exceeds 64 KiB.");
            return 2;
        }

        lines = await File.ReadAllLinesAsync(fullPath);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                      ArgumentException or NotSupportedException)
    {
        Console.Error.WriteLine($"Canary.ReadFailed: {exception.GetType().Name}");
        return 2;
    }

    var parsedList = CanaryListParser.Parse(lines);
    if (!parsedList.IsSuccess)
    {
        Console.Error.WriteLine($"{parsedList.Error!.Code}: {parsedList.Error.Message}");
        return 2;
    }

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    using var handler = CreateSystemNetworkHandler();
    using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    var resolver = new YouTubeMetadataResolver(client);
    var failures = 0;
    var videoIds = parsedList.Value;
    for (var index = 0; index < videoIds.Count; index++)
    {
        if (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Canary.Cancelled: remaining checks skipped.");
            return 1;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var resolved = await resolver.ResolveAsync(videoIds[index], timeout.Token);
        if (!resolved.IsSuccess)
        {
            failures++;
            Console.WriteLine($"FAIL {index + 1}: {resolved.Error!.Code}");
            continue;
        }

        var formats = resolved.Value.Metadata.Formats;
        var progressive = formats.Count(format => format.Kind == StreamKind.Progressive);
        var audio = formats.Count(format => format.Kind == StreamKind.AudioOnly);
        var video = formats.Count(format => format.Kind == StreamKind.VideoOnly);
        var best = AdaptiveFormatSelector.SelectBest(formats);
        if (formats.Count == 0 || best is null)
        {
            failures++;
            Console.WriteLine($"FAIL {index + 1}: Extractor.NoUsableOutput");
            continue;
        }

        var stage = resolved.Value.Diagnostics?.Stage ?? "UnknownStage";
        var output = best.RequiresMuxing ? best.OutputContainer.ToString() + "/mux" : "progressive";
        Console.WriteLine(
            $"PASS {index + 1}: stage={stage}; formats={formats.Count}; p/a/v={progressive}/{audio}/{video}; output={output}");
    }

    Console.WriteLine($"Canaries: {videoIds.Count - failures}/{videoIds.Count} passed.");
    return failures == 0 ? 0 : 1;
}

static async Task<int> RunCollectionAsync(string input, int maximumItems)
{
    var parsed = YouTubeCollectionUrlParser.Parse(input);
    if (!parsed.IsSuccess)
    {
        Console.Error.WriteLine($"{parsed.Error!.Code}: {parsed.Error.Message}");
        return 2;
    }

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    using var handler = CreateSystemNetworkHandler();
    using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    var resolver = new YouTubeCollectionResolver(client);
    var result = await resolver.ResolveAsync(parsed.Value, maximumItems, timeout.Token);
    if (!result.IsSuccess)
    {
        Console.Error.WriteLine($"{result.Error!.Code}: {result.Error.Message}");
        if (!string.IsNullOrWhiteSpace(result.Error.TechnicalDetail))
        {
            Console.Error.WriteLine(result.Error.TechnicalDetail);
        }

        return 1;
    }

    var data = result.Value;
    var knownIndexes = data.Items.Count(item => item.Index is > 0);
    var uniqueVideoIds = data.Items.Select(item => item.VideoId).Distinct().Count();
    Console.WriteLine($"Kind: {data.Source.Kind}");
    Console.WriteLine($"Items: {data.Items.Count}");
    Console.WriteLine($"Pages: {data.PagesRead}");
    Console.WriteLine($"Unique IDs: {uniqueVideoIds}");
    Console.WriteLine($"Known indexes: {knownIndexes}");
    Console.WriteLine($"Truncated: {data.IsTruncated}");
    return data.Items.Count > 0 && uniqueVideoIds == data.Items.Count ? 0 : 1;
}

static SocketsHttpHandler CreateSystemNetworkHandler() => new()
{
    AutomaticDecompression = DecompressionMethods.All,
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 5,
    ConnectTimeout = TimeSpan.FromSeconds(10),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    Proxy = new ConfigurableWebProxy(new NetworkProxyConfiguration(NetworkProxyMode.System)),
    UseProxy = true
};
