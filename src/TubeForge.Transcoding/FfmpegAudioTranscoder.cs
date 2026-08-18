using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using TubeForge.Core.Errors;
using TubeForge.Core.Media;
using TubeForge.Core.Results;
using TubeForge.Media;

namespace TubeForge.Transcoding;

public sealed class FfmpegAudioTranscoder
{
    private const string RelativeBundledPath = "ffmpeg/ffmpeg.exe";
    private readonly string _executablePath;
    private readonly IFfmpegAudioProcessRunner _processRunner;

    public FfmpegAudioTranscoder(string executablePath)
        : this(executablePath, new FfmpegAudioProcessRunner())
    {
    }

    internal FfmpegAudioTranscoder(
        string executablePath,
        IFfmpegAudioProcessRunner processRunner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public string ExecutablePath => _executablePath;

    public bool IsAvailable => File.Exists(_executablePath);

    public static string BundledExecutablePath(string baseDirectory) =>
        Path.GetFullPath(Path.Combine(baseDirectory, RelativeBundledPath));

    public async Task<Result<AudioTranscodeReceipt>> TranscodeAsync(
        AudioTranscodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return Result<AudioTranscodeReceipt>.Failure(validation);
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
                var recovered = AudioTranscodeFileValidator.Validate(destination, request.Output.Kind);
                if (!request.AllowExistingValidatedOutput || !recovered.IsSuccess)
                {
                    return Result<AudioTranscodeReceipt>.Failure(
                        recovered.Error ?? new TubeForgeError(
                            "Download.DestinationExists",
                            "The selected output file already exists."));
                }

                return Success(destination, request.Output.BitrateKbps);
            }

            if (!File.Exists(source))
            {
                return Failure("Media.TranscodeSourceMissing", "The source audio for conversion is missing.");
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
                "0:a:0",
                "-vn"
            ]);
            if (request.RemovedSegments.Count > 0)
            {
                arguments.AddRange([
                    "-af", $"asetpts=PTS-STARTPTS,aselect=not({RemovalExpression(request.RemovedSegments)}),asetpts=N/SR/TB"
                ]);
            }
            AppendEncoderArguments(arguments, request.Output);
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
                    "Media.FFmpegAudioFailed",
                    $"FFmpeg could not convert this audio stream to {request.Output.DisplayName}.",
                    $"FFmpeg exited with code {exitCode}.");
            }

            var outputValidation = AudioTranscodeFileValidator.Validate(temporary, request.Output.Kind);
            if (!outputValidation.IsSuccess)
            {
                return Result<AudioTranscodeReceipt>.Failure(outputValidation.Error!);
            }

            File.Move(temporary, destination, overwrite: false);
            temporary = null;
            return Success(destination, request.Output.BitrateKbps);
        }
        catch (OperationCanceledException)
        {
            return Failure("Operation.Cancelled", "The audio conversion was cancelled.");
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
                "TubeForge could not write the converted audio file.",
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

    private static TubeForgeError? ValidateRequest(AudioTranscodeRequest request)
    {
        if (!request.Output.IsValid || !request.Output.IsAudioTranscode ||
            request.Trim is { IsValid: false } || !RemovalRangesAreValid(request.RemovedSegments))
        {
            return new TubeForgeError("Media.InvalidTranscodeProfile", "Select a supported converted audio output profile.");
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

    private static bool RemovalRangesAreValid(IReadOnlyList<MediaTrimRange>? ranges)
    {
        if (ranges is null || ranges.Count > 1_000 || ranges.Any(range => !range.IsValid))
        {
            return false;
        }

        return ranges.Zip(ranges.Skip(1)).All(pair => pair.First.End <= pair.Second.Start);
    }

    private static string RemovalExpression(IReadOnlyList<MediaTrimRange> ranges) =>
        string.Join('+', ranges.Select(range =>
            $"between(t\\,{range.Start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}" +
            $"\\,{range.End.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)})"));

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

    private static void AppendEncoderArguments(List<string> arguments, OutputProfile output)
    {
        switch (output.Kind)
        {
            case OutputProfileKind.Mp3:
                arguments.AddRange(["-c:a", "libmp3lame", "-b:a", $"{output.BitrateKbps}k", "-f", "mp3"]);
                break;
            case OutputProfileKind.Aac:
                arguments.AddRange([
                    "-c:a", "aac", "-b:a", $"{output.BitrateKbps}k", "-movflags", "+faststart", "-f", "mp4"
                ]);
                break;
            case OutputProfileKind.Opus:
                arguments.AddRange(["-c:a", "libopus", "-b:a", $"{output.BitrateKbps}k", "-f", "ogg"]);
                break;
            case OutputProfileKind.Wav:
                arguments.AddRange(["-c:a", "pcm_s16le", "-f", "wav"]);
                break;
            case OutputProfileKind.Flac:
                arguments.AddRange(["-c:a", "flac", "-compression_level", "8", "-f", "flac"]);
                break;
            default:
                throw new InvalidOperationException("Unsupported audio output profile.");
        }
    }

    private static Result<AudioTranscodeReceipt> Success(string path, int bitrateKbps) =>
        Result<AudioTranscodeReceipt>.Success(new AudioTranscodeReceipt(
            path,
            new FileInfo(path).Length,
            bitrateKbps,
            Channels: 0,
            SampleRate: 0));

    private static Result<AudioTranscodeReceipt> Failure(
        string code,
        string message,
        string? detail = null) =>
        Result<AudioTranscodeReceipt>.Failure(new TubeForgeError(code, message, detail));

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

internal interface IFfmpegAudioProcessRunner
{
    Task<int> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed class FfmpegAudioProcessRunner : IFfmpegAudioProcessRunner
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
        ChildProcessJob.TryEnroll(process!);
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
