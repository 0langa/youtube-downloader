using System.Net;
using System.Text;
using TubeForge.Core.Media;
using TubeForge.Downloads.Hls;
using TubeForge.Downloads.Queue;
using TubeForge.Tests.Framework;

namespace TubeForge.Tests.Downloads;

public static class HlsCaptureEngineTests
{
    private static readonly Uri Master = new("https://manifest.googlevideo.com/live/master.m3u8");

    [Test]
    public static async Task DownloadsHighestVariantAndAssemblesBoundedCapture()
    {
        using var directory = new TestDirectory();
        var requests = new List<string>();
        var highPlaylistAttempts = 0;
        var firstSegmentAttempts = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/live/master.m3u8" => Text("""
                    #EXTM3U
                    #EXT-X-STREAM-INF:BANDWIDTH=500000
                    low.m3u8
                    #EXT-X-STREAM-INF:BANDWIDTH=2000000
                    high.m3u8
                    """),
                "/live/high.m3u8" when ++highPlaylistAttempts == 1 =>
                    new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "/live/high.m3u8" => Text("""
                    #EXTM3U
                    #EXT-X-TARGETDURATION:6
                    #EXT-X-MEDIA-SEQUENCE:10
                    #EXT-X-MAP:URI="init.mp4"
                    #EXTINF:6,
                    10.m4s
                    #EXTINF:6,
                    11.m4s
                    #EXT-X-ENDLIST
                    """),
                "/live/init.mp4" => Bytes("init"),
                "/live/10.m4s" when ++firstSegmentAttempts == 1 =>
                    new HttpResponseMessage(HttpStatusCode.NotFound),
                "/live/10.m4s" => Bytes("ten"),
                "/live/11.m4s" => Bytes("eleven"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var destination = Path.Combine(directory.Path, "capture.source");
        var engine = new HlsCaptureEngine(client, new HostRequestGate());

        var result = await engine.CaptureAsync(Request(destination));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("initteneleven", await File.ReadAllTextAsync(destination));
        Assert.True(requests.Contains("/live/high.m3u8"));
        Assert.False(requests.Contains("/live/low.m3u8"));
        Assert.Equal(2, highPlaylistAttempts);
        Assert.Equal(2, firstSegmentAttempts);
        var journal = await File.ReadAllTextAsync(destination + ".hls.json");
        Assert.False(journal.Contains("http", StringComparison.OrdinalIgnoreCase));
        Assert.False(journal.Contains("googlevideo", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public static async Task ResumesJournalAfterCancellationAndRejectsExpiredGap()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "capture.source");
        using var cancellation = new CancellationTokenSource();
        using (var firstClient = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
               {
                   "/live/master.m3u8" => MediaPlaylist(20, endList: false),
                   "/live/20.ts" => Bytes("twenty"),
                   "/live/21.ts" => Bytes("twenty-one"),
                   _ => new HttpResponseMessage(HttpStatusCode.NotFound)
               })))
        {
            var first = new HlsCaptureEngine(
                firstClient,
                new HostRequestGate(),
                delay: (_, token) =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled(token);
                });
            var cancelled = await first.CaptureAsync(Request(destination), cancellationToken: cancellation.Token);
            Assert.Equal("Operation.Cancelled", cancelled.Error?.Code);
        }

        Assert.True(File.Exists(destination + ".hls.json"));
        using (var resumeClient = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
               {
                   "/live/master.m3u8" => Text("""
                       #EXTM3U
                       #EXT-X-TARGETDURATION:6
                       #EXT-X-MEDIA-SEQUENCE:20
                       #EXTINF:6,
                       20.ts
                       #EXTINF:6,
                       21.ts
                       #EXTINF:6,
                       22.ts
                       #EXT-X-ENDLIST
                       """),
                   "/live/22.ts" => Bytes("twenty-two"),
                   _ => new HttpResponseMessage(HttpStatusCode.NotFound)
               })))
        {
            var resumed = await new HlsCaptureEngine(resumeClient, new HostRequestGate())
                .CaptureAsync(Request(destination));
            Assert.True(resumed.IsSuccess, resumed.Error?.Message);
            Assert.True(resumed.Value.Resumed);
            Assert.Equal("twenty-onetwenty-two", await File.ReadAllTextAsync(destination));
        }

        var expiredDestination = Path.Combine(directory.Path, "expired.source");
        File.Copy(destination + ".hls.json", expiredDestination + ".hls.json");
        Directory.CreateDirectory(expiredDestination + ".hls.parts");
        foreach (var part in Directory.GetFiles(destination + ".hls.parts"))
        {
            File.Copy(part, Path.Combine(expiredDestination + ".hls.parts", Path.GetFileName(part)));
        }

        using var expiredClient = new HttpClient(new StubHandler(_ => Text("""
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:30
            #EXTINF:6,
            30.ts
            """)));
        var expired = await new HlsCaptureEngine(expiredClient, new HostRequestGate())
            .CaptureAsync(Request(expiredDestination));
        Assert.Equal("Hls.SegmentsExpired", expired.Error?.Code);
    }

    [Test]
    public static async Task ReadsBoundedMediaPlaylistAboveLegacyTwoMegabytes()
    {
        using var directory = new TestDirectory();
        var largePlaylist = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:42\n");
        for (var index = 0; index < 2_000; index++)
        {
            largePlaylist.Append('#').Append(new string('x', 1_100)).Append('\n');
        }

        largePlaylist.Append("#EXTINF:6,\n42.ts\n#EXT-X-ENDLIST\n");
        Assert.True(largePlaylist.Length > 2 * 1024 * 1024);
        using var client = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/live/master.m3u8" => Text(largePlaylist.ToString()),
            "/live/42.ts" => Bytes("segment"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var destination = Path.Combine(directory.Path, "large-playlist.source");

        var result = await new HlsCaptureEngine(client, new HostRequestGate())
            .CaptureAsync(Request(destination));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("segment", await File.ReadAllTextAsync(destination));
    }

    private static HlsCaptureRequest Request(string destination) => new()
    {
        ManifestUri = Master,
        DestinationPath = destination,
        Options = new LiveCaptureOptions(TimeSpan.FromMinutes(1), 16 * 1024 * 1024, TimeSpan.Zero)
    };

    private static HttpResponseMessage MediaPlaylist(long sequence, bool endList) => Text($"""
        #EXTM3U
        #EXT-X-TARGETDURATION:6
        #EXT-X-MEDIA-SEQUENCE:{sequence}
        #EXTINF:6,
        {sequence}.ts
        #EXTINF:6,
        {sequence + 1}.ts
        {(endList ? "#EXT-X-ENDLIST" : string.Empty)}
        """);

    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/vnd.apple.mpegurl")
    };

    private static HttpResponseMessage Bytes(string value) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(value))
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }

    private sealed class TestDirectory : IDisposable
    {
        private static readonly string SafeRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TubeForge.Tests.Hls"));

        public TestDirectory()
        {
            Directory.CreateDirectory(SafeRoot);
            Path = System.IO.Path.Combine(SafeRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (resolved.StartsWith(SafeRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
