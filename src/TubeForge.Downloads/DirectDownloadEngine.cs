using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using TubeForge.Core.Errors;
using TubeForge.Core.Media;
using TubeForge.Core.Networking;
using TubeForge.Core.Results;
using TubeForge.Downloads.Queue;
using TubeForge.Downloads.Resume;
using TubeForge.Media;

namespace TubeForge.Downloads;

public sealed class DirectDownloadEngine
{
    private const int BufferSize = 128 * 1024;
    internal const long MaximumDirectRequestBytes = 8 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly DownloadUriPolicy _uriPolicy;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string, bool, Stream> _outputStreamFactory;
    private readonly SegmentedDownloadEngine _segmentedDownloadEngine;
    private readonly int _maximumAttempts;

    public DirectDownloadEngine(
        HttpClient httpClient,
        DownloadUriPolicy? uriPolicy = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int maximumAttempts = DownloadRetryPolicy.MaximumAttempts)
        : this(httpClient, uriPolicy, delay, outputStreamFactory: null, maximumAttempts)
    {
    }

    internal DirectDownloadEngine(
        HttpClient httpClient,
        DownloadUriPolicy? uriPolicy,
        Func<TimeSpan, CancellationToken, Task>? delay,
        Func<string, bool, Stream>? outputStreamFactory,
        int maximumAttempts = DownloadRetryPolicy.MaximumAttempts)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _uriPolicy = uriPolicy ?? DownloadUriPolicy.YouTubeMediaOnly;
        _delay = delay ?? Task.Delay;
        _outputStreamFactory = outputStreamFactory ?? CreateOutputStream;
        if (maximumAttempts is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        _maximumAttempts = maximumAttempts;
        _segmentedDownloadEngine = new SegmentedDownloadEngine(_httpClient, _uriPolicy);
    }

    public async Task<Result<DownloadReceipt>> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var validationFailure = ValidateRequest(request);
        if (validationFailure is not null)
        {
            return Result<DownloadReceipt>.Failure(validationFailure);
        }

        var useSegmentedTransfer = SegmentedDownloadEngine.ShouldUse(request);
        for (var attempt = 1; attempt <= _maximumAttempts; attempt++)
        {
            var result = useSegmentedTransfer
                ? await _segmentedDownloadEngine.DownloadAttemptAsync(request, progress, cancellationToken)
                    .ConfigureAwait(false)
                : await DownloadAttemptAsync(request, progress, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess && result.Error?.Code == "Download.RangeUnsupported")
            {
                SegmentedDownloadEngine.Reset(request.DestinationPath);
                useSegmentedTransfer = false;
                result = await DownloadAttemptAsync(request, progress, cancellationToken).ConfigureAwait(false);
            }

            if (result.IsSuccess ||
                result.Error?.IsTransient != true ||
                attempt == _maximumAttempts ||
                cancellationToken.IsCancellationRequested)
            {
                return result;
            }

            try
            {
                var delay = result.Error?.RetryAfter ?? DownloadRetryPolicy.DelayBeforeAttempt(attempt);
                await _delay(delay, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Cancelled();
            }
        }

        throw new UnreachableException();
    }

    private async Task<Result<DownloadReceipt>> DownloadAttemptAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var destinationPath = Path.GetFullPath(request.DestinationPath);
        var partialPath = destinationPath + ".part";
        var statePath = destinationPath + ".part.json";
        try
        {
            if (File.Exists(destinationPath))
            {
                return Failure(
                    "Download.DestinationExists",
                    "A file already exists at the selected destination.");
            }

            var directory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return Failure("Download.InvalidDestination", "The selected destination is invalid.");
            }

            Directory.CreateDirectory(directory);
            var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            var state = existingLength > 0
                ? await DownloadResumeStore.ReadAsync(statePath, cancellationToken).ConfigureAwait(false)
                : null;
            var canResume = existingLength > 0 && IsCompatible(state, request, existingLength);
            if (!canResume)
            {
                existingLength = 0;
            }

            var resumedAtStart = canResume;
            var currentLength = canResume ? existingLength : 0;
            var expectedLength = request.ExpectedLength ?? state?.ExpectedLength;
            var activeState = canResume ? state : null;
            var requestNumber = 0;
            if (expectedLength is not null && currentLength == expectedLength)
            {
                return FinalizeCompletedPartial(
                    destinationPath,
                    partialPath,
                    statePath,
                    currentLength,
                    resumedAtStart,
                    request.ExpectedContainer);
            }

