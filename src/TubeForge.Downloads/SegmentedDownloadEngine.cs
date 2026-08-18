using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TubeForge.Core.Errors;
using TubeForge.Core.Networking;
using TubeForge.Core.Results;

namespace TubeForge.Downloads;

internal sealed class SegmentedDownloadEngine(HttpClient httpClient, DownloadUriPolicy uriPolicy)
{
    private const int BufferSize = 128 * 1024;
    private const int MaximumSegmentCount = 4_096;
    private static readonly long ProgressIntervalTicks = TimeSpan.FromMilliseconds(100).Ticks;
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly DownloadUriPolicy _uriPolicy = uriPolicy ?? throw new ArgumentNullException(nameof(uriPolicy));

    public static bool ShouldUse(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var destination = Path.GetFullPath(request.DestinationPath);
        return !File.Exists(destination + ".part.json") &&
               request.ExpectedLength is long expectedLength &&
               (File.Exists(StatePath(destination)) ||
                request.EnableSegmentedTransfer && expectedLength >= request.SegmentedTransferMinimumBytes);
    }

    public static void Reset(string destinationPath)
    {
        var destination = Path.GetFullPath(destinationPath);
        TryDelete(destination + ".part");
        TryDelete(StatePath(destination));
        TryDelete(StatePath(destination) + ".new");
    }

    public async Task<Result<DownloadReceipt>> DownloadAttemptAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(request.DestinationPath);
        var partialPath = destination + ".part";
        var statePath = StatePath(destination);
        try
        {
            if (File.Exists(destination))
            {
                return Failure("Download.DestinationExists", "A file already exists at the selected destination.");
            }

            var directory = Path.GetDirectoryName(destination);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return Failure("Download.InvalidDestination", "The selected destination is invalid.");
            }

            Directory.CreateDirectory(directory);
            var expectedLength = request.ExpectedLength!.Value;
            var segmentBytes = CalculateSegmentBytes(expectedLength, request.SegmentedTransferChunkBytes);
            var segments = CreateSegments(expectedLength, segmentBytes);
            var state = await SegmentedDownloadStateStore.ReadAsync(statePath, cancellationToken)
                .ConfigureAwait(false);
            if (!IsCompatible(state, request, segments, segmentBytes) ||
                !File.Exists(partialPath) ||
                new FileInfo(partialPath).Length != expectedLength)
            {
                Reset(destination);
                await using (var initializer = new FileStream(
                                 partialPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.Read,
                                 bufferSize: 1,
                                 FileOptions.WriteThrough | FileOptions.RandomAccess))
                {
                    initializer.SetLength(expectedLength);
                    initializer.Flush(flushToDisk: true);
                }

                state = new SegmentedDownloadState
                {
                    SourceIdentity = request.SourceIdentity,
                    ExpectedLength = expectedLength,
                    SegmentCount = segments.Length,
                    SegmentBytes = segmentBytes,
                    Completed = new bool[segments.Length]
                };
                await SegmentedDownloadStateStore.WriteAsync(statePath, state, cancellationToken)
                    .ConfigureAwait(false);
            }

