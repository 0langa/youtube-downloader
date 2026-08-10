using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using TubeForge.Core.Errors;
using TubeForge.Core.Media;
using TubeForge.Core.Results;
using TubeForge.Media;

namespace TubeForge.Transcoding;

public sealed class FfmpegVideoTranscoder
{
    private const string RelativeBundledPath = "ffmpeg/ffmpeg.exe";
    private readonly string _executablePath;
    private readonly IFfmpegVideoProcessRunner _processRunner;

    public FfmpegVideoTranscoder(string executablePath)
        : this(executablePath, new FfmpegVideoProcessRunner())
    {
    }

    internal FfmpegVideoTranscoder(
        string executablePath,
        IFfmpegVideoProcessRunner processRunner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public bool IsAvailable => File.Exists(_executablePath);

    public static string BundledExecutablePath(string baseDirectory) =>
        Path.GetFullPath(Path.Combine(baseDirectory, RelativeBundledPath));

    public async Task<Result<VideoTranscodeReceipt>> TranscodeAsync(
        VideoTranscodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return Result<VideoTranscodeReceipt>.Failure(validation);
        }

        var source = Path.GetFullPath(request.SourcePath);
        var destination = Path.GetFullPath(request.DestinationPath);
        string? temporary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAvailable)
            {
                return Failure(
                    "Media.FFmpegMissing",
                    "TubeForge's bundled FFmpeg media engine is missing. Reinstall TubeForge.");
            }

            if (File.Exists(destination))
            {
                var recovered = ValidateOutput(destination, request.Output);
                if (!request.AllowExistingValidatedOutput || !recovered.IsSuccess)
                {
                    return Result<VideoTranscodeReceipt>.Failure(
                        recovered.Error ?? new TubeForgeError(
                            "Download.DestinationExists",
                            "The selected output file already exists."));
                }

                return Success(destination, request.Output);
            }

            if (!File.Exists(source))
            {
                return Failure("Media.TranscodeSourceMissing", "The source media for video conversion is missing.");
            }

            var directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".transcoding" + request.Output.Extension;
            var arguments = new List<string>
            {
                "-hide_banner",
                "-loglevel",
                "error",
                "-xerror",
                "-nostdin"
            };
            AppendTrimInput(
                arguments,
                source,
                request.Trim,
                limitInputDuration: request.RemovedSegments.Count > 0);
            arguments.AddRange([
                "-map",
                "0:v:0",
                "-map",
                "0:a:0"
            ]);
            AppendEncoderArguments(arguments, request.Output, request.RemovedSegments);
            arguments.AddRange(["-map_metadata", "-1", temporary]);

            var exitCode = await _processRunner.RunAsync(
                    _executablePath,
                    arguments,
                    directory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (exitCode != 0)
            {
                return Failure(
                    "Media.FFmpegVideoFailed",
                    $"FFmpeg could not convert this media to {request.Output.DisplayName}.",
                    $"FFmpeg exited with code {exitCode}.");
            }

            var outputValidation = ValidateOutput(temporary, request.Output);
            if (!outputValidation.IsSuccess)
            {
                return Result<VideoTranscodeReceipt>.Failure(outputValidation.Error!);
            }

            File.Move(temporary, destination, overwrite: false);
            temporary = null;
            return Success(destination, request.Output);
        }
        catch (OperationCanceledException)
        {
            return Failure("Operation.Cancelled", "The video conversion was cancelled.");
        }
        catch (Win32Exception exception)
        {
            return Failure(
                "Media.FFmpegStartFailed",
                "TubeForge could not start FFmpeg.",
                exception.GetType().Name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(
                "Media.TranscodeWriteFailed",
                "TubeForge could not write the converted video file.",
                exception.GetType().Name);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(
                "Media.InvalidTranscodePath",
                "Select valid source and destination paths.",
                exception.GetType().Name);
        }
        finally
        {
            if (temporary is not null)
            {
                TryDelete(temporary);
            }
        }
    }

    private static TubeForgeError? ValidateRequest(VideoTranscodeRequest request)
    {
        if (!request.Output.IsValid || !request.Output.IsVideoTranscode ||
            request.Trim is { IsValid: false } || !RemovalRangesAreValid(request.RemovedSegments))
        {
            return new TubeForgeError(
                "Media.InvalidTranscodeProfile",
                "Select a supported video output profile.");
        }

        if (string.IsNullOrWhiteSpace(request.SourcePath) ||
            string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            return new TubeForgeError("Media.InvalidTranscodePath", "Select valid source and destination paths.");
        }

        try
        {
            var source = Path.GetFullPath(request.SourcePath);
            var destination = Path.GetFullPath(request.DestinationPath);
            if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(destination), request.Output.Extension, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(Path.GetDirectoryName(destination)))
            {
                return new TubeForgeError("Media.InvalidTranscodePath", "Select valid source and destination paths.");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new TubeForgeError(
                "Media.InvalidTranscodePath",
                "Select valid source and destination paths.",
                exception.GetType().Name);
        }

        return null;
    }