            while (true)
            {
                var maximumEnd = currentLength > long.MaxValue - MaximumDirectRequestBytes
                    ? long.MaxValue
                    : currentLength + MaximumDirectRequestBytes - 1;
                var rangeEnd = expectedLength is long total
                    ? Math.Min(total - 1, maximumEnd)
                    : maximumEnd;
                var usesRangeQuery = IsGoogleVideo(request.SourceUrl);
                var requestUri = usesRangeQuery
                    ? AddRangeQuery(request.SourceUrl, currentLength, rangeEnd, requestNumber++)
                    : request.SourceUrl;
                using var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri);
                if (!HttpUserAgentHeader.TryApply(requestMessage, request.HttpUserAgent))
                {
                    return Failure(
                        "Download.InvalidUserAgent",
                        "The media request contained an invalid HTTP user agent.");
                }

                // Googlevideo rejects full-object and open-ended GETs for some adaptive URLs.
                // Bounded requests also keep each response recoverable after interruption.
                if (!usesRangeQuery)
                {
                    requestMessage.Headers.Range = new RangeHeaderValue(currentLength, rangeEnd);
                }
                if (currentLength > 0 && activeState is not null)
                {
                    if (!string.IsNullOrWhiteSpace(activeState.EntityTag) &&
                        EntityTagHeaderValue.TryParse(activeState.EntityTag, out var entityTag))
                    {
                        requestMessage.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
                    }
                    else if (activeState.LastModified is not null)
                    {
                        requestMessage.Headers.IfRange = new RangeConditionHeaderValue(activeState.LastModified.Value);
                    }
                }

                using var response = await _httpClient.SendAsync(
                    requestMessage,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                var finalUri = response.RequestMessage?.RequestUri ?? request.SourceUrl;
                if (!_uriPolicy.IsAllowed(finalUri))
                {
                    return Failure(
                        "Download.UnsafeRedirect",
                        "The media request redirected to an untrusted location.");
                }

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
                    expectedLength == currentLength)
                {
                    return FinalizeCompletedPartial(
                        destinationPath,
                        partialPath,
                        statePath,
                        currentLength,
                        resumedAtStart,
                        request.ExpectedContainer);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var failure = HttpFailure(response);
                    return Result<DownloadReceipt>.Failure(failure.Error! with
                    {
                        TechnicalDetail = $"Requested media bytes {currentLength}-{rangeEnd}."
                    });
                }

                var isPartial = response.StatusCode == HttpStatusCode.PartialContent ||
                                usesRangeQuery && response.StatusCode == HttpStatusCode.OK;
                var contentRange = response.Content.Headers.ContentRange;
                if (!usesRangeQuery && response.StatusCode == HttpStatusCode.PartialContent &&
                    (contentRange?.From != currentLength ||
                     contentRange.To is null ||
                     contentRange.To > rangeEnd))
                {
                    return Failure(
                        "Download.RemoteChanged",
                        "The server returned an incompatible byte range.");
                }

                if (!isPartial && currentLength > 0)
                {
                    // If-Range permits a changed server object to return 200 with the complete
                    // representation. Replace the partial instead of appending that response.
                    currentLength = 0;
                    activeState = null;
                }

                var requestedBytes = checked(rangeEnd - currentLength + 1);
                var queryResponseBytes = response.Content.Headers.ContentLength;
                var discoveredQueryEnd = usesRangeQuery &&
                                         expectedLength is null &&
                                         queryResponseBytes is >= 0 &&
                                         queryResponseBytes < requestedBytes;
                if (usesRangeQuery &&
                    queryResponseBytes is long queryLength &&
                    (queryLength > requestedBytes ||
                     expectedLength is not null && queryLength != requestedBytes))
                {
                    return Failure(
                        "Download.RemoteChanged",
                        "The media server returned an incompatible query range.");
                }

                var responseTotal = usesRangeQuery
                    ? discoveredQueryEnd
                        ? checked(currentLength + queryResponseBytes!.Value)
                        : expectedLength
                    : contentRange?.Length ?? DetermineTotalLength(response, currentLength);
                if (expectedLength is not null &&
                    responseTotal is not null &&
                    expectedLength != responseTotal)
                {
                    return Failure(
                        "Download.RemoteChanged",
                        "The remote media size changed before the download completed.",
                        $"Expected {expectedLength}; response reported {responseTotal}; " +
                        $"status {(int)response.StatusCode}; range {contentRange}.");
                }

                expectedLength ??= responseTotal;
                var nextState = new DownloadResumeState
                {
                    SourceIdentity = request.SourceIdentity,
                    ExpectedLength = expectedLength,
                    EntityTag = response.Headers.ETag?.ToString() ?? activeState?.EntityTag,
                    LastModified = response.Content.Headers.LastModified ?? activeState?.LastModified
                };
                if (activeState != nextState)
                {
                    await DownloadResumeStore.WriteAsync(statePath, nextState, cancellationToken)
                        .ConfigureAwait(false);
                }

