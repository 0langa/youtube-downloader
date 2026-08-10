using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TubeForge.Core.Errors;
using TubeForge.Core.Media;
using TubeForge.Core.Networking;
using TubeForge.Core.Results;
using TubeForge.Downloads.Queue;

namespace TubeForge.Downloads.Hls;

public sealed class HlsCaptureEngine
{
    private const int BufferSize = 128 * 1024;
    private const long MaximumPlaylistBytes = 8 * 1024 * 1024;
    private const long MaximumSegmentBytes = 64 * 1024 * 1024;
    private const long MaximumInitializationBytes = 16 * 1024 * 1024;
    private const int MaximumJournalBytes = 16 * 1024 * 1024;
    private const int MaximumCapturedSegments = 100_000;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly DownloadUriPolicy _uriPolicy;
    private readonly HostRequestGate _hostRequestGate;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public HlsCaptureEngine(
        HttpClient httpClient,
        HostRequestGate hostRequestGate,
        DownloadUriPolicy? uriPolicy = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _hostRequestGate = hostRequestGate ?? throw new ArgumentNullException(nameof(hostRequestGate));
        _uriPolicy = uriPolicy ?? DownloadUriPolicy.YouTubeMediaOnly;
        _delay = delay ?? Task.Delay;
    }

    public async Task<Result<DownloadReceipt>> CaptureAsync(
        HlsCaptureRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return Result<DownloadReceipt>.Failure(validation);
        }

