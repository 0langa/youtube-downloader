using System.ComponentModel;
using System.Globalization;
using TubeForge.Core.Errors;
using TubeForge.Core.Files;
using TubeForge.Core.Media;
using TubeForge.Core.Results;

namespace TubeForge.Media;

public sealed record ChapterSplitRequest
{
    public required string SourcePath { get; init; }

    public required string DestinationDirectory { get; init; }

    public required MediaContainer OutputContainer { get; init; }

    public required IReadOnlyList<VideoChapter> Chapters { get; init; }

    public required TimeSpan Duration { get; init; }

    public string FileNameTemplate { get; init; } = "{chapterIndex} - {chapterTitle}";

    public required FileNameTemplateContext FileNameContext { get; init; }

    public bool AllowExistingValidatedOutput { get; init; }
}

public sealed record ChapterSplitReceipt(
    string DestinationDirectory,
    IReadOnlyList<string> OutputPaths,
    long BytesWritten);

public sealed class FfmpegChapterSplitter
{
    private readonly string _executablePath;
    private readonly IFfmpegProcessRunner _processRunner;

    public FfmpegChapterSplitter(string executablePath) : this(executablePath, new FfmpegProcessRunner())
    {
    }

    internal FfmpegChapterSplitter(string executablePath, IFfmpegProcessRunner processRunner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<Result<ChapterSplitReceipt>> SplitAsync(
        ChapterSplitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return Result<ChapterSplitReceipt>.Failure(validation);
        }

        var sourcePath = Path.GetFullPath(request.SourcePath);
        var destinationDirectory = Path.GetFullPath(request.DestinationDirectory);
        var parent = Path.GetDirectoryName(destinationDirectory)!;
        if (Directory.Exists(destinationDirectory))
        {
            return request.AllowExistingValidatedOutput
                ? ValidateExisting(request, destinationDirectory)
                : Failure("Download.DestinationExists", "The chapter output folder already exists.");
        }

        var temporaryDirectory = destinationDirectory + "." + Guid.NewGuid().ToString("N") + ".processing";
        try
        {
            if (!File.Exists(_executablePath))
            {
                return Failure("Media.FFmpegMissing", "TubeForge's bundled FFmpeg media engine is missing. Reinstall TubeForge.");
            }

            if (!File.Exists(sourcePath))
            {
                return Failure("Media.InputMissing", "The downloaded media file is missing.");
            }

            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(temporaryDirectory);
            var plannedOutputs = PlanOutputPaths(request, temporaryDirectory);
            if (!plannedOutputs.IsSuccess)
            {
                return Result<ChapterSplitReceipt>.Failure(plannedOutputs.Error!);
            }

            var outputs = new List<string>(request.Chapters.Count);
            long bytesWritten = 0;
            for (var index = 0; index < request.Chapters.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chapter = request.Chapters[index];
                var end = index + 1 < request.Chapters.Count
                    ? request.Chapters[index + 1].StartTime
                    : request.Duration;
                var outputPath = plannedOutputs.Value[index];
                var arguments = BuildArguments(
                    sourcePath,
                    outputPath,
                    request.OutputContainer,
                    chapter.StartTime,
                    end - chapter.StartTime);
                var exitCode = await _processRunner.RunAsync(
                        _executablePath,
                        arguments,
                        temporaryDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (exitCode != 0)
                {
                    return Failure(
                        "Media.ChapterSplitFailed",
                        $"FFmpeg could not create chapter {index + 1}.",
                        $"FFmpeg exited with code {exitCode}.");
                }

                var outputError = FfmpegMediaProcessor.ValidateOutput(outputPath, request.OutputContainer);
                if (outputError is not null)
                {
                    return Result<ChapterSplitReceipt>.Failure(outputError);
                }

                outputs.Add(Path.Combine(destinationDirectory, Path.GetFileName(outputPath)));
                bytesWritten = checked(bytesWritten + new FileInfo(outputPath).Length);
            }

            Directory.Move(temporaryDirectory, destinationDirectory);
            temporaryDirectory = string.Empty;
            return Result<ChapterSplitReceipt>.Success(new ChapterSplitReceipt(
                destinationDirectory,
                outputs,
                bytesWritten));
        }
        catch (OperationCanceledException)
        {
            return Failure("Operation.Cancelled", "Chapter splitting was cancelled.");
        }
        catch (OverflowException)
        {
            return Failure("Media.ChapterSplitTooLarge", "The chapter outputs exceed supported size limits.");
        }
        catch (Win32Exception exception)
        {
            return Failure("Media.FFmpegStartFailed", "TubeForge could not start FFmpeg.", exception.GetType().Name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("Media.ChapterSplitWriteFailed", "TubeForge could not publish the chapter outputs.", exception.GetType().Name);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryDirectory))
            {
                TryDeleteTemporaryDirectory(temporaryDirectory, destinationDirectory);
            }
        }
    }

