using TubeForge.Core.Media;
using TubeForge.Tests.Framework;
using TubeForge.Transcoding;

namespace TubeForge.Tests.Transcoding;

public static class FfmpegAudioTranscoderTests
{
    [Test]
    public static async Task BuildsSafeArgumentsAndValidatesEveryAudioOutputProfile()
    {
        foreach (var extension in new[] { ".m4a", ".webm" })
        {
            foreach (var testCase in OutputCases())
            {
                var directory = CreateTemporaryDirectory();
                try
                {
                    var executable = Path.Combine(directory, "ffmpeg.exe");
                    var source = Path.Combine(directory, "source" + extension);
                    var destination = Path.Combine(directory, "output" + testCase.Output.Extension);
                    await File.WriteAllBytesAsync(executable, []);
                    await File.WriteAllBytesAsync(source, [0x01, 0x02, 0x03]);
                    var runner = new ValidAudioProcessRunner();

                    var result = await new FfmpegAudioTranscoder(executable, runner).TranscodeAsync(
                        new AudioTranscodeRequest
                        {
                            SourcePath = source,
                            DestinationPath = destination,
                            Output = testCase.Output
                        });

                    Assert.True(result.IsSuccess, result.Error?.Message);
                    Assert.Equal(testCase.Output.BitrateKbps, result.Value.BitrateKbps);
                    Assert.True(result.Value.BytesWritten > 0);
                    Assert.True(AudioTranscodeFileValidator.Validate(destination, testCase.Output.Kind).IsSuccess);
                    Assert.True(ContainsAdjacent(runner.Arguments, "-i", source));
                    Assert.True(ContainsAdjacent(runner.Arguments, "-map", "0:a:0"));
                    Assert.True(ContainsAdjacent(runner.Arguments, "-c:a", testCase.Codec));
                    Assert.True(ContainsAdjacent(runner.Arguments, "-f", testCase.Format));
                    if (testCase.Output.BitrateKbps > 0)
                    {
                        Assert.True(ContainsAdjacent(
                            runner.Arguments,
                            "-b:a",
                            $"{testCase.Output.BitrateKbps}k"));
                    }
                    else
                    {
                        Assert.False(runner.Arguments.Contains("-b:a"));
                    }

                    Assert.True(runner.Arguments.Contains("-vn"));
                    Assert.True(runner.Arguments.Contains("-nostdin"));
                    Assert.True(runner.Arguments.Contains("-xerror"));
                    Assert.Equal(0, Directory.GetFiles(directory, "*.transcoding.*").Length);
                }
                finally
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }

    [Test]
    public static async Task AppliesTrimDuringAudioReencoding()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "ffmpeg.exe");
            var source = Path.Combine(directory, "source.m4a");
            var destination = Path.Combine(directory, "output.mp3");
            await File.WriteAllBytesAsync(executable, []);
            await File.WriteAllBytesAsync(source, [0x01]);
            var runner = new ValidAudioProcessRunner();
            Assert.True(MediaTrimRange.TryCreate(
                TimeSpan.FromSeconds(1.25),
                TimeSpan.FromSeconds(9.5),
                out var trim));

            var result = await new FfmpegAudioTranscoder(executable, runner).TranscodeAsync(
                new AudioTranscodeRequest
                {
                    SourcePath = source,
                    DestinationPath = destination,
                    Output = OutputProfile.Mp3(192),
                    Trim = trim,
                    RemovedSegments =
                    [
                        new MediaTrimRange(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4))
                    ]
                });

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.True(ContainsAdjacent(runner.Arguments, "-ss", "1.25"));
            Assert.True(ContainsAdjacent(runner.Arguments, "-t", "8.25"));
            Assert.True(
                Array.IndexOf(runner.Arguments.ToArray(), "-t") <
                Array.IndexOf(runner.Arguments.ToArray(), "-i"));
            Assert.True(ContainsAdjacent(runner.Arguments, "-c:a", "libmp3lame"));
            Assert.True(runner.Arguments.Any(argument =>
                argument.StartsWith(
                    "asetpts=PTS-STARTPTS,aselect=not(between(t\\,3\\,4))",
                    StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public static async Task CancelledConversionPublishesNoOutput()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "ffmpeg.exe");
            var source = Path.Combine(directory, "source.m4a");
            var destination = Path.Combine(directory, "output.mp3");
            await File.WriteAllBytesAsync(executable, []);
            await File.WriteAllBytesAsync(source, [0x01]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var result = await new FfmpegAudioTranscoder(
                executable,
                new ValidAudioProcessRunner()).TranscodeAsync(new AudioTranscodeRequest
                {
                    SourcePath = source,
                    DestinationPath = destination,
                    Output = OutputProfile.Mp3(320)
                }, cancellation.Token);

            Assert.False(result.IsSuccess);
            Assert.Equal("Operation.Cancelled", result.Error!.Code);
            Assert.False(File.Exists(destination));
            Assert.Equal(0, Directory.GetFiles(directory, "*.transcoding.mp3").Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public static async Task InvalidFfmpegOutputFailsClosedAndDeletesTemporaryFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "ffmpeg.exe");
            var source = Path.Combine(directory, "source.m4a");
            var destination = Path.Combine(directory, "output.mp3");
            await File.WriteAllBytesAsync(executable, []);
            await File.WriteAllBytesAsync(source, [0x01]);

            var result = await new FfmpegAudioTranscoder(
                executable,
                new InvalidOutputProcessRunner()).TranscodeAsync(new AudioTranscodeRequest
                {
                    SourcePath = source,
                    DestinationPath = destination,
                    Output = OutputProfile.Mp3(192)
                });

            Assert.False(result.IsSuccess);
            Assert.Equal("Media.InvalidTranscodeOutput", result.Error!.Code);
            Assert.False(File.Exists(destination));
            Assert.Equal(0, Directory.GetFiles(directory, "*.transcoding.mp3").Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public static async Task RecoversValidatedOutputPublishedBeforeQueueCheckpoint()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "ffmpeg.exe");
            var source = Path.Combine(directory, "source.m4a");
            var destination = Path.Combine(directory, "output.mp3");
            await File.WriteAllBytesAsync(executable, []);
            await File.WriteAllBytesAsync(source, [0x01]);
            var runner = new ValidAudioProcessRunner();
            var transcoder = new FfmpegAudioTranscoder(executable, runner);
            var request = new AudioTranscodeRequest
            {
                SourcePath = source,
                DestinationPath = destination,
                Output = OutputProfile.Mp3(192),
                AllowExistingValidatedOutput = true
            };
            var initial = await transcoder.TranscodeAsync(request);
            Assert.True(initial.IsSuccess, initial.Error?.Message);
            var originalBytes = await File.ReadAllBytesAsync(destination);
            var calls = runner.CallCount;
            File.Delete(source);

            var recovered = await transcoder.TranscodeAsync(request);

            Assert.True(recovered.IsSuccess, recovered.Error?.Message);
            Assert.Equal(originalBytes.LongLength, recovered.Value.BytesWritten);
            Assert.SequenceEqual(originalBytes, await File.ReadAllBytesAsync(destination));
            Assert.Equal(calls, runner.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public static async Task MissingFfmpegFailsBeforePublishingOutput()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(directory, "source.m4a");
            var destination = Path.Combine(directory, "output.mp3");
            await File.WriteAllBytesAsync(source, [0x01]);

            var result = await new FfmpegAudioTranscoder(
                Path.Combine(directory, "missing-ffmpeg.exe"),
                new ValidAudioProcessRunner()).TranscodeAsync(new AudioTranscodeRequest
                {
                    SourcePath = source,
                    DestinationPath = destination,
                    Output = OutputProfile.Mp3(192)
                });

            Assert.False(result.IsSuccess);
            Assert.Equal("Media.FFmpegMissing", result.Error!.Code);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public static void ValidatorRejectsNonMp3Bytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tubeforge-invalid-{Guid.NewGuid():N}.mp3");
        try
        {
            File.WriteAllBytes(path, [0x00, 0x11, 0x22, 0x33, 0x44]);
            var result = Mp3FileValidator.Validate(path);
            Assert.False(result.IsSuccess);
            Assert.Equal("Media.InvalidTranscodeOutput", result.Error!.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tubeforge-transcode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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

    private static IReadOnlyList<OutputCase> OutputCases() =>
    [
        new(OutputProfile.Mp3(128), "libmp3lame", "mp3"),
        new(OutputProfile.Mp3(192), "libmp3lame", "mp3"),
        new(OutputProfile.Mp3(256), "libmp3lame", "mp3"),
        new(OutputProfile.Mp3(320), "libmp3lame", "mp3"),
        new(OutputProfile.Aac(256), "aac", "mp4"),
        new(OutputProfile.Opus(160), "libopus", "ogg"),
        new(OutputProfile.Wav, "pcm_s16le", "wav"),
        new(OutputProfile.Flac, "flac", "flac")
    ];

    private sealed record OutputCase(OutputProfile Output, string Codec, string Format);

    private sealed class ValidAudioProcessRunner : IFfmpegAudioProcessRunner
    {
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
            await File.WriteAllBytesAsync(arguments[^1], ValidBytes(arguments[^1]), cancellationToken);
            return 0;
        }

        private static byte[] ValidBytes(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => [
                0xff, 0xfb, 0x90, 0x64, 0x00, 0x00, 0x00, 0x00,
                0xff, 0xfb, 0x90, 0x64, 0x00, 0x00, 0x00, 0x00
            ],
            ".m4a" => [0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'M', (byte)'4', (byte)'A', (byte)' '],
            ".ogg" => [(byte)'O', (byte)'g', (byte)'g', (byte)'S'],
            ".wav" => [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'E'],
            ".flac" => [(byte)'f', (byte)'L', (byte)'a', (byte)'C'],
            _ => throw new InvalidOperationException("Unexpected test output extension.")
        };
    }

    private sealed class InvalidOutputProcessRunner : IFfmpegAudioProcessRunner
    {
        public async Task<int> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            await File.WriteAllBytesAsync(arguments[^1], [0x00, 0x11, 0x22], cancellationToken);
            return 0;
        }
    }
}