        var destination = Path.GetFullPath(request.DestinationPath);
        var partsDirectory = PartsDirectory(destination);
        var journalPath = JournalPath(destination);
        try
        {
            if (File.Exists(destination))
            {
                var length = new FileInfo(destination).Length;
                return length > 0
                    ? Result<DownloadReceipt>.Success(new DownloadReceipt(destination, length, Resumed: true))
                    : Failure("Hls.InvalidOutput", "The recovered HLS capture is empty.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.CreateDirectory(partsDirectory);
            var journalResult = await LoadJournalAsync(journalPath, partsDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (!journalResult.IsSuccess)
            {
                return Result<DownloadReceipt>.Failure(journalResult.Error!);
            }

            var journal = journalResult.Value ?? new HlsCaptureJournal
            {
                StartedAtUtc = DateTimeOffset.UtcNow
            };
            var resumed = journal.Parts.Count > 0;
            var stopwatch = Stopwatch.StartNew();
            var initialBytes = journal.TotalBytes;
            var complete = false;

            while (!complete)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var playlistResult = await FetchMediaPlaylistAsync(
                    request.ManifestUri,
                    request.HttpUserAgent,
                    cancellationToken).ConfigureAwait(false);
                if (!playlistResult.IsSuccess)
                {
                    return Result<DownloadReceipt>.Failure(playlistResult.Error!);
                }

                var playlist = playlistResult.Value;
                if (journal.Parts.Count > 0 && playlist.Segments.Count > 0 &&
                    playlist.Segments[0].Sequence > journal.Parts[^1].Sequence + 1)
                {
                    return Failure(
                        "Hls.SegmentsExpired",
                        "The live playlist advanced past segments needed to resume this capture.");
                }

                var availableSegments = journal.Parts.Count == 0 && !playlist.IsEndList
                    ? playlist.Segments.TakeLast(1)
                    : playlist.Segments;
                foreach (var segment in availableSegments)
                {
                    if (journal.Parts.Count > 0 && segment.Sequence <= journal.Parts[^1].Sequence)
                    {
                        continue;
                    }

                    if (journal.Parts.Count >= MaximumCapturedSegments)
                    {
                        return Failure("Hls.CaptureTooLong", "The live capture exceeded its safe segment limit.");
                    }

                    var partResult = await DownloadPartAsync(
                        partsDirectory,
                        segment,
                        journal.LastInitializationHash,
                        request.Options.MaximumBytes - journal.TotalBytes,
                        request.HttpUserAgent,
                        cancellationToken).ConfigureAwait(false);
                    if (!partResult.IsSuccess)
                    {
                        if (partResult.Error?.Code == "Hls.SizeLimitReached" && journal.Parts.Count > 0)
                        {
                            complete = true;
                            break;
                        }

                        return Result<DownloadReceipt>.Failure(partResult.Error!);
                    }

                    var part = partResult.Value;
                    journal.Parts.Add(part.Entry);
                    journal.TotalBytes += part.Entry.Length;
                    journal.CapturedDurationTicks += segment.Duration.Ticks;
                    journal.LastInitializationHash = part.InitializationHash;
                    await SaveJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
                    var elapsed = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                    var speed = Math.Max(0, journal.TotalBytes - initialBytes) / elapsed;
                    var remainingBytes = request.Options.MaximumBytes - journal.TotalBytes;
                    progress?.Report(new DownloadProgress(
                        journal.TotalBytes,
                        request.Options.MaximumBytes,
                        speed,
                        speed > 0 ? TimeSpan.FromSeconds(remainingBytes / speed) : null));

                    if (journal.TotalBytes >= request.Options.MaximumBytes ||
                        TimeSpan.FromTicks(journal.CapturedDurationTicks) >= request.Options.MaximumDuration)
                    {
                        complete = true;
                        break;
                    }
                }

                complete |= playlist.IsEndList;
                if (!complete)
                {
                    var pollDelay = TimeSpan.FromMilliseconds(Math.Clamp(
                        playlist.TargetDuration.TotalMilliseconds / 2,
                        1_000,
                        10_000));
                    await _delay(pollDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            if (journal.Parts.Count == 0)
            {
                return Failure("Hls.NoSegments", "The live playlist did not provide any downloadable segments.");
            }

            var assembled = await AssembleAsync(destination, partsDirectory, journal, cancellationToken)
                .ConfigureAwait(false);
            return assembled.IsSuccess
                ? Result<DownloadReceipt>.Success(new DownloadReceipt(
                    destination,
                    assembled.Value,
                    resumed))
                : Result<DownloadReceipt>.Failure(assembled.Error!);
        }
        catch (OperationCanceledException)
        {
            return Failure("Operation.Cancelled", "The live capture was paused or cancelled.");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure("Hls.WriteFailed", "TubeForge cannot write the live capture.", exception.GetType().Name);
        }
        catch (IOException exception)
        {
            return Failure("Hls.WriteFailed", "TubeForge could not safely write the live capture.", exception.GetType().Name);
        }
    }

    public static void Cleanup(string destinationPath)
    {
        var destination = Path.GetFullPath(destinationPath);
        TryDelete(JournalPath(destination));
        TryDelete(JournalPath(destination) + ".new");
        TryDelete(JournalPath(destination) + ".bak");
        var parts = PartsDirectory(destination);
        if (Directory.Exists(parts))
        {
            Directory.Delete(parts, recursive: true);
        }
    }

    private async Task<Result<HlsPlaylist>> FetchMediaPlaylistAsync(
        Uri manifestUri,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = await FetchMediaPlaylistOnceAsync(manifestUri, userAgent, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess || result.Error?.IsTransient != true || attempt == 3)
            {
                return result;
            }

            await _delay(
                result.Error.RetryAfter ?? TimeSpan.FromMilliseconds(250 * attempt),
                cancellationToken).ConfigureAwait(false);
        }

        throw new UnreachableException();
    }

    private async Task<Result<HlsPlaylist>> FetchMediaPlaylistOnceAsync(
        Uri manifestUri,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var manifestResult = await FetchPlaylistAsync(manifestUri, userAgent, cancellationToken)
            .ConfigureAwait(false);
        if (!manifestResult.IsSuccess || !manifestResult.Value.IsMaster)
        {
            return manifestResult;
        }

        var variant = manifestResult.Value.Variants.FirstOrDefault(candidate => _uriPolicy.IsAllowed(candidate.Uri));
        return variant is null
            ? FailurePlaylist("Hls.UnsafeUri", "The HLS master playlist contains no trusted media variant.")
            : await FetchPlaylistAsync(variant.Uri, userAgent, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<HlsPlaylist>> FetchPlaylistAsync(
        Uri uri,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (!_uriPolicy.IsAllowed(uri))
        {
            return FailurePlaylist("Hls.UnsafeUri", "The HLS playlist points outside trusted YouTube media hosts.");
        }

        using var lease = await _hostRequestGate.EnterAsync(uri, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!HttpUserAgentHeader.TryApply(request, userAgent))
        {
            return FailurePlaylist("Hls.InvalidUserAgent", "The HLS request contained an invalid HTTP user agent.");
        }

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            if (!_uriPolicy.IsAllowed(finalUri))
            {
                return FailurePlaylist("Hls.UnsafeRedirect", "The HLS playlist redirected to an untrusted location.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return PlaylistHttpFailure(response, uri);
            }

            if (response.Content.Headers.ContentLength is > MaximumPlaylistBytes)
            {
                return FailurePlaylist("Hls.PlaylistTooLarge", "The HLS playlist exceeds its safe size limit.");
            }

            var text = await ReadBoundedTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return HlsPlaylistParser.Parse(text, finalUri);
        }
        catch (HttpRequestException exception)
        {
            return FailurePlaylist("Hls.NetworkFailed", "TubeForge could not fetch the HLS playlist.", exception.GetType().Name, true);
        }
        catch (IOException exception)
        {
            return FailurePlaylist("Hls.PlaylistTooLarge", "TubeForge could not safely read the HLS playlist.", exception.GetType().Name);
        }
    }

    private async Task<Result<DownloadedPart>> DownloadPartAsync(
        string partsDirectory,
        HlsSegment segment,
        string? previousInitializationHash,
        long maximumBytes,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0)
        {
            return FailurePart("Hls.SizeLimitReached", "The live capture reached its configured size limit.");
        }

        if (!_uriPolicy.IsAllowed(segment.Uri) ||
            segment.InitializationUri is { } initialization && !_uriPolicy.IsAllowed(initialization))
        {
            return FailurePart("Hls.UnsafeUri", "An HLS segment points outside trusted YouTube media hosts.");
        }

        var initializationHash = segment.InitializationUri is null
            ? null
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(segment.InitializationUri.AbsoluteUri)));
        var needsInitialization = initializationHash is not null && initializationHash != previousInitializationHash;
        var fileName = $"{segment.Sequence:D20}.bin";
        var path = Path.Combine(partsDirectory, fileName);
        var temporary = path + ".new";
        TryDelete(temporary);
        try
        {
            await using var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            if (needsInitialization)
            {
                var initResult = await AppendUriWithRetryAsync(
                    segment.InitializationUri!,
                    output,
                    Math.Min(maximumBytes, MaximumInitializationBytes),
                    userAgent,
                    cancellationToken).ConfigureAwait(false);
                if (!initResult.IsSuccess)
                {
                    return Result<DownloadedPart>.Failure(initResult.Error!);
                }
            }

            var remaining = maximumBytes - output.Length;
            if (remaining <= 0)
            {
                return FailurePart("Hls.SizeLimitReached", "The live capture reached its configured size limit.");
            }

            var segmentResult = await AppendUriWithRetryAsync(
                segment.Uri,
                output,
                Math.Min(remaining, MaximumSegmentBytes),
                userAgent,
                cancellationToken).ConfigureAwait(false);
            if (!segmentResult.IsSuccess)
            {
                return Result<DownloadedPart>.Failure(segmentResult.Error!);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if (output.Length <= 0 || output.Length > maximumBytes)
            {
                return FailurePart("Hls.SizeLimitReached", "The live capture reached its configured size limit.");
            }

            var length = output.Length;
            output.Close();
            File.Move(temporary, path);
            return Result<DownloadedPart>.Success(new DownloadedPart(
                new HlsPart(segment.Sequence, fileName, length, segment.Duration.Ticks),
                initializationHash));
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task<Result<long>> AppendUriWithRetryAsync(
        Uri uri,
        Stream output,
        long maximumBytes,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var start = output.Position;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = await AppendUriAsync(uri, output, maximumBytes, userAgent, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess || result.Error?.IsTransient != true || attempt == 3)
            {
                return result;
            }

            output.SetLength(start);
            output.Position = start;
            await _delay(
                result.Error.RetryAfter ?? TimeSpan.FromMilliseconds(250 * attempt),
                cancellationToken).ConfigureAwait(false);
        }

        throw new UnreachableException();
    }

    private async Task<Result<long>> AppendUriAsync(
        Uri uri,
        Stream output,
        long maximumBytes,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        using var lease = await _hostRequestGate.EnterAsync(uri, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!HttpUserAgentHeader.TryApply(request, userAgent))
        {
            return FailureLong("Hls.InvalidUserAgent", "The HLS request contained an invalid HTTP user agent.");
        }

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            if (!_uriPolicy.IsAllowed(finalUri))
            {
                return FailureLong("Hls.UnsafeRedirect", "An HLS segment redirected to an untrusted location.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return SegmentHttpFailure(response, uri);
            }

            if (response.Content.Headers.ContentLength is > 0 and var contentLength && contentLength > maximumBytes)
            {
                return FailureLong("Hls.SizeLimitReached", "The live capture reached its configured size limit.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[BufferSize];
            long written = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return written > 0
                        ? Result<long>.Success(written)
                        : FailureLong("Hls.EmptySegment", "An HLS segment was empty.");
                }

                if (written > maximumBytes - read)
                {
                    return FailureLong("Hls.SizeLimitReached", "The live capture reached its configured size limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
            }
        }
        catch (HttpRequestException exception)
        {
            return FailureLong("Hls.NetworkFailed", "TubeForge could not download an HLS segment.", exception.GetType().Name, true);
        }
    }

    private static async Task<Result<long>> AssembleAsync(
        string destination,
        string partsDirectory,
        HlsCaptureJournal journal,
        CancellationToken cancellationToken)
    {
        var temporary = destination + ".new";
        TryDelete(temporary);
        try
        {
            await using var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            foreach (var part in journal.Parts.OrderBy(part => part.Sequence))
            {
                var path = Path.Combine(partsDirectory, part.FileName);
                await using var input = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (input.Length != part.Length)
                {
                    return FailureLong("Hls.InvalidJournal", "The saved HLS capture journal does not match its segment files.");
                }

                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            var length = output.Length;
            output.Close();
            File.Move(temporary, destination);
            return Result<long>.Success(length);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async Task<Result<HlsCaptureJournal?>> LoadJournalAsync(
        string journalPath,
        string partsDirectory,
        CancellationToken cancellationToken)
    {
        Result<HlsCaptureJournal?>? failure = null;
        foreach (var candidate in new[] { journalPath, journalPath + ".new", journalPath + ".bak" })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var result = await LoadJournalCandidateAsync(candidate, partsDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return result;
            }

            failure ??= result;
        }

        return failure ?? Result<HlsCaptureJournal?>.Success(null);
    }

    private static async Task<Result<HlsCaptureJournal?>> LoadJournalCandidateAsync(
        string path,
        string partsDirectory,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > MaximumJournalBytes)
        {
            return FailureJournal("Hls.InvalidJournal", "The saved HLS capture journal is oversized.");
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var journal = await JsonSerializer.DeserializeAsync<HlsCaptureJournal>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            if (journal is null || journal.SchemaVersion != 1 || journal.StartedAtUtc == default ||
                journal.TotalBytes < 0 || journal.CapturedDurationTicks < 0 ||
                journal.TotalBytes > LiveCaptureOptions.MaximumAllowedBytes ||
                journal.Parts.Count > MaximumCapturedSegments ||
                journal.Parts.Any(part => part.Sequence < 0 || part.Length is <= 0 or > MaximumSegmentBytes + MaximumInitializationBytes ||
                    part.DurationTicks <= 0 ||
                    part.FileName != $"{part.Sequence:D20}.bin" ||
                    !File.Exists(Path.Combine(partsDirectory, part.FileName))) ||
                !journal.Parts.Select(part => part.Sequence).SequenceEqual(
                    journal.Parts.Select(part => part.Sequence).Order()) ||
                journal.Parts.Sum(part => part.Length) != journal.TotalBytes ||
                journal.Parts.Sum(part => part.DurationTicks) != journal.CapturedDurationTicks ||
                journal.LastInitializationHash is not null &&
                    (journal.LastInitializationHash.Length != 64 ||
                     !journal.LastInitializationHash.All(Uri.IsHexDigit)))
            {
                return FailureJournal("Hls.InvalidJournal", "The saved HLS capture journal is invalid.");
            }

            return Result<HlsCaptureJournal?>.Success(journal);
        }
        catch (JsonException)
        {
            return FailureJournal("Hls.InvalidJournal", "The saved HLS capture journal is malformed.");
        }
        catch (OverflowException)
        {
            return FailureJournal("Hls.InvalidJournal", "The saved HLS capture journal contains invalid totals.");
        }
    }

    private static async Task SaveJournalAsync(
        string path,
        HlsCaptureJournal journal,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".new";
        TryDelete(temporary);
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, journal, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            TryDelete(path + ".bak");
            File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporary, path);
        }
    }

    private static async Task<string> ReadBoundedTextAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var buffer = new char[8_192];
        var builder = new StringBuilder();
        while (builder.Length <= HlsPlaylistParser.MaximumCharacters)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return builder.ToString();
            }

            builder.Append(buffer, 0, read);
        }

        throw new IOException("The HLS playlist exceeded its safe size limit.");
    }

    private TubeForgeError? ValidateRequest(HlsCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_uriPolicy.IsAllowed(request.ManifestUri) || !request.Options.IsValid ||
            string.IsNullOrWhiteSpace(request.DestinationPath) || !Path.IsPathFullyQualified(request.DestinationPath))
        {
            return new TubeForgeError("Hls.InvalidRequest", "The live capture settings are invalid.");
        }

        try
        {
            var destination = Path.GetFullPath(request.DestinationPath);
            return string.IsNullOrWhiteSpace(Path.GetDirectoryName(destination)) ||
                   destination.EndsWith(Path.DirectorySeparatorChar)
                ? new TubeForgeError("Hls.InvalidRequest", "The live capture destination is invalid.")
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new TubeForgeError("Hls.InvalidRequest", "The live capture destination is invalid.");
        }
    }

    private Result<HlsPlaylist> PlaylistHttpFailure(HttpResponseMessage response, Uri uri)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = HttpRetryAfterParser.Parse(response.Headers);
            _hostRequestGate.Defer(uri, retryAfter);
            return FailurePlaylist("Network.RateLimited", "The HLS service rate-limited this request.", null, true, retryAfter);
        }

