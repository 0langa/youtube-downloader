using TubeForge.Core.Errors;
using TubeForge.Core.Media;
using TubeForge.Core.Results;
using TubeForge.Media;
using TubeForge.Media.Ebml;
using TubeForge.Media.IsoBmff;

namespace TubeForge.Downloads;

public sealed class AdaptiveDownloadEngine(
    DirectDownloadEngine directDownloadEngine,
    FfmpegMediaProcessor? ffmpegMediaProcessor = null)
{
    private readonly DirectDownloadEngine _directDownloadEngine =
        directDownloadEngine ?? throw new ArgumentNullException(nameof(directDownloadEngine));
    private readonly FfmpegMediaProcessor? _ffmpegMediaProcessor = ffmpegMediaProcessor;

    /// <param name="networkPhaseCompleted">
    /// Raised once both tracks are on disk and before muxing begins. Muxing is local work, so a
    /// caller holding a per-host transfer slot can release it here instead of blocking other
    /// transfers for the length of the mux.
    /// </param>
    public async Task<Result<AdaptiveDownloadReceipt>> DownloadAsync(
        AdaptiveDownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action? networkPhaseCompleted = null)
    {
        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return Result<AdaptiveDownloadReceipt>.Failure(validation);
        }

        var totalBytes = Sum(request.Video.ExpectedLength, request.Audio.ExpectedLength);
        if (request.AllowExistingValidatedOutput &&
            request.OutputContainer is MediaContainer.Mp4 or MediaContainer.WebM or MediaContainer.Mkv &&
            _ffmpegMediaProcessor is not null &&
            File.Exists(request.DestinationPath))
        {
            networkPhaseCompleted?.Invoke();
            var recovered = await _ffmpegMediaProcessor.MuxAsync(
                    request.Video.DestinationPath,
                    request.Audio.DestinationPath,
                    request.DestinationPath,
                    request.OutputContainer,
                    cancellationToken,
                    allowExistingValidatedOutput: true)
                .ConfigureAwait(false);
            if (!recovered.IsSuccess)
            {
                return Result<AdaptiveDownloadReceipt>.Failure(recovered.Error!);
            }

            progress?.Report(new DownloadProgress(
                recovered.Value.BytesWritten,
                recovered.Value.BytesWritten,
                0,
                TimeSpan.Zero));
            return Result<AdaptiveDownloadReceipt>.Success(new AdaptiveDownloadReceipt(
                recovered.Value.DestinationPath,
                recovered.Value.BytesWritten,
                0,
                0));
        }

        var videoProgress = ProgressFor(progress, 0, totalBytes);
        var videoResult = await EnsureTrackAsync(request.Video, videoProgress, cancellationToken)
            .ConfigureAwait(false);
        if (!videoResult.IsSuccess)
        {
            return Result<AdaptiveDownloadReceipt>.Failure(videoResult.Error!);
        }

        var audioProgress = ProgressFor(progress, videoResult.Value.BytesWritten, totalBytes);
        var audioResult = await EnsureTrackAsync(request.Audio, audioProgress, cancellationToken)
            .ConfigureAwait(false);
        if (!audioResult.IsSuccess)
        {
            return Result<AdaptiveDownloadReceipt>.Failure(audioResult.Error!);
        }

        networkPhaseCompleted?.Invoke();
        var muxResult = await MuxTracksAsync(request, cancellationToken).ConfigureAwait(false);
        if (!muxResult.IsSuccess)
        {
            return Result<AdaptiveDownloadReceipt>.Failure(muxResult.Error!);
        }

        // The muxed output already exists and is validated, so a failure to remove an
        // intermediate track is untidy, not fatal. Letting it throw here turned a completed
        // download into a Failed queue row and left both tracks behind anyway.
        TryDeleteIntermediate(request.Video.DestinationPath);
        TryDeleteIntermediate(request.Audio.DestinationPath);
        progress?.Report(new DownloadProgress(
            totalBytes ?? videoResult.Value.BytesWritten + audioResult.Value.BytesWritten,
            totalBytes,
            0,
            TimeSpan.Zero));
        return Result<AdaptiveDownloadReceipt>.Success(new AdaptiveDownloadReceipt(
            muxResult.Value.Path,
            muxResult.Value.Length,
            videoResult.Value.BytesWritten,
            audioResult.Value.BytesWritten));
    }

    private static void TryDeleteIntermediate(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            // A virus scanner or media indexer briefly holding the file is the common case.
        }
    }

    private async Task<Result<(string Path, long Length)>> MuxTracksAsync(
        AdaptiveDownloadRequest request,
        CancellationToken cancellationToken)
    {
        if (_ffmpegMediaProcessor is not null)
        {
            var processed = await _ffmpegMediaProcessor.MuxAsync(
                    request.Video.DestinationPath,
                    request.Audio.DestinationPath,
                    request.DestinationPath,
                    request.OutputContainer,
                    cancellationToken,
                    request.AllowExistingValidatedOutput)
                .ConfigureAwait(false);
            return processed.IsSuccess
                ? Result<(string Path, long Length)>.Success((
                    processed.Value.DestinationPath,
                    processed.Value.BytesWritten))
                : Result<(string Path, long Length)>.Failure(processed.Error!);
        }

        if (request.OutputContainer == MediaContainer.Mkv)
        {
            return Result<(string Path, long Length)>.Failure(new TubeForgeError(
                "Media.FFmpegMissing",
                "TubeForge's bundled FFmpeg media engine is missing. Reinstall TubeForge."));
        }

        if (request.OutputContainer == MediaContainer.Mp4)
        {
            var mp4 = await Mp4TrackMuxer.MuxAsync(
                    request.Video.DestinationPath,
                    request.Audio.DestinationPath,
                    request.DestinationPath,
                    cancellationToken)
                .ConfigureAwait(false);
            return mp4.IsSuccess
                ? Result<(string Path, long Length)>.Success((
                    mp4.Value.DestinationPath,
                    mp4.Value.BytesWritten))
                : Result<(string Path, long Length)>.Failure(mp4.Error!);
        }

        var webm = await WebMTrackMuxer.MuxAsync(
                request.Video.DestinationPath,
                request.Audio.DestinationPath,
                request.DestinationPath,
                cancellationToken)
            .ConfigureAwait(false);
        return webm.IsSuccess
            ? Result<(string Path, long Length)>.Success((
                webm.Value.DestinationPath,
                webm.Value.BytesWritten))
            : Result<(string Path, long Length)>.Failure(webm.Error!);
    }

    private async Task<Result<DownloadReceipt>> EnsureTrackAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(request.DestinationPath))
        {
            return await _directDownloadEngine.DownloadAsync(request, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        var length = new FileInfo(request.DestinationPath).Length;
        if (request.ExpectedLength is not null && request.ExpectedLength != length)
        {
            return Result<DownloadReceipt>.Failure(new TubeForgeError(
                "Media.IntermediateConflict",
                "A completed intermediate track has an unexpected size."));
        }

        var validation = MediaContainerValidator.Validate(request.DestinationPath, request.ExpectedContainer);
        if (!validation.IsSuccess)
        {
            return Result<DownloadReceipt>.Failure(validation.Error!);
        }

        progress?.Report(new DownloadProgress(length, request.ExpectedLength ?? length, 0, TimeSpan.Zero));
        return Result<DownloadReceipt>.Success(new DownloadReceipt(request.DestinationPath, length, Resumed: true));
    }

    private static TubeForgeError? ValidateRequest(AdaptiveDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Video);
        ArgumentNullException.ThrowIfNull(request.Audio);
        var sourcesAreDownloadable =
            request.Video.ExpectedContainer is MediaContainer.Mp4 or MediaContainer.WebM &&
            request.Audio.ExpectedContainer is MediaContainer.Mp4 or MediaContainer.WebM;
        var valid = request.OutputContainer switch
        {
            // Native lossless mux: both tracks must already share the output container.
            MediaContainer.Mp4 or MediaContainer.WebM =>
                request.Video.ExpectedContainer == request.OutputContainer &&
                request.Audio.ExpectedContainer == request.OutputContainer,
            // Cross-container lossless mux into Matroska: any downloadable source pair.
            MediaContainer.Mkv => sourcesAreDownloadable,
            _ => false
        };
        if (!valid)
        {
            return new TubeForgeError(
                "Media.IncompatibleTracks",
                "Adaptive video and audio must use one supported output container.");
        }

        if (string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            return new TubeForgeError("Download.InvalidDestination", "Select an adaptive output file.");
        }

        return null;
    }

    private static IProgress<DownloadProgress>? ProgressFor(
        IProgress<DownloadProgress>? target,
        long completedBytes,
        long? totalBytes) =>
        target is null
            ? null
            : new InlineProgress(value =>
            {
                var received = checked(completedBytes + value.BytesReceived);
                var remaining = totalBytes is not null && value.BytesPerSecond > 0
                    ? TimeSpan.FromSeconds(Math.Max(0, totalBytes.Value - received) / value.BytesPerSecond)
                    : value.EstimatedRemaining;
                target.Report(new DownloadProgress(received, totalBytes, value.BytesPerSecond, remaining));
            });

    private static long? Sum(long? first, long? second) =>
        first is not null && second is not null ? checked(first.Value + second.Value) : null;

    private sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
