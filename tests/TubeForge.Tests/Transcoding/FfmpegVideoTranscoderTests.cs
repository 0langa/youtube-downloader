using TubeForge.Core.Media;
using TubeForge.Tests.Framework;
using TubeForge.Transcoding;

namespace TubeForge.Tests.Transcoding;

public static class FfmpegVideoTranscoderTests
{
    [Test]
    public static async Task BuildsAllowlistedArgumentsAndPublishesEveryVideoProfile()
    {
        foreach (var testCase in Cases())
        {
            using var directory = new TestDirectory();
            var executable = Path.Combine(directory.Path, "ffmpeg.exe");
            var source = Path.Combine(directory.Path, "source.mkv");
            var destination = Path.Combine(directory.Path, "output" + testCase.Output.Extension);
            await File.WriteAllBytesAsync(executable, []);
            await File.WriteAllBytesAsync(source, [0x01]);
            var runner = new ValidVideoProcessRunner();

            var result = await new FfmpegVideoTranscoder(executable, runner).TranscodeAsync(
                new VideoTranscodeRequest
                {
                    SourcePath = source,
                    DestinationPath = destination,
                    Output = testCase.Output
                });

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.True(result.Value.BytesWritten > 0);
            Assert.Equal(testCase.Output, result.Value.Output);
            Assert.True(ContainsAdjacent(runner.Arguments, "-map", "0:v:0"));
            Assert.True(ContainsAdjacent(runner.Arguments, "-map", "0:a:0"));
            Assert.True(ContainsAdjacent(runner.Arguments, "-c:v", testCase.VideoEncoder));
            Assert.True(ContainsAdjacent(runner.Arguments, "-c:a", testCase.AudioEncoder));
            Assert.True(ContainsAdjacent(runner.Arguments, "-f", testCase.Format));
            Assert.True(ContainsAdjacent(
                runner.Arguments,
                "-b:v",
                $"{testCase.Output.VideoBitrateKbps}k"));
            Assert.True(ContainsAdjacent(
                runner.Arguments,
                "-b:a",
                $"{testCase.Output.BitrateKbps}k"));
            if (testCase.Output.Kind == OutputProfileKind.H265AacMp4)
            {
                Assert.True(ContainsAdjacent(
                    runner.Arguments,
                    "-vf",
                    "pad=ceil(iw/8)*8:ceil(ih/8)*8"));
            }
            else if (testCase.Output.Kind == OutputProfileKind.H264AacMp4)
            {
                Assert.True(ContainsAdjacent(
                    runner.Arguments,
                    "-maxrate",
                    $"{testCase.Output.VideoBitrateKbps}k"));
                Assert.True(ContainsAdjacent(
                    runner.Arguments,
                    "-bufsize",
                    $"{testCase.Output.VideoBitrateKbps * 2}k"));
            }

            Assert.True(runner.Arguments.Contains("-nostdin"));
            Assert.True(runner.Arguments.Contains("-xerror"));
            Assert.Equal(0, Directory.GetFiles(directory.Path, "*.transcoding.*").Length);
        }
    }

    [Test]
    public static async Task AppliesTrimDuringVideoReencoding()
    {
        using var directory = new TestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var source = Path.Combine(directory.Path, "source.mkv");
        var destination = Path.Combine(directory.Path, "output.mp4");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(source, [0x01]);
        var runner = new ValidVideoProcessRunner();
        Assert.True(MediaTrimRange.TryCreate(
            TimeSpan.FromSeconds(2.5),
            TimeSpan.FromSeconds(12.75),
            out var trim));

        var result = await new FfmpegVideoTranscoder(executable, runner).TranscodeAsync(
            new VideoTranscodeRequest
            {
                SourcePath = source,
                DestinationPath = destination,
                Output = OutputProfile.H264AacMp4,
                Trim = trim,
                RemovedSegments =
                [
                    new MediaTrimRange(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4)),
                    new MediaTrimRange(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(7))
                ]
            });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(ContainsAdjacent(runner.Arguments, "-ss", "2.5"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-t", "10.25"));
        Assert.True(
            Array.IndexOf(runner.Arguments.ToArray(), "-t") <
            Array.IndexOf(runner.Arguments.ToArray(), "-i"));
        Assert.True(ContainsAdjacent(runner.Arguments, "-c:v", "libopenh264"));
        Assert.True(runner.Arguments.Any(argument =>
            argument.StartsWith("setpts=PTS-STARTPTS,select=not(between(t\\,3\\,4)+between(t\\,6\\,7))", StringComparison.Ordinal)));
        Assert.True(runner.Arguments.Any(argument =>
            argument.StartsWith("asetpts=PTS-STARTPTS,aselect=not", StringComparison.Ordinal)));
    }

    [Test]
    public static async Task CombinesSponsorRemovalWithH265PaddingFilter()
    {
        using var directory = new TestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var source = Path.Combine(directory.Path, "source.mkv");
        var destination = Path.Combine(directory.Path, "output.mp4");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(source, [0x01]);
        var runner = new ValidVideoProcessRunner();

        var result = await new FfmpegVideoTranscoder(executable, runner).TranscodeAsync(
            new VideoTranscodeRequest
            {
                SourcePath = source,
                DestinationPath = destination,
                Output = OutputProfile.H265AacMp4,
                RemovedSegments =
                [
                    new MediaTrimRange(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4))
                ]
            });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(runner.Arguments.Any(argument =>
            argument.Contains("select=not", StringComparison.Ordinal) &&
            argument.Contains("pad=ceil(iw/8)*8:ceil(ih/8)*8", StringComparison.Ordinal)));
        Assert.Equal(1, runner.Arguments.Count(argument => argument == "-vf"));
    }