        return FailurePlaylist(
            response.StatusCode == HttpStatusCode.NotFound ? "Hls.ManifestExpired" : "Hls.NetworkFailed",
            response.StatusCode == HttpStatusCode.NotFound
                ? "The HLS playlist expired before capture completed."
                : "TubeForge could not fetch the HLS playlist.",
            null,
            (int)response.StatusCode >= 500);
    }

    private Result<long> SegmentHttpFailure(HttpResponseMessage response, Uri uri)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = HttpRetryAfterParser.Parse(response.Headers);
            _hostRequestGate.Defer(uri, retryAfter);
            return FailureLong("Network.RateLimited", "The HLS service rate-limited this request.", null, true, retryAfter);
        }

        return FailureLong(
            response.StatusCode == HttpStatusCode.NotFound ? "Hls.SegmentMissing" : "Hls.NetworkFailed",
            response.StatusCode == HttpStatusCode.NotFound
                ? "A required HLS segment is no longer available."
                : "TubeForge could not download an HLS segment.",
            null,
            response.StatusCode == HttpStatusCode.NotFound || (int)response.StatusCode >= 500);
    }

    private static string PartsDirectory(string destination) => destination + ".hls.parts";

    private static string JournalPath(string destination) => destination + ".hls.json";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Result<DownloadReceipt> Failure(string code, string message, string? detail = null) =>
        Result<DownloadReceipt>.Failure(new TubeForgeError(code, message, detail));

    private static Result<HlsPlaylist> FailurePlaylist(
        string code,
        string message,
        string? detail = null,
        bool transient = false,
        TimeSpan? retryAfter = null) =>
        Result<HlsPlaylist>.Failure(new TubeForgeError(code, message, detail, transient, retryAfter));

    private static Result<DownloadedPart> FailurePart(string code, string message, string? detail = null) =>
        Result<DownloadedPart>.Failure(new TubeForgeError(code, message, detail));

    private static Result<long> FailureLong(
        string code,
        string message,
        string? detail = null,
        bool transient = false,
        TimeSpan? retryAfter = null) =>
        Result<long>.Failure(new TubeForgeError(code, message, detail, transient, retryAfter));

    private static Result<HlsCaptureJournal?> FailureJournal(string code, string message) =>
        Result<HlsCaptureJournal?>.Failure(new TubeForgeError(code, message));

    private sealed record DownloadedPart(HlsPart Entry, string? InitializationHash);

    private sealed record HlsPart(long Sequence, string FileName, long Length, long DurationTicks);

    private sealed record HlsCaptureJournal
    {
        public int SchemaVersion { get; init; } = 1;

        public required DateTimeOffset StartedAtUtc { get; init; }

        public long TotalBytes { get; set; }

        public long CapturedDurationTicks { get; set; }

        public string? LastInitializationHash { get; set; }

        public List<HlsPart> Parts { get; init; } = [];
    }
}