    private static TubeForgeError? ValidateRequest(ChapterSplitRequest request)
    {
        if (request.OutputContainer is not (MediaContainer.Mp4 or MediaContainer.Mkv or MediaContainer.WebM) ||
            request.Chapters is null || request.Chapters.Count is < 1 or > 1_000 ||
            request.Duration <= TimeSpan.Zero || request.FileNameContext is null ||
            string.IsNullOrWhiteSpace(request.FileNameTemplate))
        {
            return new TubeForgeError("Media.InvalidChapterSplit", "The chapter split request is invalid.");
        }

        var previous = TimeSpan.MinValue;
        foreach (var chapter in request.Chapters)
        {
            if (string.IsNullOrWhiteSpace(chapter.Title) || chapter.Title.Length > 500 ||
                chapter.Title.Any(char.IsControl) || chapter.StartTime < TimeSpan.Zero ||
                chapter.StartTime <= previous || chapter.StartTime >= request.Duration)
            {
                return new TubeForgeError("Media.InvalidChapterSplit", "Chapter names or timestamps are invalid.");
            }

            previous = chapter.StartTime;
        }

        var template = FileNameTemplate.Render(
            request.FileNameTemplate,
            request.FileNameContext with
            {
                ChapterIndex = 1,
                ChapterTitle = request.Chapters[0].Title
            });
        if (!template.IsSuccess)
        {
            return template.Error;
        }

        try
        {
            var source = Path.GetFullPath(request.SourcePath);
            var destination = Path.GetFullPath(request.DestinationDirectory);
            if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(Path.GetDirectoryName(destination)))
            {
                return new TubeForgeError("Media.InvalidChapterSplit", "Chapter input and output paths are invalid.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new TubeForgeError("Media.InvalidChapterSplit", "Chapter input and output paths are invalid.");
        }

        return null;
    }

    private static IReadOnlyList<string> BuildArguments(
        string sourcePath,
        string outputPath,
        MediaContainer container,
        TimeSpan start,
        TimeSpan length)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-xerror", "-nostdin",
            "-ss", Seconds(start), "-i", sourcePath,
            "-t", Seconds(length),
            "-map", "0:v?", "-map", "0:a?", "-map", "0:s?",
            "-c", "copy", "-reset_timestamps", "1"
        };
        if (container == MediaContainer.Mp4)
        {
            arguments.Add("-movflags");
            arguments.Add("+faststart");
        }

        arguments.Add("-f");
        arguments.Add(container switch
        {
            MediaContainer.Mkv => "matroska",
            MediaContainer.WebM => "webm",
            _ => "mp4"
        });
        arguments.Add(outputPath);
        return arguments;
    }

    private static Result<ChapterSplitReceipt> ValidateExisting(
        ChapterSplitRequest request,
        string destinationDirectory)
    {
        try
        {
            var outputs = Directory.GetFiles(destinationDirectory, "*" + Extension(request.OutputContainer));
            var expected = PlanOutputPaths(request, destinationDirectory);
            if (!expected.IsSuccess)
            {
                return Result<ChapterSplitReceipt>.Failure(expected.Error!);
            }

            if (outputs.Length != request.Chapters.Count ||
                !outputs.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(expected.Value))
            {
                return Failure("Media.ChapterSplitValidationFailed", "The existing chapter output folder is incomplete.");
            }

            long bytes = 0;
            foreach (var output in outputs)
            {
                var error = FfmpegMediaProcessor.ValidateOutput(output, request.OutputContainer);
                if (error is not null)
                {
                    return Result<ChapterSplitReceipt>.Failure(error);
                }

                bytes = checked(bytes + new FileInfo(output).Length);
            }

            return Result<ChapterSplitReceipt>.Success(new ChapterSplitReceipt(
                destinationDirectory,
                outputs.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                bytes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return Failure("Media.ChapterSplitValidationFailed", "The existing chapter output folder could not be verified.", exception.GetType().Name);
        }
    }

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static Result<IReadOnlyList<string>> PlanOutputPaths(
        ChapterSplitRequest request,
        string directory)
    {
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputs = new List<string>(request.Chapters.Count);
        for (var index = 0; index < request.Chapters.Count; index++)
        {
            var rendered = FileNameTemplate.Render(
                request.FileNameTemplate,
                request.FileNameContext with
                {
                    ChapterIndex = index + 1,
                    ChapterTitle = request.Chapters[index].Title
                });
            if (!rendered.IsSuccess)
            {
                return Result<IReadOnlyList<string>>.Failure(rendered.Error!);
            }

            var output = FileNamePolicy.AvailablePath(
                directory,
                rendered.Value,
                Extension(request.OutputContainer),
                path => planned.Contains(path));
            planned.Add(output);
            outputs.Add(output);
        }

        return Result<IReadOnlyList<string>>.Success(outputs);
    }

    private static string Extension(MediaContainer container) => container switch
    {
        MediaContainer.Mkv => ".mkv",
        MediaContainer.WebM => ".webm",
        _ => ".mp4"
    };

    private static void TryDeleteTemporaryDirectory(string temporaryDirectory, string destinationDirectory)
    {
        try
        {
            var parent = Path.GetDirectoryName(destinationDirectory);
            if (parent is null ||
                !Path.GetDirectoryName(temporaryDirectory)!.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(temporaryDirectory).StartsWith(
                    Path.GetFileName(destinationDirectory) + ".",
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(temporaryDirectory).EndsWith(".processing", StringComparison.Ordinal))
            {
                return;
            }

            Directory.Delete(temporaryDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static Result<ChapterSplitReceipt> Failure(string code, string message, string? detail = null) =>
        Result<ChapterSplitReceipt>.Failure(new TubeForgeError(code, message, detail));
}