    [Test]
    public static async Task RecoversPublishedOutputWhenSourceWasAlreadyRemoved()
    {
        using var directory = new TestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var destination = Path.Combine(directory.Path, "output.mp4");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(destination, ValidVideoProcessRunner.ValidMp4);
        var runner = new ValidVideoProcessRunner();

        var result = await new FfmpegVideoTranscoder(executable, runner).TranscodeAsync(
            new VideoTranscodeRequest
            {
                SourcePath = Path.Combine(directory.Path, "removed-source.mkv"),
                DestinationPath = destination,
                Output = OutputProfile.H264AacMp4,
                AllowExistingValidatedOutput = true
            });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(0, runner.CallCount);
    }

    [Test]
    public static async Task InvalidOutputFailsClosedAndDeletesTemporaryFile()
    {
        using var directory = new TestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var source = Path.Combine(directory.Path, "source.mkv");
        var destination = Path.Combine(directory.Path, "output.webm");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(source, [0x01]);

        var result = await new FfmpegVideoTranscoder(
            executable,
            new InvalidVideoProcessRunner()).TranscodeAsync(new VideoTranscodeRequest
            {
                SourcePath = source,
                DestinationPath = destination,
                Output = OutputProfile.Vp9OpusWebM
            });

        Assert.False(result.IsSuccess);
        Assert.Equal("Media.InvalidStructure", result.Error!.Code);
        Assert.False(File.Exists(destination));
        Assert.Equal(0, Directory.GetFiles(directory.Path, "*.transcoding.*").Length);
    }

    [Test]
    public static async Task CancelledConversionPublishesNoOutput()
    {
        using var directory = new TestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var source = Path.Combine(directory.Path, "source.mkv");
        var destination = Path.Combine(directory.Path, "output.mp4");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(source, [0x01]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new FfmpegVideoTranscoder(
            executable,
            new ValidVideoProcessRunner()).TranscodeAsync(new VideoTranscodeRequest
            {
                SourcePath = source,
                DestinationPath = destination,
                Output = OutputProfile.H264AacMp4
            }, cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("Operation.Cancelled", result.Error!.Code);
        Assert.False(File.Exists(destination));
        Assert.Equal(0, Directory.GetFiles(directory.Path, "*.transcoding.*").Length);
    }

    private static IReadOnlyList<VideoCase> Cases() =>
    [
        new(OutputProfile.H264AacMp4, "libopenh264", "aac", "mp4"),
        new(OutputProfile.H265AacMp4, "libkvazaar", "aac", "mp4"),
        new(OutputProfile.Vp9OpusWebM, "libvpx-vp9", "libopus", "webm")
    ];

    private static bool ContainsAdjacent(IReadOnlyList<string> arguments, string first, string second)
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

    private sealed record VideoCase(
        OutputProfile Output,
        string VideoEncoder,
        string AudioEncoder,
        string Format);

    private sealed class ValidVideoProcessRunner : IFfmpegVideoProcessRunner
    {
        public static readonly byte[] ValidMp4 = [
            0, 0, 0, 16,
            (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m',
            0, 0, 0, 0
        ];

        private static readonly byte[] ValidWebM = [0x1a, 0x45, 0xdf, 0xa3, 0x81, 0x00];

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public int CallCount { get; private set; }

        public async Task<int> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Arguments = arguments.ToArray();
            CallCount++;
            var bytes = Path.GetExtension(arguments[^1]).Equals(".webm", StringComparison.OrdinalIgnoreCase)
                ? ValidWebM
                : ValidMp4;
            await File.WriteAllBytesAsync(arguments[^1], bytes, cancellationToken);
            return 0;
        }
    }

    private sealed class InvalidVideoProcessRunner : IFfmpegVideoProcessRunner
    {
        public async Task<int> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            await File.WriteAllBytesAsync(arguments[^1], [0x00, 0x01, 0x02, 0x03], cancellationToken);
            return 0;
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        private static readonly string SafeRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TubeForge.Tests"));

        public TestDirectory()
        {
            Path = System.IO.Path.Combine(SafeRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(SafeRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to clean a test directory outside the safe root.");
            }

            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