            var activeState = state!;
            var resumed = activeState.Completed.Any(completed => completed);
            var completedBytes = segments
                .Where((_, index) => activeState.Completed[index])
                .Sum(segment => segment.Length);
            var attemptBytes = 0L;
            var stopwatch = Stopwatch.StartNew();
            var lastProgressTicks = 0L;
            var requestNumber = -1;
            using var stateGate = new SemaphoreSlim(1, 1);
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var currentState = activeState;
            await using (var output = new FileStream(
                             partialPath,
                             FileMode.Open,
                             FileAccess.Write,
                             FileShare.Read,
                             bufferSize: 1,
                             FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                progress?.Report(Progress(completedBytes, expectedLength, attemptBytes, stopwatch.Elapsed));
                var pendingSegments = segments
                    .Where((_, index) => !activeState.Completed[index])
                    .ToArray();
                var nextSegment = -1;
                var failures = new ConcurrentQueue<TubeForgeError>();

                void ReportBytes(int bytes)
                {
                    var received = Interlocked.Add(ref completedBytes, bytes);
                    var currentAttemptBytes = Interlocked.Add(ref attemptBytes, bytes);
                    if (progress is null)
                    {
                        return;
                    }

                    var elapsed = stopwatch.Elapsed;
                    var elapsedTicks = elapsed.Ticks;
                    var previousTicks = Volatile.Read(ref lastProgressTicks);
                    if (received != expectedLength &&
                        (elapsedTicks - previousTicks < ProgressIntervalTicks ||
                         Interlocked.CompareExchange(ref lastProgressTicks, elapsedTicks, previousTicks) != previousTicks))
                    {
                        return;
                    }

                    progress.Report(Progress(received, expectedLength, currentAttemptBytes, elapsed));
                }

                async Task RunWorkerAsync()
                {
                    await Task.Yield();
                    while (!attemptCancellation.IsCancellationRequested)
                    {
                        var index = Interlocked.Increment(ref nextSegment);
                        if (index >= pendingSegments.Length)
                        {
                            return;
                        }

                        var result = await DownloadSegmentAsync(
                                request,
                                pendingSegments[index],
                                Interlocked.Increment(ref requestNumber),
                                output.SafeFileHandle,
                                stateGate,
                                () => currentState,
                                value => currentState = value,
                                statePath,
                                ReportBytes,
                                attemptCancellation.Token)
                            .ConfigureAwait(false);
                        if (result.IsSuccess)
                        {
                            continue;
                        }

                        failures.Enqueue(result.Error!);
                        await attemptCancellation.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }

                var workers = Enumerable.Range(
                        0,
                        Math.Min(request.MaximumSegments, pendingSegments.Length))
                    .Select(_ => RunWorkerAsync())
                    .ToArray();
                await Task.WhenAll(workers).ConfigureAwait(false);
                if (failures.TryDequeue(out var failure))
                {
                    return Result<DownloadReceipt>.Failure(failure);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            if (!currentState.Completed.All(completed => completed))
            {
                return Failure(
                    "Download.Incomplete",
                    "One or more segmented ranges did not complete.",
                    isTransient: true);
            }

            return DirectDownloadEngine.FinalizeCompletedPartial(
                destination,
                partialPath,
                statePath,
                expectedLength,
                resumed,
                request.ExpectedContainer);
        }
        catch (OperationCanceledException)
        {
            return Failure("Operation.Cancelled", "The download was cancelled.");
        }
        catch (HttpRequestException exception)
        {
            return Failure(
                "Network.RequestFailed",
                "The media connection failed.",
                exception.GetType().Name,
                isTransient: true);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(
                "Download.WriteFailed",
                "TubeForge does not have permission to write the selected file.",
                exception.GetType().Name);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 0x27 or 0x70)
        {
            return Failure("Download.DiskFull", "The destination drive is full.", exception.GetType().Name);
        }
        catch (IOException exception)
        {
            return Failure(
                "Download.TransferFailed",
                "The segmented media transfer was interrupted.",
                exception.GetType().Name,
                isTransient: true);
        }
        catch (JsonException)
        {
            Reset(destination);
            return Failure(
                "Download.SegmentStateInvalid",
                "The segmented transfer state was invalid and has been reset.",
                isTransient: true);
        }
    }

    private async Task<Result<bool>> DownloadSegmentAsync(
        DownloadRequest request,
        ByteRange segment,
        int requestNumber,
        SafeFileHandle output,
        SemaphoreSlim stateGate,
        Func<SegmentedDownloadState> getState,
        Action<SegmentedDownloadState> setState,
        string statePath,
        Action<int> reportBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var usesRangeQuery = DirectDownloadEngine.IsGoogleVideo(request.SourceUrl);
            var requestUri = usesRangeQuery
                ? DirectDownloadEngine.AddRangeQuery(request.SourceUrl, segment.Start, segment.End, requestNumber)
                : request.SourceUrl;
            using var message = new HttpRequestMessage(HttpMethod.Get, requestUri);
            if (!HttpUserAgentHeader.TryApply(message, request.HttpUserAgent))
            {
                return FailureBool(
                    "Download.InvalidUserAgent",
                    "The media request contained an invalid HTTP user agent.");
            }

            if (!usesRangeQuery)
            {
                message.Headers.Range = new RangeHeaderValue(segment.Start, segment.End);
            }
            var initialState = getState();
            if (!string.IsNullOrWhiteSpace(initialState.EntityTag) &&
                EntityTagHeaderValue.TryParse(initialState.EntityTag, out var entityTag))
            {
                message.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
            }

            using var response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? request.SourceUrl;
            if (!_uriPolicy.IsAllowed(finalUri))
            {
                return FailureBool("Download.UnsafeRedirect", "The media request redirected to an untrusted location.");
            }

            if (!usesRangeQuery && response.StatusCode == HttpStatusCode.OK)
            {
                return FailureBool(
                    "Download.RangeUnsupported",
                    "The media server does not support segmented byte ranges.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var httpFailure = DirectDownloadEngine.HttpFailure(response);
                return Result<bool>.Failure(httpFailure.Error!);
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (usesRangeQuery && contentLength is long queryLength && queryLength != segment.Length)
            {
                return FailureBool(
                    queryLength > segment.Length ? "Download.RangeUnsupported" : "Download.RemoteChanged",
                    queryLength > segment.Length
                        ? "The media server did not honor a segmented query range."
                        : "The server returned an incompatible segmented query range.");
            }

            var contentRange = response.Content.Headers.ContentRange;
            if (!usesRangeQuery &&
                (response.StatusCode != HttpStatusCode.PartialContent ||
                 contentRange?.From != segment.Start ||
                 contentRange.To != segment.End ||
                 contentRange.Length != request.ExpectedLength ||
                 contentLength is long headerLength && headerLength != segment.Length))
            {
                return FailureBool(
                    "Download.RemoteChanged",
                    "The server returned an incompatible segmented range.");
            }

            await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var state = getState();
                var responseEntityTag = response.Headers.ETag?.ToString();
                if (!string.IsNullOrWhiteSpace(state.EntityTag) &&
                    !string.IsNullOrWhiteSpace(responseEntityTag) &&
                    !state.EntityTag.Equals(responseEntityTag, StringComparison.Ordinal))
                {
                    return FailureBool(
                        "Download.RemoteChanged",
                        "The remote media validator changed between segmented ranges.");
                }

                if (string.IsNullOrWhiteSpace(state.EntityTag) && !string.IsNullOrWhiteSpace(responseEntityTag))
                {
                    setState(state with { EntityTag = responseEntityTag });
                }
            }
            finally
            {
                stateGate.Release();
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            var received = 0L;
            try
            {
                while (true)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                        .ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    if (received + count > segment.Length)
                    {
                        return FailureBool(
                            usesRangeQuery ? "Download.RangeUnsupported" : "Download.RemoteChanged",
                            usesRangeQuery
                                ? "The media server did not honor a segmented query range."
                                : "The server sent more bytes than the requested segment.");
                    }

                    await RandomAccess.WriteAsync(
                        output,
                        buffer.AsMemory(0, count),
                        segment.Start + received,
                        cancellationToken).ConfigureAwait(false);
                    received += count;
                    reportBytes(count);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (received != segment.Length)
            {
                return FailureBool(
                    "Download.Incomplete",
                    "The server ended a segmented range early.",
                    isTransient: true);
            }

            // Flush the segment's bytes to the device before recording it as complete. Marking it
            // first means a power loss between the two leaves a resume that trusts a region the
            // file system never wrote, and the finished file is silently corrupt.
            RandomAccess.FlushToDisk(output);

            await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var state = getState();
                var completed = state.Completed.ToArray();
                completed[segment.Index] = true;
                var updated = state with { Completed = completed };
                await SegmentedDownloadStateStore.WriteAsync(statePath, updated, cancellationToken)
                    .ConfigureAwait(false);
                setState(updated);
            }
            finally
            {
                stateGate.Release();
            }

            return Result<bool>.Success(true);
        }
        catch (OperationCanceledException)
        {
            return FailureBool("Operation.Cancelled", "The download was cancelled.");
        }
        catch (HttpRequestException exception)
        {
            return FailureBool(
                "Network.RequestFailed",
                "The media connection failed.",
                exception.GetType().Name,
                isTransient: true);
        }
        catch (IOException exception)
        {
            return FailureBool(
                "Download.TransferFailed",
                "A segmented range transfer failed.",
                exception.GetType().Name,
                isTransient: true);
        }
    }

    private static bool IsCompatible(
        SegmentedDownloadState? state,
        DownloadRequest request,
        IReadOnlyList<ByteRange> segments,
        long segmentBytes) =>
        state is not null &&
        state.SchemaVersion == SegmentedDownloadState.CurrentSchemaVersion &&
        string.Equals(state.SourceIdentity, request.SourceIdentity, StringComparison.Ordinal) &&
        state.ExpectedLength == request.ExpectedLength &&
        state.SegmentCount == segments.Count &&
        state.SegmentBytes == segmentBytes &&
        state.Completed is not null &&
        state.Completed.Length == segments.Count;

    private static long CalculateSegmentBytes(long totalLength, long requestedBytes)
    {
        var minimumBytes = totalLength / MaximumSegmentCount;
        if (totalLength % MaximumSegmentCount != 0)
        {
            minimumBytes++;
        }

        return Math.Max(requestedBytes, minimumBytes);
    }

    private static ByteRange[] CreateSegments(long totalLength, long segmentBytes)
    {
        var count = totalLength / segmentBytes;
        if (totalLength % segmentBytes != 0)
        {
            count++;
        }

        var ranges = new ByteRange[checked((int)count)];
        var start = 0L;
        for (var index = 0; index < ranges.Length; index++)
        {
            var length = Math.Min(segmentBytes, totalLength - start);
            ranges[index] = new ByteRange(index, start, checked(start + length - 1));
            start += length;
        }

        return ranges;
    }

    private static DownloadProgress Progress(
        long received,
        long total,
        long attemptBytes,
        TimeSpan elapsed)
    {
        var speed = elapsed.TotalSeconds > 0 ? attemptBytes / elapsed.TotalSeconds : 0;
        var remaining = speed > 0
            ? TimeSpan.FromSeconds(Math.Max(0, total - received) / speed)
            : (TimeSpan?)null;
        return new DownloadProgress(received, total, speed, remaining);
    }

    private static string StatePath(string destination) => destination + ".part.segments.json";

    private static Result<DownloadReceipt> Failure(
        string code,
        string message,
        string? detail = null,
        bool isTransient = false) =>
        Result<DownloadReceipt>.Failure(new TubeForgeError(code, message, detail, isTransient));

    private static Result<bool> FailureBool(
        string code,
        string message,
        string? detail = null,
        bool isTransient = false) =>
        Result<bool>.Failure(new TubeForgeError(code, message, detail, isTransient));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ByteRange(int Index, long Start, long End)
    {
        public long Length => checked(End - Start + 1);
    }
}

internal sealed record SegmentedDownloadState
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string SourceIdentity { get; init; }

    public required long ExpectedLength { get; init; }

    public required int SegmentCount { get; init; }

    public long SegmentBytes { get; init; }

    public required bool[] Completed { get; init; }

    public string? EntityTag { get; init; }
}

internal static class SegmentedDownloadStateStore
{
    private const long MaximumStateBytes = 256 * 1024;
    private const int MaximumSegmentCount = 4_096;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<SegmentedDownloadState?> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > MaximumStateBytes)
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            8 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<SegmentedDownloadState>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteAsync(
        string path,
        SegmentedDownloadState state,
        CancellationToken cancellationToken)
    {
        var pendingPath = path + ".new";
        await using (var stream = new FileStream(
                         pendingPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         8 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(pendingPath, path, overwrite: true);
    }

    public static long? ReadCompletedBytes(string destinationPath)
    {
        try
        {
            var destination = Path.GetFullPath(destinationPath);
            var partialPath = destination + ".part";
            var statePath = destination + ".part.segments.json";
            if (!File.Exists(partialPath) || !File.Exists(statePath) ||
                new FileInfo(statePath).Length > MaximumStateBytes)
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<SegmentedDownloadState>(
                File.ReadAllText(statePath),
                SerializerOptions);
            if (state is null ||
                state.ExpectedLength <= 0 ||
                state.Completed is null ||
                state.Completed.Length != state.SegmentCount ||
                new FileInfo(partialPath).Length != state.ExpectedLength)
            {
                return null;
            }

            if (state.SchemaVersion == 1 && state.SegmentCount is >= 2 and <= 8)
            {
                var baseLength = state.ExpectedLength / state.SegmentCount;
                var remainder = state.ExpectedLength % state.SegmentCount;
                var legacyCompletedBytes = 0L;
                for (var index = 0; index < state.SegmentCount; index++)
                {
                    if (state.Completed[index])
                    {
                        legacyCompletedBytes = checked(
                            legacyCompletedBytes + baseLength + (index < remainder ? 1 : 0));
                    }
                }

                return legacyCompletedBytes;
            }

            if (state.SchemaVersion != SegmentedDownloadState.CurrentSchemaVersion ||
                state.SegmentBytes <= 0 ||
                state.SegmentCount is < 1 or > MaximumSegmentCount ||
                state.SegmentCount != SegmentCount(state.ExpectedLength, state.SegmentBytes))
            {
                return null;
            }

            var completedBytes = 0L;
            for (var index = 0; index < state.SegmentCount; index++)
            {
                if (!state.Completed[index])
                {
                    continue;
                }

                var start = checked(index * state.SegmentBytes);
                completedBytes = checked(
                    completedBytes + Math.Min(state.SegmentBytes, state.ExpectedLength - start));
            }

            return completedBytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                          ArgumentException or NotSupportedException or OverflowException)
        {
            return null;
        }
    }

    private static int SegmentCount(long totalLength, long segmentBytes)
    {
        var count = totalLength / segmentBytes;
        if (totalLength % segmentBytes != 0)
        {
            count++;
        }

        return checked((int)count);
    }
}