                activeState = nextState;

                var responseStart = currentLength;
                var transferred = await CopyResponseAsync(
                    response,
                    partialPath,
                    append: currentLength > 0,
                    currentLength,
                    expectedLength,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                currentLength = checked(currentLength + transferred);

                if (response.Content.Headers.ContentLength is long responseLength &&
                    transferred != responseLength ||
                    usesRangeQuery && transferred != (discoveredQueryEnd
                        ? queryResponseBytes!.Value
                        : requestedBytes) ||
                    !usesRangeQuery && response.StatusCode == HttpStatusCode.PartialContent &&
                    contentRange!.To != currentLength - 1)
                {
                    return Result<DownloadReceipt>.Failure(new TubeForgeError(
                        "Download.Incomplete",
                        "The server ended a media range before all requested bytes arrived.",
                        $"Range started at {responseStart}; received {transferred} bytes.",
                        IsTransient: true));
                }

                if (expectedLength is not null && currentLength > expectedLength)
                {
                    return Failure(
                        "Download.RemoteChanged",
                        "The server returned more media bytes than expected.");
                }

                if (expectedLength == currentLength ||
                    !isPartial && expectedLength is null)
                {
                    return FinalizeCompletedPartial(
                        destinationPath,
                        partialPath,
                        statePath,
                        currentLength,
                        resumedAtStart,
                        request.ExpectedContainer);
                }

                if (!isPartial || transferred == 0)
                {
                    return Result<DownloadReceipt>.Failure(new TubeForgeError(
                        "Download.Incomplete",
                        "The server ended the transfer before all media bytes arrived.",
                        expectedLength is null
                            ? null
                            : $"Expected {expectedLength} bytes; received {currentLength}.",
                        IsTransient: true));
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (HttpRequestException exception)
        {
            return Result<DownloadReceipt>.Failure(new TubeForgeError(
                "Network.RequestFailed",
                "The media connection failed.",
                exception.GetType().Name,
                IsTransient: true));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result<DownloadReceipt>.Failure(new TubeForgeError(
                "Download.WriteFailed",
                "TubeForge does not have permission to write the selected file.",
                exception.GetType().Name));
        }
        catch (IOException exception) when (IsDiskFull(exception))
        {
            return Result<DownloadReceipt>.Failure(new TubeForgeError(
                "Download.DiskFull",
                "The destination drive is full.",
                exception.GetType().Name));
        }
        catch (IOException exception)
        {
            return Result<DownloadReceipt>.Failure(new TubeForgeError(
                "Download.TransferFailed",
                "The media transfer was interrupted.",
                exception.GetType().Name,
                IsTransient: true));
        }
    }

    private async Task<long> CopyResponseAsync(
        HttpResponseMessage response,
        string partialPath,
        bool append,
        long startingLength,
        long? totalLength,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = _outputStreamFactory(partialPath, append);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var stopwatch = Stopwatch.StartNew();
        var transferred = 0L;
        var lastReport = TimeSpan.Zero;

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

                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                transferred += count;

                if (progress is not null &&
                    (stopwatch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(100) ||
                     startingLength + transferred == totalLength))
                {
                    lastReport = stopwatch.Elapsed;
                    ReportProgress(progress, startingLength + transferred, totalLength, transferred, stopwatch.Elapsed);
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return transferred;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Stream CreateOutputStream(string partialPath, bool append) => new FileStream(
        partialPath,
        append ? FileMode.Append : FileMode.Create,
        FileAccess.Write,
        FileShare.Read,
        BufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void ReportProgress(
        IProgress<DownloadProgress> progress,
        long bytesReceived,
        long? totalBytes,
        long attemptBytes,
        TimeSpan elapsed)
    {
        var speed = elapsed.TotalSeconds > 0 ? attemptBytes / elapsed.TotalSeconds : 0;
        TimeSpan? remaining = totalBytes is not null && speed > 0
            ? TimeSpan.FromSeconds(Math.Max(0, totalBytes.Value - bytesReceived) / speed)
            : null;
        progress.Report(new DownloadProgress(bytesReceived, totalBytes, speed, remaining));
    }

    internal static Result<DownloadReceipt> FinalizeCompletedPartial(
        string destinationPath,
        string partialPath,
        string statePath,
        long finalLength,
        bool resumed,
        MediaContainer expectedContainer)
    {
        if (!File.Exists(partialPath) || new FileInfo(partialPath).Length != finalLength)
        {
            return Failure(
                "Download.Incomplete",
                "The partial file did not pass final length validation.");
        }

        if (expectedContainer != MediaContainer.Unknown)
        {
            var validation = MediaContainerValidator.Validate(partialPath, expectedContainer);
            if (!validation.IsSuccess)
            {
                return Result<DownloadReceipt>.Failure(validation.Error!);
            }
        }

        File.Move(partialPath, destinationPath, overwrite: false);
        TryDeleteResumeState(statePath);
        return Result<DownloadReceipt>.Success(new DownloadReceipt(destinationPath, finalLength, resumed));
    }

    private static void TryDeleteResumeState(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + ".new");
        }
        catch (IOException)
        {
            // A valid, finalized media file must not be reported as failed because a
            // nonessential resume record is temporarily held by another process.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only. A future download can replace this state.
        }
    }

    private static bool IsCompatible(
        DownloadResumeState? state,
        DownloadRequest request,
        long partialLength) =>
        state is not null &&
        state.SourceIdentity.Equals(request.SourceIdentity, StringComparison.Ordinal) &&
        (request.ExpectedLength is null || state.ExpectedLength is null || request.ExpectedLength == state.ExpectedLength) &&
        (state.ExpectedLength is null || partialLength <= state.ExpectedLength);

    private static long? DetermineTotalLength(HttpResponseMessage response, long startingLength)
    {
        if (response.StatusCode == HttpStatusCode.PartialContent &&
            response.Content.Headers.ContentRange?.Length is long rangeLength)
        {
            return rangeLength;
        }

        return response.Content.Headers.ContentLength is long contentLength
            ? startingLength + contentLength
            : null;
    }

    private TubeForgeError? ValidateRequest(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_uriPolicy.IsAllowed(request.SourceUrl))
        {
            return new TubeForgeError(
                "Download.UnsafeSource",
                "The selected media URL is not an approved YouTube media endpoint.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceIdentity) ||
            request.SourceIdentity.Length > DownloadSourceIdentity.MaximumIdentityLength)
        {
            return new TubeForgeError(
                "Download.InvalidSourceIdentity",
                "The media source identity is invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            return new TubeForgeError("Download.InvalidDestination", "Select a destination file.");
        }

        if (request.ExpectedLength is <= 0)
        {
            return new TubeForgeError("Download.InvalidLength", "The expected media size is invalid.");
        }

        if (request.EnableSegmentedTransfer &&
            (request.MaximumSegments is < 2 or > 8 ||
             request.SegmentedTransferMinimumBytes <= 0 ||
             request.SegmentedTransferChunkBytes <= 0))
        {
            return new TubeForgeError(
                "Download.InvalidSegmentation",
                "The segmented transfer settings are invalid.");
        }

        return null;
    }

    /// <summary>Error code raised when the media server rejects a signed URL outright.</summary>
    public const string MediaUrlRejectedCode = "Network.MediaUrlRejected";

    internal static Result<DownloadReceipt> HttpFailure(HttpResponseMessage response)
    {
        var statusCode = response.StatusCode;
        var transient = statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                        (int)statusCode >= 500;
        if (statusCode == HttpStatusCode.Forbidden)
        {
            // A signed media URL that expired or was invalidated mid-transfer. Retrying the same
            // URL cannot succeed, but re-resolving the video and starting again can.
            return Result<DownloadReceipt>.Failure(new TubeForgeError(
                MediaUrlRejectedCode,
                "The media server rejected this stream link; it has expired or is no longer valid.",
                "HTTP 403"));
        }

        return Result<DownloadReceipt>.Failure(new TubeForgeError(
            statusCode == HttpStatusCode.TooManyRequests ? "Network.RateLimited" : "Network.HttpError",
            $"The media server returned HTTP {(int)statusCode}.",
            IsTransient: transient,
            RetryAfter: statusCode == HttpStatusCode.TooManyRequests
                ? HttpRetryAfterParser.Parse(response.Headers)
                : null));
    }

    private static Result<DownloadReceipt> Failure(
        string code,
        string message,
        string? technicalDetail = null) =>
        Result<DownloadReceipt>.Failure(new TubeForgeError(code, message, technicalDetail));

    private static Result<DownloadReceipt> Cancelled() =>
        Result<DownloadReceipt>.Failure(new TubeForgeError(
            "Operation.Cancelled",
            "The download was cancelled."));

    private static bool IsDiskFull(IOException exception) =>
        (exception.HResult & 0xFFFF) is 0x27 or 0x70;

    internal static bool IsGoogleVideo(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase));

    internal static Uri AddRangeQuery(Uri source, long from, long to, int requestNumber)
    {
        var separator = string.IsNullOrEmpty(source.Query) ? "?" : "&";
        return new Uri(
            source.AbsoluteUri + separator +
            $"range={from}-{to}&rn={requestNumber}&rbuf=0",
            UriKind.Absolute);
    }
}
