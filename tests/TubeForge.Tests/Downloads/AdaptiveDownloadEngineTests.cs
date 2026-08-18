using System.Net;
using TubeForge.Core.Media;
using TubeForge.Downloads;
using TubeForge.Media;
using TubeForge.Tests.Framework;
using TubeForge.Tests.Media;

namespace TubeForge.Tests.Downloads;

public static class AdaptiveDownloadEngineTests
{
    [Test]
    public static async Task DownloadsBothTracksMuxesAndRemovesIntermediates()
    {
        using var directory = new AdaptiveTestDirectory();
        var video = SyntheticMp4.Track("vide", "VIDEO-SAMPLES"u8, 1, 90_000, 450_000);
        var audio = SyntheticMp4.Track("soun", "AUDIO-SAMPLES"u8, 1, 48_000, 240_000);
        using var handler = new MediaHandler(video, audio);
        using var client = new HttpClient(handler);
        var direct = new DirectDownloadEngine(
            client,
            DownloadUriPolicy.YouTubeMediaAndLoopback,
            (_, _) => Task.CompletedTask);
        var engine = new AdaptiveDownloadEngine(direct);
        var output = Path.Combine(directory.Path, "combined.mp4");
        var videoTrack = output + ".video-track.mp4";
        var audioTrack = output + ".audio-track.m4a";

        var result = await engine.DownloadAsync(new AdaptiveDownloadRequest
        {
            Video = Request("video", videoTrack, video.Length, MediaContainer.Mp4),
            Audio = Request("audio", audioTrack, audio.Length, MediaContainer.Mp4),
            DestinationPath = output,
            OutputContainer = MediaContainer.Mp4
        });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(videoTrack));
        Assert.False(File.Exists(audioTrack));
        Assert.Equal(new FileInfo(output).Length, result.Value.BytesWritten);
        Assert.True(result.Value.BytesWritten > Math.Max(video.Length, audio.Length));
    }

    [Test]
    public static async Task SignalsEndOfTheNetworkPhaseBeforeMuxingStarts()
    {
        using var directory = new AdaptiveTestDirectory();
        var video = SyntheticMp4.Track("vide", "VIDEO-SAMPLES"u8, 1, 90_000, 450_000);
        var audio = SyntheticMp4.Track("soun", "AUDIO-SAMPLES"u8, 1, 48_000, 240_000);
        using var handler = new MediaHandler(video, audio);
        using var client = new HttpClient(handler);
        var direct = new DirectDownloadEngine(
            client,
            DownloadUriPolicy.YouTubeMediaAndLoopback,
            (_, _) => Task.CompletedTask);
        var engine = new AdaptiveDownloadEngine(direct);
        var output = Path.Combine(directory.Path, "combined.mp4");
        var videoTrack = output + ".video-track.mp4";
        var audioTrack = output + ".audio-track.m4a";
        var signals = 0;
        var tracksOnDiskAtSignal = false;
        var outputExistedAtSignal = true;

        var result = await engine.DownloadAsync(
            new AdaptiveDownloadRequest
            {
                Video = Request("video", videoTrack, video.Length, MediaContainer.Mp4),
                Audio = Request("audio", audioTrack, audio.Length, MediaContainer.Mp4),
                DestinationPath = output,
                OutputContainer = MediaContainer.Mp4
            },
            progress: null,
            cancellationToken: default,
            networkPhaseCompleted: () =>
            {
                signals++;
                tracksOnDiskAtSignal = File.Exists(videoTrack) && File.Exists(audioTrack);
                outputExistedAtSignal = File.Exists(output);
            });

        Assert.True(result.IsSuccess, result.Error?.Message);
        // The caller releases its per-host transfer slot on this signal, so it has to arrive once
        // both tracks are down and before the local mux — otherwise the slot stays held for the
        // length of the mux and blocks every other transfer.
        Assert.Equal(1, signals);
        Assert.True(tracksOnDiskAtSignal);
        Assert.False(outputExistedAtSignal);
    }

    [Test]
    public static async Task RecoversPublishedMp4BeforeRedownloadingMissingTracks()
    {
        using var directory = new AdaptiveTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var output = Path.Combine(directory.Path, "combined.mp4");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllBytesAsync(output, SyntheticMp4.Track(
            "vide", "VIDEO-SAMPLES"u8, 1, 90_000, 450_000));
        using var client = new HttpClient(new RejectingHandler());
        var direct = new DirectDownloadEngine(
            client,
            DownloadUriPolicy.YouTubeMediaAndLoopback,
            (_, _) => Task.CompletedTask);
        var processor = new FfmpegMediaProcessor(executable, new RejectingProcessRunner());
        var engine = new AdaptiveDownloadEngine(direct, processor);

        var result = await engine.DownloadAsync(new AdaptiveDownloadRequest
        {
            Video = Request("video", output + ".missing-video.mp4", 100, MediaContainer.Mp4),
            Audio = Request("audio", output + ".missing-audio.m4a", 100, MediaContainer.Mp4),
            DestinationPath = output,
            OutputContainer = MediaContainer.Mp4,
            AllowExistingValidatedOutput = true
        });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new FileInfo(output).Length, result.Value.BytesWritten);
        Assert.Equal(0L, result.Value.VideoBytes);
        Assert.Equal(0L, result.Value.AudioBytes);
    }

    [Test]
    public static async Task WebMOutputRoutesThroughFfmpegWhenAvailable()
    {
        using var directory = new AdaptiveTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var muxed = Path.Combine(directory.Path, "ffmpeg-output.webm");
        await File.WriteAllBytesAsync(executable, []);
        var muxedBytes = SyntheticWebM.Track(
            1, "V_VP9", (0, "MUXED-V"u8.ToArray()), (100, "MUXED-A"u8.ToArray()));
        await File.WriteAllBytesAsync(muxed, muxedBytes);
        var video = SyntheticWebM.Track(1, "V_VP9", (0, "VIDEO"u8.ToArray()));
        var audio = SyntheticWebM.Track(2, "A_OPUS", (0, "AUDIO"u8.ToArray()));
        using var handler = new MediaHandler(video, audio);
        using var client = new HttpClient(handler);
        var direct = new DirectDownloadEngine(
            client,
            DownloadUriPolicy.YouTubeMediaAndLoopback,
            (_, _) => Task.CompletedTask);
        var processor = new FfmpegMediaProcessor(executable, new CopyingProcessRunner(muxed));
        var engine = new AdaptiveDownloadEngine(direct, processor);
        var output = Path.Combine(directory.Path, "combined.webm");
        var videoTrack = output + ".video-track.webm";
        var audioTrack = output + ".audio-track.webm";

        var result = await engine.DownloadAsync(new AdaptiveDownloadRequest
        {
            Video = Request("video", videoTrack, video.Length, MediaContainer.WebM),
            Audio = Request("audio", audioTrack, audio.Length, MediaContainer.WebM),
            DestinationPath = output,
            OutputContainer = MediaContainer.WebM
        });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(videoTrack));
        Assert.False(File.Exists(audioTrack));
        Assert.SequenceEqual(muxedBytes, await File.ReadAllBytesAsync(output));
    }

    [Test]
    public static async Task MkvOutputMuxesCrossContainerTracksThroughFfmpeg()
    {
        using var directory = new AdaptiveTestDirectory();
        var executable = Path.Combine(directory.Path, "ffmpeg.exe");
        var muxed = Path.Combine(directory.Path, "ffmpeg-output.mkv");
        await File.WriteAllBytesAsync(executable, []);
        var muxedBytes = SyntheticWebM.Track(
            1, "V_VP9", (0, "MUXED-V"u8.ToArray()), (100, "MUXED-A"u8.ToArray()));
        await File.WriteAllBytesAsync(muxed, muxedBytes);
        // Cross-family: VP9 video served as WebM, AAC audio served as MP4 (m4a).
        var video = SyntheticWebM.Track(1, "V_VP9", (0, "VIDEO"u8.ToArray()));
        var audio = SyntheticMp4.Track("soun", "AUDIO"u8, 1, 48_000, 48_000);
        using var handler = new MediaHandler(video, audio);
        using var client = new HttpClient(handler);
        var direct = new DirectDownloadEngine(
            client,
            DownloadUriPolicy.YouTubeMediaAndLoopback,
            (_, _) => Task.CompletedTask);
        var engine = new AdaptiveDownloadEngine(
            direct,
            new FfmpegMediaProcessor(executable, new CopyingProcessRunner(muxed)));
        var output = Path.Combine(directory.Path, "combined.mkv");
        var videoTrack = output + ".video-track.webm";
        var audioTrack = output + ".audio-track.m4a";

        var result = await engine.DownloadAsync(new AdaptiveDownloadRequest
        {
            Video = Request("video", videoTrack, video.Length, MediaContainer.WebM),
            Audio = Request("audio", audioTrack, audio.Length, MediaContainer.Mp4),
            DestinationPath = output,
            OutputContainer = MediaContainer.Mkv
        });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(File.Exists(videoTrack));
        Assert.False(File.Exists(audioTrack));
        Assert.SequenceEqual(muxedBytes, await File.ReadAllBytesAsync(output));
    }

    [Test]
    public static async Task MkvOutputFailsClosedWithoutFfmpeg()
    {
        using var directory = new AdaptiveTestDirectory();
        var video = SyntheticWebM.Track(1, "V_VP9", (0, "VIDEO"u8.ToArray()));
        var audio = SyntheticMp4.Track("soun", "AUDIO"u8, 1, 48_000, 48_000);
        using var handler = new MediaHandler(video, audio);
        using var client = new HttpClient(handler);
        var direct = new DirectDownloadEngine(
            client,
            DownloadUriPolicy.YouTubeMediaAndLoopback,
            (_, _) => Task.CompletedTask);
        var engine = new AdaptiveDownloadEngine(direct);
        var output = Path.Combine(directory.Path, "combined.mkv");

        var result = await engine.DownloadAsync(new AdaptiveDownloadRequest
        {
            Video = Request("video", output + ".video-track.webm", video.Length, MediaContainer.WebM),
            Audio = Request("audio", output + ".audio-track.m4a", audio.Length, MediaContainer.Mp4),
            DestinationPath = output,
            OutputContainer = MediaContainer.Mkv
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("Media.FFmpegMissing", result.Error?.Code);
        Assert.False(File.Exists(output));
    }

    private static DownloadRequest Request(
        string kind,
        string destination,
        long length,
        MediaContainer container) => new()
        {
            SourceUrl = new Uri($"http://localhost/{kind}"),
            SourceIdentity = $"Fixture123_:{kind}",
            DestinationPath = destination,
            ExpectedLength = length,
            ExpectedContainer = container
        };

    private sealed class MediaHandler(byte[] video, byte[] audio) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var payload = request.RequestUri?.AbsolutePath == "/video" ? video : audio;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload)
            });
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery must not issue a media request.");
    }

    private sealed class RejectingProcessRunner : IFfmpegProcessRunner
    {
        public Task<int> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery must not start FFmpeg.");
    }

    private sealed class CopyingProcessRunner(string sourcePath) : IFfmpegProcessRunner
    {
        public async Task<int> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
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

    private sealed class AdaptiveTestDirectory : IDisposable
    {
        private static readonly string SafeRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TubeForge.Tests"));

        public AdaptiveTestDirectory()
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
