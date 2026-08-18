using System.Net;
using System.Net.Http.Headers;
using TubeForge.Core.Media;
using TubeForge.Core.Networking;
using TubeForge.Core.Settings;
using TubeForge.Core.YouTube;
using TubeForge.Tests.Downloads;
using TubeForge.Tests.Framework;
using TubeForge.YouTube;

namespace TubeForge.Tests.YouTube;

public static class YouTubeMetadataResolverTests
{
    [Test]
    public static async Task FetchesMetadataThroughConfiguredLoopbackProxy()
    {
        var html = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "watch-page-basic.html"));
        await using var proxy = LoopbackHttpResponseServer.Start(
            IPAddress.Loopback,
            System.Text.Encoding.UTF8.GetBytes(html),
            maximumRequests: 8);
        var proxyOrigin = new Uri(proxy.EndpointUri.GetLeftPart(UriPartial.Authority) + "/");
        var networkProxy = new ConfigurableWebProxy(
            new NetworkProxyConfiguration(NetworkProxyMode.Manual, proxyOrigin));
        using var handler = new SocketsHttpHandler { Proxy = networkProxy, UseProxy = true };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var resolver = new YouTubeMetadataResolver(
            client,
            new Uri("http://metadata.test/"),
            TimeSpan.FromSeconds(5));

        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));
        var result = await resolver.ResolveAsync(videoId);
        var request = await proxy.Request;

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(request.Contains(
            "GET http://metadata.test/watch?v=Fixture123_&hl=en HTTP/1.1",
            StringComparison.Ordinal));
        Assert.Equal("Fixture {video}; title", result.Value.Metadata.Title);
    }

    [Test]
    public static async Task FetchesExpectedWatchPageAndMapsIt()
    {
        var html = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "watch-page-basic.html"));
        using var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post || request.RequestUri?.AbsolutePath != "/watch")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            Assert.Equal("www.youtube.com", request.RequestUri?.Host);
            Assert.Equal("Fixture123_", QueryValue(request.RequestUri!, "v"));
            Assert.True(request.Headers.UserAgent.Count > 0);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html)
            };
        });
        using var client = new HttpClient(handler);
        var resolver = new YouTubeMetadataResolver(client);

        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));
        var result = await resolver.ResolveAsync(videoId);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Fixture {video}; title", result.Value.Metadata.Title);
    }

    [Test]
    public static async Task ClassifiesRateLimitWithoutReadingBody()
    {
        using var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return response;
        });
        using var client = new HttpClient(handler);
        var resolver = new YouTubeMetadataResolver(client);
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));

        var result = await resolver.ResolveAsync(videoId);

        Assert.False(result.IsSuccess);
        Assert.Equal("Network.RateLimited", result.Error?.Code);
        Assert.True(result.Error?.IsTransient == true);
        Assert.Equal(TimeSpan.FromSeconds(17), result.Error?.RetryAfter);
    }

    [Test]
    public static async Task FetchesPlayerScriptAndDeciphersCipheredFormat()
    {
        const string watchPage = """
            <script>
            var ytInitialPlayerResponse={
              "playabilityStatus":{"status":"OK"},
              "videoDetails":{"videoId":"Fixture123_","title":"Cipher fixture","lengthSeconds":"10"},
              "streamingData":{"formats":[{
                "itag":22,
                "signatureCipher":"url=https%3A%2F%2Ffixture.googlevideo.com%2Fvideoplayback%3Fitag%3D22&s=abcdef&sp=sig",
                "mimeType":"video/mp4; codecs=\"avc1.64001F, mp4a.40.2\"",
                "width":1280,"height":720,"contentLength":"10"
              }]}
            };
            ytcfg.set({"PLAYER_JS_URL":"/s/player/resolver-fixture/base.js"});
            </script>
            """;
        const string playerScript = """
            var AB={rv:function(a){a.reverse()},sl:function(a,b){a.splice(0,b)},sw:function(a,b){var c=a[0];a[0]=a[b%a.length];a[b%a.length]=c}};
            XY=function(a){a=a.split("");AB.sw(a,2);AB.rv(a);AB.sl(a,1);return a.join("")};
            """;
        var requestCount = 0;
        using var handler = new StubHandler(request =>
        {
            requestCount++;
            if (request.RequestUri?.Host == "www.youtube.com" && request.RequestUri.AbsolutePath == "/watch")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(watchPage) };
            }

            if (request.RequestUri?.Host == "www.youtube.com" &&
                request.RequestUri.AbsolutePath == "/s/player/resolver-fixture/base.js")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(playerScript) };
            }

            Assert.Equal("fixture.googlevideo.com", request.RequestUri?.Host);
            Assert.True(request.Headers.Range is null);
            Assert.Equal("0-0", QueryValue(request.RequestUri!, "range"));
            Assert.Equal("0", QueryValue(request.RequestUri!, "rn"));
            Assert.Equal("0", QueryValue(request.RequestUri!, "rbuf"));
            var probe = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0])
            };
            probe.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 10);
            return probe;
        });
        using var client = new HttpClient(handler);
        var resolver = new YouTubeMetadataResolver(client);
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));

        var result = await resolver.ResolveAsync(videoId);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, result.Value.Metadata.Formats.Count);
        Assert.Equal(22, result.Value.Metadata.Formats[0].FormatId);
        Assert.True(result.Value.Metadata.Formats[0].Url.Query.Contains("sig=edabc", StringComparison.Ordinal));
        Assert.Equal(3, requestCount);
    }

    [Test]
    public static async Task FetchesPlayerScriptAndResolvesThrottledDirectFormat()
    {
        const string watchPage = """
            <script>
            var ytInitialPlayerResponse={
              "playabilityStatus":{"status":"OK"},
              "videoDetails":{"videoId":"Fixture123_","title":"Throttle fixture","lengthSeconds":"10"},
              "streamingData":{"formats":[{
                "itag":22,
                "url":"https://fixture.googlevideo.com/videoplayback?n=abcdef&itag=22",
                "mimeType":"video/mp4; codecs=\"avc1.64001F, mp4a.40.2\"",
                "width":1280,"height":720,"contentLength":"10"
              }]}
            };
            ytcfg.set({"PLAYER_JS_URL":"/s/player/throttle-fixture/base.js"});
            </script>
            """;
        const string playerScript = """
            const OP={rv(a){a.reverse()},sl(a,b){a.splice(0,b)}};
            const NT=(a)=>{a=a.split('');OP.sl(a,2);OP.rv(a);return a.join('')};
            const TF=[NT];let n=url.searchParams.get('n');n=TF[0](n);url.searchParams.set('n',n);
            """;
        var requestCount = 0;
        using var handler = new StubHandler(request =>
        {
            requestCount++;
            if (request.RequestUri?.AbsolutePath == "/watch")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(watchPage) };
            }

            if (request.RequestUri?.AbsolutePath == "/s/player/throttle-fixture/base.js")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(playerScript) };
            }

            Assert.Equal("fixture.googlevideo.com", request.RequestUri?.Host);
            Assert.Equal("fedc", QueryValue(request.RequestUri!, "n"));
            Assert.True(request.Headers.Range is null);
            Assert.Equal("0-0", QueryValue(request.RequestUri!, "range"));
            Assert.Equal("0", QueryValue(request.RequestUri!, "rn"));
            Assert.Equal("0", QueryValue(request.RequestUri!, "rbuf"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0])
            };
        });
        using var client = new HttpClient(handler);
        var resolver = new YouTubeMetadataResolver(client);
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));

        var result = await resolver.ResolveAsync(videoId);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("fedc", QueryValue(result.Value.Metadata.Formats.Single().Url, "n"));
        Assert.Equal(3, requestCount);
    }

    [Test]
    public static async Task UsesTailVerifiedVisionOsClientWhenPageHasNoDirectFormats()
    {
        const string watchPage = """
            <script>
            var ytInitialPlayerResponse={
              "playabilityStatus":{"status":"OK"},
              "videoDetails":{"videoId":"Fixture123_","title":"Watch metadata","lengthSeconds":"10","isShortsEligible":true},
              "captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[{
                "baseUrl":"https://www.youtube.com/api/timedtext?v=Fixture123_&lang=en",
                "name":{"simpleText":"English"},
                "vssId":".en","languageCode":"en","isTranslatable":true
              }]}}
            };
            ytcfg.set({
              "INNERTUBE_API_KEY":"fixturePublicConfig",
              "VISITOR_DATA":"fixtureVisitor",
              "PLAYER_JS_URL":"/s/player/android-fixture/base.js"
            });
            </script>
            """;
        const string playerResponse = """
            {
              "playabilityStatus":{"status":"OK"},
              "videoDetails":{"videoId":"Fixture123_","title":"Android metadata","lengthSeconds":"10"},
              "streamingData":{"formats":[{
                "itag":22,
                "url":"https://fixture.googlevideo.com/videoplayback?id=synthetic&itag=22",
                "mimeType":"video/mp4; codecs=\"avc1.64001F, mp4a.40.2\"",
                "width":1280,"height":720,"contentLength":"10"
              }]}
            }
            """;
        var requestCount = 0;
        using var handler = new StubHandler(request =>
        {
            requestCount++;
            if (request.Method == HttpMethod.Get && request.RequestUri?.Host == "www.youtube.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(watchPage) };
            }

            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("fixture.googlevideo.com", request.RequestUri?.Host);
                Assert.True(request.Headers.Range is null);
                Assert.Equal("9-9", QueryValue(request.RequestUri!, "range"));
                Assert.Equal("0", QueryValue(request.RequestUri!, "rn"));
                Assert.Equal("0", QueryValue(request.RequestUri!, "rbuf"));
                Assert.True(request.Headers.UserAgent.ToString().Contains("Version/26.0", StringComparison.Ordinal));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0])
                };
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/youtubei/v1/player", request.RequestUri?.AbsolutePath);
            Assert.Equal("101", request.Headers.GetValues("X-YouTube-Client-Name").Single());
            Assert.Equal("1.02", request.Headers.GetValues("X-YouTube-Client-Version").Single());
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(playerResponse) };
        });
        using var client = new HttpClient(handler);
        var resolver = new YouTubeMetadataResolver(client);
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));

        var result = await resolver.ResolveAsync(videoId);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, result.Value.Metadata.Formats.Count);
        Assert.Equal(1, result.Value.Metadata.CaptionTracks.Count);
        Assert.Equal("en", result.Value.Metadata.CaptionTracks[0].LanguageCode);
        Assert.Equal("Android metadata", result.Value.Metadata.Title);
        Assert.Equal(VideoContentKind.Short, result.Value.Metadata.ContentKind);
        Assert.Equal("ClientResolved:VISIONOS", result.Value.Diagnostics?.Stage);
        Assert.Equal(3, requestCount);
    }

    [Test]
    public static async Task AugmentsLowProgressiveWatchFormatWithHighestAdaptiveClientPair()
    {
        const string watchPage = """
            <script>
            var ytInitialPlayerResponse={
              "playabilityStatus":{"status":"OK"},
              "videoDetails":{"videoId":"Fixture123_","title":"Watch metadata","lengthSeconds":"10"},
              "streamingData":{"formats":[{
                "itag":18,
                "url":"https://fixture.googlevideo.com/videoplayback?id=watch&itag=18",
                "mimeType":"video/mp4; codecs=\"avc1.42001E, mp4a.40.2\"",
                "width":640,"height":360,"contentLength":"10"
              }]}
            };
            ytcfg.set({"INNERTUBE_API_KEY":"fixturePublicConfig","VISITOR_DATA":"fixtureVisitor"});
            </script>
            """;
        const string playerResponse = """
            {
              "playabilityStatus":{"status":"OK"},
              "videoDetails":{"videoId":"Fixture123_","title":"Android metadata","lengthSeconds":"10"},
              "streamingData":{
                "formats":[{
                  "itag":18,
                  "url":"https://fixture.googlevideo.com/videoplayback?id=android&itag=18",
                  "mimeType":"video/mp4; codecs=\"avc1.42001E, mp4a.40.2\"",
                  "width":640,"height":360,"contentLength":"10"
                }],
                "adaptiveFormats":[{
                  "itag":401,
                  "url":"https://fixture.googlevideo.com/videoplayback?id=android&itag=401",
                  "mimeType":"video/mp4; codecs=\"av01.0.12M.08\"",
                  "width":3840,"height":2160,"fps":60,"bitrate":15000000,"contentLength":"100"
                },{
                  "itag":140,
                  "url":"https://fixture.googlevideo.com/videoplayback?id=android&itag=140",
                  "mimeType":"audio/mp4; codecs=\"mp4a.40.2\"",
                  "bitrate":130000,"audioSampleRate":"44100","contentLength":"20"
                }]
              }
            }
            """;
        var requestCount = 0;
        using var handler = new StubHandler(request =>
        {
            requestCount++;
            if (request.Method == HttpMethod.Get && request.RequestUri?.Host == "www.youtube.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(watchPage) };
            }

            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(playerResponse) };
            }

            var formatId = int.Parse(QueryValue(request.RequestUri!, "itag")!);
            var length = formatId == 401 ? 100L : 20L;
            Assert.True(request.Headers.Range is null);
            Assert.Equal($"{length - 1}-{length - 1}", QueryValue(request.RequestUri!, "range"));
            Assert.Equal(formatId == 401 ? "0" : "1", QueryValue(request.RequestUri!, "rn"));
            Assert.Equal("0", QueryValue(request.RequestUri!, "rbuf"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0])
            };
        });
        using var client = new HttpClient(handler);
        var resolver = new YouTubeMetadataResolver(client);
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));

        var result = await resolver.ResolveAsync(videoId);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var selection = AdaptiveFormatSelector.SelectBest(result.Value.Metadata.Formats);
        Assert.Equal(3, result.Value.Metadata.Formats.Count);
        Assert.Equal(1, result.Value.Metadata.Formats.Count(format => format.FormatId == 18));
        Assert.Equal("ClientResolved:VISIONOS+WatchPage", result.Value.Diagnostics?.Stage);
        Assert.True(selection?.RequiresMuxing == true);
        Assert.Equal(401, selection!.Video.FormatId);
        Assert.Equal(140, selection.Audio!.FormatId);
        Assert.Equal(4, requestCount);
    }

    [Test]
    public static async Task UsesTvFallbackWithProviderUserAgentContainingSeparator()
    {
        const string watchPage = """
            <script>
            var ytInitialPlayerResponse={
              "playabilityStatus":{"status":"OK"},
              "videoDetails":{"videoId":"Fixture123_","title":"Watch metadata","lengthSeconds":"10"}
            };
            ytcfg.set({"INNERTUBE_API_KEY":"fixturePublicConfig"});
            </script>
            """;
        const string emptyPlayerResponse = """
            {
              "playabilityStatus":{"status":"OK"},
              "videoDetails":{"videoId":"Fixture123_","title":"Client metadata","lengthSeconds":"10"}
            }
            """;
        const string tvPlayerResponse = """
            {
              "playabilityStatus":{"status":"OK"},
              "videoDetails":{"videoId":"Fixture123_","title":"TV metadata","lengthSeconds":"10"},
              "streamingData":{"formats":[{
                "itag":18,
                "url":"https://fixture.googlevideo.com/videoplayback?id=tv&itag=18",
                "mimeType":"video/mp4; codecs=\"avc1.42001E, mp4a.40.2\"",
                "width":640,"height":360,"contentLength":"10"
              }]}
            }
            """;
        var clientNames = new List<string>();
        using var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.Host == "www.youtube.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(watchPage) };
            }

            if (request.Method == HttpMethod.Post)
            {
                var clientName = request.Headers.GetValues("X-YouTube-Client-Name").Single();
                clientNames.Add(clientName);
                if (clientName == "7")
                {
                    Assert.True(request.Headers.GetValues("User-Agent").Single()
                        .Contains("(unlike Gecko), Unknown_TV", StringComparison.Ordinal));
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tvPlayerResponse) };
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(emptyPlayerResponse) };
            }

            Assert.Equal("fixture.googlevideo.com", request.RequestUri?.Host);
            Assert.True(request.Headers.GetValues("User-Agent").Single()
                .Contains("(unlike Gecko), Unknown_TV", StringComparison.Ordinal));
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([0])
            };
        });
        using var client = new HttpClient(handler);
        var resolver = new YouTubeMetadataResolver(client);
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));

        var result = await resolver.ResolveAsync(videoId);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("ClientResolved:TVHTML5", result.Value.Diagnostics?.Stage);
        Assert.Equal(18, result.Value.Metadata.Formats.Single().FormatId);
        Assert.SequenceEqual(new[] { "101", "28", "56", "7" }, clientNames);
    }

    [Test]
    public static async Task ResolvesActiveLiveManifestThroughDirectClientFallback()
    {
        const string watchPage = """
            <script>
            var ytInitialPlayerResponse={
              "playabilityStatus":{"status":"OK"},
              "videoDetails":{"videoId":"Fixture123_","title":"Active watch metadata","isLiveContent":true,"isLive":true}
            };
            ytcfg.set({"INNERTUBE_API_KEY":"fixturePublicConfig"});
            </script>
            """;
        const string playerResponse = """
            {
              "playabilityStatus":{"status":"OK"},
              "videoDetails":{"videoId":"Fixture123_","title":"Active client metadata","isLiveContent":true,"isLive":true},
              "streamingData":{"hlsManifestUrl":"https://manifest.googlevideo.com/api/manifest/hls_playlist/fixture.m3u8"}
            }
            """;
        var requestCount = 0;
        using var handler = new StubHandler(request =>
        {
            requestCount++;
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/watch")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(watchPage) };
            }

            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("101", request.Headers.GetValues("X-YouTube-Client-Name").Single());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(playerResponse) };
            }

            Assert.Equal("manifest.googlevideo.com", request.RequestUri?.Host);
            Assert.Equal(0L, request.Headers.Range?.Ranges.Single().From);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U") };
        });
        using var client = new HttpClient(handler);
        var resolver = new YouTubeMetadataResolver(client);
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));

        var result = await resolver.ResolveAsync(videoId);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(VideoContentKind.LiveActive, result.Value.Metadata.ContentKind);
        Assert.True(result.Value.Metadata.Formats.Single().IsLiveHls);
        Assert.False(result.Value.Metadata.Formats.Single().IsLiveManifestPending);
        Assert.Equal("ClientResolved:VISIONOS+WatchPage", result.Value.Diagnostics?.Stage);
        Assert.Equal(3, requestCount);
    }

    [Test]
    public static async Task RejectsActiveLiveWhenDirectClientsAlsoOmitManifest()
    {
        const string activeWithoutManifest = """
            {
              "playabilityStatus":{"status":"OK"},
              "videoDetails":{"videoId":"Fixture123_","title":"Active metadata","isLiveContent":true,"isLive":true}
            }
            """;
        var watchPage = "<script>var ytInitialPlayerResponse=" + activeWithoutManifest +
                        ";ytcfg.set({\"INNERTUBE_API_KEY\":\"fixturePublicConfig\"});</script>";
        var requestCount = 0;
        using var handler = new StubHandler(request =>
        {
            requestCount++;
            return request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(watchPage) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(activeWithoutManifest) };
        });
        using var client = new HttpClient(handler);
        var resolver = new YouTubeMetadataResolver(client);
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));

        var result = await resolver.ResolveAsync(videoId);

        Assert.False(result.IsSuccess);
        Assert.Equal("Video.LiveManifestUnavailable", result.Error?.Code);
        Assert.Equal(6, requestCount);
    }

    private static string? QueryValue(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var components = pair.Split('=', 2);
            if (Uri.UnescapeDataString(components[0]).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return components.Length == 2 ? Uri.UnescapeDataString(components[1]) : string.Empty;
            }
        }

        return null;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }
}
