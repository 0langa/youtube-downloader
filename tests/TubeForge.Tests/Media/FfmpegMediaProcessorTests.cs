using TubeForge.Core.Media;
using TubeForge.Media;
using TubeForge.Tests.Framework;

namespace TubeForge.Tests.Media;

public static class FfmpegMediaProcessorTests
{
    [Test]
    public static async Task MuxUsesStreamCopyFastStartAndPublishesConventionalMp4()
    {
        using var directory = new MediaTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var video = Path.Combine(directory.Path, "video.mp4");
        var audio = Path.Combine(directory.Path, "audio.m4a");
        var output = Path.Combine(directory.Path, "output.mp4");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(video, SyntheticMp4.Track(
            "vide", "VIDEO"u8, 1, 90_000, 90_000));
        await File.WriteAllBytesAsync(audio, SyntheticMp4.Track(
            "soun", "AUDIO"u8, 1, 48_000, 48_000));
        var runner = new CopyingProcessRunner(video);
        var processor = new FfmpegMediaProcessor(executable, runner);

        var result = await processor.MuxMp4Async(video, audio, output);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(File.Exists(output));
        Assert.Equal(2, runner.Arguments.Count(argument => argument == "-i"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-c", "copy"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-movflags", "+faststart"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-map", "0:v:0"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-map", "1:a:0"));
        Assert.True(runner.Arguments.Contains("-nostdin"));
        Assert.True(runner.Arguments.Contains("-xerror"));
    }

    [Test]
    public static async Task MuxWebMUsesStreamCopyWithoutFastStartAndPublishesWebM()
    {
        using var directory = new MediaTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var video = Path.Combine(directory.Path, "video.webm");
        var audio = Path.Combine(directory.Path, "audio.webm");
        var output = Path.Combine(directory.Path, "output.webm");
        var muxed = Path.Combine(directory.Path, "muxed-source.webm");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(video, SyntheticWebM.Track(1, "V_VP9", (0, "VIDEO"u8.ToArray())));
        await File.WriteAllBytesAsync(audio, SyntheticWebM.Track(2, "A_OPUS", (0, "AUDIO"u8.ToArray())));
        await File.WriteAllBytesAsync(muxed, SyntheticWebM.Track(
            1, "V_VP9", (0, "VIDEO"u8.ToArray()), (100, "AUDIO"u8.ToArray())));
        var runner = new CopyingProcessRunner(muxed);
        var processor = new FfmpegMediaProcessor(executable, runner);

        var result = await processor.MuxAsync(video, audio, output, MediaContainer.WebM);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(File.Exists(output));
        Assert.Equal(2, runner.Arguments.Count(argument => argument == "-i"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-c", "copy"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-f", "webm"));
        Assert.False(runner.Arguments.Contains("+faststart"));
    }