    private static void AppendTrimInput(
        List<string> arguments,
        string source,
        MediaTrimRange? trim,
        bool limitInputDuration)
    {
        if (trim is { } range)
        {
            arguments.AddRange([
                "-ss", range.Start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)
            ]);
            if (limitInputDuration)
            {
                arguments.AddRange([
                    "-t", range.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                ]);
            }
        }

        arguments.AddRange(["-i", source]);
        if (trim is { } selectedRange && !limitInputDuration)
        {
            arguments.AddRange([
                "-t", selectedRange.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)
            ]);
        }
    }

    private static bool RemovalRangesAreValid(IReadOnlyList<MediaTrimRange>? ranges)
    {
        if (ranges is null || ranges.Count > 1_000 || ranges.Any(range => !range.IsValid))
        {
            return false;
        }

        return ranges.Zip(ranges.Skip(1)).All(pair => pair.First.End <= pair.Second.Start);
    }

    private static void AppendEncoderArguments(
        List<string> arguments,
        OutputProfile output,
        IReadOnlyList<MediaTrimRange> removedSegments)
    {
        var filters = new List<string>();
        if (removedSegments.Count > 0)
        {
            filters.Add("setpts=PTS-STARTPTS");
            filters.Add($"select=not({RemovalExpression(removedSegments)})");
            filters.Add("setpts=N/FRAME_RATE/TB");
        }

        if (output.Kind == OutputProfileKind.H265AacMp4)
        {
            filters.Add("pad=ceil(iw/8)*8:ceil(ih/8)*8");
        }

        if (filters.Count > 0)
        {
            arguments.AddRange(["-vf", string.Join(',', filters)]);
        }

        if (removedSegments.Count > 0)
        {
            arguments.AddRange([
                "-af", $"asetpts=PTS-STARTPTS,aselect=not({RemovalExpression(removedSegments)}),asetpts=N/SR/TB"
            ]);
        }

        switch (output.Kind)
        {
            case OutputProfileKind.H264AacMp4:
                arguments.AddRange([
                    "-c:v", "libopenh264",
                    "-b:v", $"{output.VideoBitrateKbps}k",
                    "-maxrate", $"{output.VideoBitrateKbps}k",
                    "-bufsize", $"{output.VideoBitrateKbps * 2}k",
                    "-pix_fmt", "yuv420p",
                    "-c:a", "aac",
                    "-b:a", $"{output.BitrateKbps}k",
                    "-ac", "2",
                    "-movflags", "+faststart",
                    "-f", "mp4"
                ]);
                break;
            case OutputProfileKind.H265AacMp4:
                arguments.AddRange([
                    "-c:v", "libkvazaar",
                    "-b:v", $"{output.VideoBitrateKbps}k",
                    "-pix_fmt", "yuv420p",
                    "-c:a", "aac",
                    "-b:a", $"{output.BitrateKbps}k",
                    "-ac", "2",
                    "-movflags", "+faststart",
                    "-tag:v", "hvc1",
                    "-f", "mp4"
                ]);
                break;
            case OutputProfileKind.Vp9OpusWebM:
                arguments.AddRange([
                    "-c:v", "libvpx-vp9",
                    "-b:v", $"{output.VideoBitrateKbps}k",
                    "-deadline", "good",
                    "-cpu-used", "2",
                    "-row-mt", "1",
                    "-pix_fmt", "yuv420p",
                    "-c:a", "libopus",
                    "-b:a", $"{output.BitrateKbps}k",
                    "-f", "webm"
                ]);
                break;
            default:
                throw new InvalidOperationException("Unsupported video output profile.");
        }
    }

    private static string RemovalExpression(IReadOnlyList<MediaTrimRange> ranges) =>
        string.Join('+', ranges.Select(range =>
            $"between(t\\,{range.Start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}" +
            $"\\,{range.End.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)})"));

    private static Result<bool> ValidateOutput(string path, OutputProfile output) =>
        MediaContainerValidator.Validate(
            path,
            output.Kind == OutputProfileKind.Vp9OpusWebM ? MediaContainer.WebM : MediaContainer.Mp4);

    private static Result<VideoTranscodeReceipt> Success(string path, OutputProfile output) =>
        Result<VideoTranscodeReceipt>.Success(new VideoTranscodeReceipt(
            path,
            new FileInfo(path).Length,
            output));

    private static Result<VideoTranscodeReceipt> Failure(
        string code,
        string message,
        string? detail = null) =>
        Result<VideoTranscodeReceipt>.Failure(new TubeForgeError(code, message, detail));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal interface IFfmpegVideoProcessRunner
{
    Task<int> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed class FfmpegVideoProcessRunner : IFfmpegVideoProcessRunner
{
    public async Task<int> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            throw new Win32Exception("FFmpeg did not start.");
        }

        var standardError = process.StandardError.ReadToEndAsync();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        _ = await standardError.ConfigureAwait(false);
        _ = await standardOutput.ConfigureAwait(false);
        return process.ExitCode;
    }
}