    [Test]
    public static async Task MuxMkvUsesMatroskaStreamCopyAndValidatesEbml()
    {
        using var directory = new MediaTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var video = Path.Combine(directory.Path, "video.mp4");
        var audio = Path.Combine(directory.Path, "audio.webm");
        var output = Path.Combine(directory.Path, "output.mkv");
        var muxed = Path.Combine(directory.Path, "muxed-source.mkv");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(video, SyntheticMp4.Track("vide", "VIDEO"u8, 1, 90_000, 90_000));
        await File.WriteAllBytesAsync(audio, SyntheticWebM.Track(2, "A_OPUS", (0, "AUDIO"u8.ToArray())));
        await File.WriteAllBytesAsync(muxed, SyntheticWebM.Track(
            1, "V_VP9", (0, "VIDEO"u8.ToArray()), (100, "AUDIO"u8.ToArray())));
        var runner = new CopyingProcessRunner(muxed);
        var processor = new FfmpegMediaProcessor(executable, runner);

        var result = await processor.MuxAsync(video, audio, output, MediaContainer.Mkv);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(File.Exists(output));
        Assert.True(ContainsAdjacent(runner.Arguments, "-c", "copy"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-f", "matroska"));
        Assert.False(runner.Arguments.Contains("+faststart"));
    }

    [Test]
    public static async Task RemuxesCapturedHlsSourceIntoValidatedMkv()
    {
        using var directory = new MediaTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var source = Path.Combine(directory.Path, "capture.hls-source");
        var output = Path.Combine(directory.Path, "capture.mkv");
        var muxed = Path.Combine(directory.Path, "muxed-source.mkv");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(source, "synthetic transport stream"u8.ToArray());
        await File.WriteAllBytesAsync(muxed, SyntheticWebM.Track(
            1, "V_VP9", (0, "VIDEO"u8.ToArray()), (100, "AUDIO"u8.ToArray())));
        var runner = new CopyingProcessRunner(muxed);

        var result = await new FfmpegMediaProcessor(executable, runner)
            .RemuxHlsCaptureAsync(source, output);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(File.Exists(output));
        Assert.True(ContainsAdjacent(runner.Arguments, "-map", "0:v?"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-map", "0:a?"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-f", "matroska"));
        Assert.Equal(0, Directory.GetFiles(directory.Path, "*.processing.*").Length);
    }

    [Test]
    public static async Task TrimsWithBoundedStreamCopyAndAtomicPublication()
    {
        using var directory = new MediaTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var media = Path.Combine(directory.Path, "source.mp4");
        var output = Path.Combine(directory.Path, "output.mp4");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(media, SyntheticMp4.Track(
            "vide", "VIDEO"u8, 1, 90_000, 90_000));
        var runner = new CopyingProcessRunner(media);
        Assert.True(MediaTrimRange.TryCreate(
            TimeSpan.FromSeconds(5.25),
            TimeSpan.FromSeconds(35.75),
            out var trim));

        var result = await new FfmpegMediaProcessor(executable, runner).TrimStreamCopyAsync(
            media,
            output,
            MediaContainer.Mp4,
            trim);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(File.Exists(output));
        Assert.True(ContainsAdjacent(runner.Arguments, "-ss", "5.25"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-t", "30.5"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-c", "copy"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-avoid_negative_ts", "make_zero"));
        Assert.Equal(0, Directory.GetFiles(directory.Path, "*.processing.*").Length);
    }

    [Test]
    public static async Task EmbedsSoftSubtitlesWithContainerSpecificCodecAndAtomicPublication()
    {
        foreach (var testCase in new[]
                 {
                     (MediaContainer.Mp4, ".mp4", "mov_text", SyntheticMp4.Track(
                         "vide", "VIDEO"u8, 1, 90_000, 90_000)),
                     (MediaContainer.Mkv, ".mkv", "srt", SyntheticWebM.Track(
                         1, "V_VP9", (0, "VIDEO"u8.ToArray()))),
                     (MediaContainer.WebM, ".webm", "webvtt", SyntheticWebM.Track(
                         1, "V_VP9", (0, "VIDEO"u8.ToArray())))
                 })
        {
            using var directory = new MediaTestDirectory();
            var executable = Path.Combine(directory.Path, "ffmpeg.exe");
            var media = Path.Combine(directory.Path, "source" + testCase.Item2);
            var caption = Path.Combine(directory.Path, "caption.srt");
            var output = Path.Combine(directory.Path, "output" + testCase.Item2);
            await File.WriteAllBytesAsync(executable, []);
            await File.WriteAllBytesAsync(media, testCase.Item4);
            await File.WriteAllTextAsync(caption, "1\r\n00:00:00,000 --> 00:00:01,000\r\nCaption\r\n");
            var runner = new CopyingProcessRunner(media);

            var result = await new FfmpegMediaProcessor(executable, runner).EmbedSubtitleAsync(
                media,
                caption,
                output,
                testCase.Item1,
                new CaptionEmbedSelection("en-US", IsAutoGenerated: false));

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.True(File.Exists(output));
            Assert.True(ContainsAdjacent(runner.Arguments, "-map", "0:v:0"));
            Assert.True(ContainsAdjacent(runner.Arguments, "-map", "0:a?"));
            Assert.True(ContainsAdjacent(runner.Arguments, "-map", "1:0"));
            Assert.True(ContainsAdjacent(runner.Arguments, "-c:s", testCase.Item3));
            Assert.True(ContainsAdjacent(runner.Arguments, "-metadata:s:s:0", "language=eng"));
            Assert.Equal(0, Directory.GetFiles(directory.Path, "*.processing.*").Length);
        }
    }

    [Test]
    public static async Task EmbedsMultipleOrderedSoftSubtitleTracks()
    {
        using var directory = new MediaTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var media = Path.Combine(directory.Path, "source.mkv");
        var english = Path.Combine(directory.Path, "english.srt");
        var german = Path.Combine(directory.Path, "german.srt");
        var output = Path.Combine(directory.Path, "output.mkv");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(media, SyntheticWebM.Track(
            1, "V_VP9", (0, "VIDEO"u8.ToArray())));
        await File.WriteAllTextAsync(english, "1\r\n00:00:00,000 --> 00:00:01,000\r\nEnglish\r\n");
        await File.WriteAllTextAsync(german, "1\r\n00:00:00,000 --> 00:00:01,000\r\nGerman\r\n");
        Assert.True(CaptionEmbedSelectionSet.TryCreate(
            [
                new CaptionEmbedSelection("en", IsAutoGenerated: false),
                new CaptionEmbedSelection("de", IsAutoGenerated: true)
            ],
            out var captions));
        var runner = new CopyingProcessRunner(media);
        var processor = new FfmpegMediaProcessor(executable, runner);

        var mismatch = await processor.EmbedSubtitlesAsync(
            media,
            [english],
            output,
            MediaContainer.Mkv,
            captions);
        Assert.False(mismatch.IsSuccess);
        Assert.Equal("Media.InvalidCaptionSelection", mismatch.Error?.Code);
        Assert.Equal(0, runner.CallCount);

        var result = await processor.EmbedSubtitlesAsync(
            media,
            [english, german],
            output,
            MediaContainer.Mkv,
            captions);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(ContainsAdjacent(runner.Arguments, "-map", "1:0"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-map", "2:0"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-metadata:s:s:0", "language=eng"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-metadata:s:s:1", "language=deu"));
    }

    [Test]
    public static async Task EmbedsChaptersAndSubtitleInOneAtomicFinalizationPass()
    {
        using var directory = new MediaTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var media = Path.Combine(directory.Path, "source.mp4");
        var caption = Path.Combine(directory.Path, "caption.srt");
        var output = Path.Combine(directory.Path, "output.mp4");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(media, SyntheticMp4.Track(
            "vide", "VIDEO"u8, 1, 90_000, 90_000));
        await File.WriteAllTextAsync(caption, "1\r\n00:00:00,000 --> 00:00:01,000\r\nCaption\r\n");
        var runner = new ChapterProcessRunner(media);

        var result = await new FfmpegMediaProcessor(executable, runner).EmbedMetadataAsync(
            media,
            output,
            MediaContainer.Mp4,
            [
                new VideoChapter { Title = "Intro #1", StartTime = TimeSpan.Zero },
                new VideoChapter { Title = "Main = topic", StartTime = TimeSpan.FromSeconds(30) }
            ],
            TimeSpan.FromMinutes(1),
            caption,
            new CaptionEmbedSelection("en", IsAutoGenerated: false));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(File.Exists(output));
        Assert.Equal(3, runner.CallCount);
        Assert.Equal(3, runner.Arguments.Count(argument => argument == "-i"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-map_chapters", "2"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-map_metadata", "2"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-c:s", "mov_text"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-metadata:s:s:0", "language=eng"));
        Assert.True(runner.ChapterMetadata.Contains("title=Intro \\#1", StringComparison.Ordinal));
        Assert.True(runner.ChapterMetadata.Contains("title=Main \\= topic", StringComparison.Ordinal));
        Assert.Equal(0, Directory.GetFiles(directory.Path, "*.ffmetadata").Length);
    }

    [Test]
    public static async Task RejectsUnsafeChapterTimelineBeforeStartingFfmpeg()
    {
        using var directory = new MediaTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var media = Path.Combine(directory.Path, "source.mp4");
        var output = Path.Combine(directory.Path, "output.mp4");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(media, SyntheticMp4.Track(
            "vide", "VIDEO"u8, 1, 90_000, 90_000));
        var runner = new ChapterProcessRunner(media);

        var result = await new FfmpegMediaProcessor(executable, runner).EmbedMetadataAsync(
            media,
            output,
            MediaContainer.Mp4,
            [
                new VideoChapter { Title = "Later", StartTime = TimeSpan.FromSeconds(30) },
                new VideoChapter { Title = "Earlier", StartTime = TimeSpan.FromSeconds(10) }
            ],
            TimeSpan.FromMinutes(1));

        Assert.False(result.IsSuccess);
        Assert.Equal("Media.InvalidChapters", result.Error?.Code);
        Assert.Equal(0, runner.CallCount);
        Assert.False(File.Exists(output));
    }

    [Test]
    public static async Task RecoversPublishedCaptionEmbedWithoutSourceFiles()
    {
        using var directory = new MediaTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var output = Path.Combine(directory.Path, "output.mp4");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(output, SyntheticMp4.Track(
            "vide", "VIDEO"u8, 1, 90_000, 90_000));
        var runner = new CopyingProcessRunner(output);

        var result = await new FfmpegMediaProcessor(executable, runner).EmbedSubtitleAsync(
            Path.Combine(directory.Path, "removed-source.mp4"),
            Path.Combine(directory.Path, "removed-caption.srt"),
            output,
            MediaContainer.Mp4,
            new CaptionEmbedSelection("de", IsAutoGenerated: true),
            allowExistingValidatedOutput: true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, runner.CallCount);
    }

    [Test]
    public static async Task RejectsRecoveredContainerWithoutSubtitleStream()
    {
        using var directory = new MediaTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var output = Path.Combine(directory.Path, "output.mp4");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(output, SyntheticMp4.Track(
            "vide", "VIDEO"u8, 1, 90_000, 90_000));

        var result = await new FfmpegMediaProcessor(
            executable,
            new SubtitleProbeFailingRunner()).EmbedSubtitleAsync(
                Path.Combine(directory.Path, "removed-source.mp4"),
                Path.Combine(directory.Path, "removed-caption.srt"),
                output,
                MediaContainer.Mp4,
                new CaptionEmbedSelection("en", IsAutoGenerated: false),
                allowExistingValidatedOutput: true);

        Assert.False(result.IsSuccess);
        Assert.Equal("Media.SubtitleValidationFailed", result.Error?.Code);
        Assert.True(File.Exists(output));
    }

    [Test]
    public static async Task RejectsFragmentedProcessorOutputWithoutPublishingDestination()
    {
        using var directory = new MediaTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var source = Path.Combine(directory.Path, "fragmented.mp4");
        var output = Path.Combine(directory.Path, "output.mp4");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(source, SyntheticMp4.FragmentedTrack(
            "vide", 1, 1000, (0, "VIDEO"u8.ToArray())));
        var processor = new FfmpegMediaProcessor(executable, new CopyingProcessRunner(source));

        var result = await processor.RemuxMp4Async(source, output);

        Assert.False(result.IsSuccess);
        Assert.Equal("Media.IncompatibleMp4Layout", result.Error?.Code);
        Assert.False(File.Exists(output));
        Assert.Equal(0, Directory.GetFiles(directory.Path, "*.processing.mp4").Length);
    }

    [Test]
    public static async Task FailsClosedWhenBundledFfmpegIsMissing()
    {
        using var directory = new MediaTestDirectory();
        var source = Path.Combine(directory.Path, "source.mp4");
        var output = Path.Combine(directory.Path, "output.mp4");
        await File.WriteAllBytesAsync(source, SyntheticMp4.Track(
            "vide", "VIDEO"u8, 1, 90_000, 90_000));
        var processor = new FfmpegMediaProcessor(
            Path.Combine(directory.Path, "missing-ffmpeg.exe"),
            new CopyingProcessRunner(source));

        var result = await processor.RemuxMp4Async(source, output);

        Assert.False(result.IsSuccess);
        Assert.Equal("Media.FFmpegMissing", result.Error?.Code);
        Assert.False(File.Exists(output));
    }

    [Test]
    public static async Task FailureMessageNamesSelectedOutputContainer()
    {
        using var directory = new MediaTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var video = Path.Combine(directory.Path, "video.webm");
        var audio = Path.Combine(directory.Path, "audio.webm");
        var output = Path.Combine(directory.Path, "output.webm");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(video, []);
        await File.WriteAllBytesAsync(audio, []);
        var processor = new FfmpegMediaProcessor(executable, new FailingProcessRunner());

        var result = await processor.MuxAsync(video, audio, output, MediaContainer.WebM);

        Assert.False(result.IsSuccess);
        Assert.Equal("Media.FFmpegFailed", result.Error?.Code);
        Assert.Equal("FFmpeg could not finalize this WebM file.", result.Error?.Message);
        Assert.False(File.Exists(output));
    }

    [Test]
    public static async Task RecoversValidatedOutputPublishedBeforeQueueCheckpoint()
    {
        using var directory = new MediaTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var source = Path.Combine(directory.Path, "source.mp4");
        var output = Path.Combine(directory.Path, "output.mp4");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(source, SyntheticMp4.Track(
            "vide", "VIDEO"u8, 1, 90_000, 90_000));
        var runner = new CopyingProcessRunner(source);
        var processor = new FfmpegMediaProcessor(executable, runner);
        var first = await processor.RemuxMp4Async(source, output);
        Assert.True(first.IsSuccess, first.Error?.Message);
        File.Delete(source);

        var recovered = await processor.RemuxMp4Async(
            source,
            output,
            allowExistingValidatedOutput: true);

        Assert.True(recovered.IsSuccess, recovered.Error?.Message);
        Assert.Equal(first.Value.BytesWritten, recovered.Value.BytesWritten);
    }

    private static bool ContainsAdjacent(
        IReadOnlyList<string> arguments,
        string first,
        string second)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (arguments[index] == first && arguments[index + 1] == second)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CopyingProcessRunner(string sourcePath) : IFfmpegProcessRunner
    {
        private readonly List<IReadOnlyList<string>> _calls = [];

        public IReadOnlyList<string> Arguments => _calls.FirstOrDefault() ?? [];

        public int CallCount { get; private set; }

        public async Task<int> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            CallCount++;
            _calls.Add(arguments.ToArray());
            if (ContainsAdjacent(arguments, "-f", "null"))
            {
                return 0;
            }

            await using var source = File.OpenRead(sourcePath);
            await using var destination = new FileStream(
                arguments[^1],
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous);
            await source.CopyToAsync(destination, cancellationToken);
            return 0;
        }
    }

    private sealed class SubtitleProbeFailingRunner : IFfmpegProcessRunner
    {
        public Task<int> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken) => Task.FromResult(23);
    }

    private sealed class ChapterProcessRunner(string sourcePath) : IFfmpegProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public string ChapterMetadata { get; private set; } = string.Empty;

        public int CallCount { get; private set; }

        public async Task<int> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
            {
                Arguments = arguments.ToArray();
                var inputIndexes = arguments
                    .Select((argument, index) => (argument, index))
                    .Where(item => item.argument == "-i")
                    .Select(item => item.index + 1)
                    .ToArray();
                ChapterMetadata = await File.ReadAllTextAsync(arguments[inputIndexes[^1]], cancellationToken);
                await using var source = File.OpenRead(sourcePath);
                await using var destination = new FileStream(
                    arguments[^1],
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous);
                await source.CopyToAsync(destination, cancellationToken);
                return 0;
            }

            if (ContainsAdjacent(arguments, "-f", "ffmetadata"))
            {
                await File.WriteAllTextAsync(arguments[^1], ChapterMetadata, cancellationToken);
            }

            return 0;
        }
    }

    private sealed class FailingProcessRunner : IFfmpegProcessRunner
    {
        public Task<int> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken) => Task.FromResult(17);
    }
}
