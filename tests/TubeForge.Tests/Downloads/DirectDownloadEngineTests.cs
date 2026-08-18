using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using TubeForge.Downloads;
using TubeForge.Tests.Framework;

namespace TubeForge.Tests.Downloads;

public static class DirectDownloadEngineTests
{
    [Test]
    public static async Task DownloadsToPartialThenAtomicallyFinalizes()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "fixture.mp4");
        var payload = Encoding.ASCII.GetBytes("synthetic-media");
        using var handler = new StubHandler((request, _) =>
        {
            var range = request.Headers.Range?.Ranges.Single();
            Assert.Equal(0L, range?.From);
            Assert.Equal(payload.Length - 1L, range?.To);
            var response = Response(HttpStatusCode.OK, payload);
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var progress = new InlineProgress<DownloadProgress>();
        var engine = Engine(client);

        var result = await engine.DownloadAsync(Request(destination, payload.Length), progress);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(payload.Length, checked((int)result.Value.BytesWritten));
        Assert.False(result.Value.Resumed);
        Assert.SequenceEqual(payload, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
        Assert.False(File.Exists(destination + ".part.json"));
        Assert.Equal(payload.Length, checked((int)progress.Values[^1].BytesReceived));
        Assert.Equal(1d, progress.Values[^1].Fraction);
    }

    [Test]
    public static async Task DownloadsLargeMediaUsingBoundedSequentialRanges()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "bounded-ranges.mp4");
        var payload = Enumerable.Range(0, checked((int)DirectDownloadEngine.MaximumDirectRequestBytes + 257))
            .Select(index => (byte)(index % 251))
            .ToArray();
        var ranges = new List<(long From, long To)>();
        using var handler = new StubHandler((request, _) =>
        {
            var range = request.Headers.Range?.Ranges.Single();
            Assert.True(range?.From is not null && range.To is not null);
            var from = range!.From!.Value;
            var to = range.To!.Value;
            Assert.True(to - from + 1 <= DirectDownloadEngine.MaximumDirectRequestBytes);
            ranges.Add((from, to));
            return Task.FromResult(RangeResponse(payload, from, to, "\"bounded-v1\""));
        });
        using var client = new HttpClient(handler);
        var engine = Engine(client);

        var result = await engine.DownloadAsync(Request(destination, payload.Length));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, ranges.Count);
        Assert.Equal((0L, DirectDownloadEngine.MaximumDirectRequestBytes - 1), ranges[0]);
        Assert.Equal((DirectDownloadEngine.MaximumDirectRequestBytes, payload.Length - 1L), ranges[1]);
        Assert.SequenceEqual(payload, await File.ReadAllBytesAsync(destination));
    }

    [Test]
    public static async Task DoesNotRewriteUnchangedResumeStateBetweenRanges()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "stable-resume-state.mp4");
        var statePath = destination + ".part.json";
        var payload = Enumerable.Range(0, checked((int)DirectDownloadEngine.MaximumDirectRequestBytes + 257))
            .Select(index => (byte)(index % 251))
            .ToArray();
        FileStream? stateLock = null;
        var requests = 0;
        using var handler = new StubHandler((request, _) =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            if (++requests == 2)
            {
                stateLock = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            return Task.FromResult(RangeResponse(
                payload,
                range.From!.Value,
                range.To!.Value,
                "\"stable-v1\""));
        });
        using var client = new HttpClient(handler);
        var engine = Engine(client);

        var result = await engine.DownloadAsync(Request(destination, payload.Length));
        stateLock?.Dispose();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, requests);
        Assert.SequenceEqual(payload, await File.ReadAllBytesAsync(destination));
    }

    [Test]
    public static async Task DownloadsGoogleVideoUsingPlayerStyleQueryRangesAndClientUserAgent()
    {
        const string providerUserAgent =
            "Mozilla/5.0 (ChromiumStylePlatform) Cobalt/25.lts.30.1034943-gold " +
            "(unlike Gecko), Unknown_TV_Unknown_0/Unknown (Unknown, Unknown)";
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "googlevideo-ranges.mp4");
        var payload = Enumerable.Range(0, checked((int)DirectDownloadEngine.MaximumDirectRequestBytes + 257))
            .Select(index => (byte)(index % 247))
            .ToArray();
        var requests = 0;
        using var handler = new StubHandler((request, _) =>
        {
            Assert.True(request.Headers.Range is null);
            Assert.Equal(providerUserAgent, request.Headers.GetValues("User-Agent").Single());
            var rangeText = QueryValue(request.RequestUri!, "range")!;
            var bounds = rangeText.Split('-').Select(long.Parse).ToArray();
            Assert.Equal(requests.ToString(), QueryValue(request.RequestUri!, "rn"));
            Assert.Equal("0", QueryValue(request.RequestUri!, "rbuf"));
            requests++;
            var length = checked((int)(bounds[1] - bounds[0] + 1));
            var content = new byte[length];
            Buffer.BlockCopy(payload, checked((int)bounds[0]), content, 0, length);
            return Task.FromResult(Response(HttpStatusCode.OK, content));
        });
        using var client = new HttpClient(handler);
        var engine = Engine(client);

        var result = await engine.DownloadAsync(Request(destination, payload.Length) with
        {
            SourceUrl = new Uri("https://fixture.googlevideo.com/videoplayback?sig=fixture"),
            HttpUserAgent = providerUserAgent
        });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, requests);
        Assert.SequenceEqual(payload, await File.ReadAllBytesAsync(destination));
    }

    [Test]
    public static async Task DiscoversUnknownGoogleVideoLengthFromShortFinalQueryRange()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "unknown-googlevideo-length.mp4");
        var payload = Enumerable.Range(0, checked((int)DirectDownloadEngine.MaximumDirectRequestBytes + 257))
            .Select(index => (byte)(index % 241))
            .ToArray();
        var requests = 0;
        using var handler = new StubHandler((request, _) =>
        {
            var bounds = QueryValue(request.RequestUri!, "range")!
                .Split('-')
                .Select(long.Parse)
                .ToArray();
            var from = bounds[0];
            var to = Math.Min(bounds[1], payload.Length - 1L);
            var length = checked((int)(to - from + 1));
            var content = new byte[length];
            Buffer.BlockCopy(payload, checked((int)from), content, 0, length);
            requests++;
            return Task.FromResult(Response(HttpStatusCode.OK, content));
        });
        using var client = new HttpClient(handler);
        var engine = Engine(client);

        var result = await engine.DownloadAsync(Request(destination, payload.Length) with
        {
            SourceUrl = new Uri("https://fixture.googlevideo.com/videoplayback?sig=fixture"),
            ExpectedLength = null
        });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, requests);
        Assert.Equal(payload.Length, result.Value.BytesWritten);
        Assert.SequenceEqual(payload, await File.ReadAllBytesAsync(destination));
    }

    [Test]
    public static async Task RejectsShortGoogleVideoQueryRangeWhenLengthIsKnown()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "short-known-googlevideo.mp4");
        var payload = Enumerable.Range(0, 257).Select(index => (byte)index).ToArray();
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, payload)));
        using var client = new HttpClient(handler);
        var engine = Engine(client);

        var result = await engine.DownloadAsync(Request(destination, payload.Length + 1L) with
        {
            SourceUrl = new Uri("https://fixture.googlevideo.com/videoplayback?sig=fixture")
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("Download.RemoteChanged", result.Error?.Code);
        Assert.False(File.Exists(destination));
    }

    [Test]
    public static async Task ResumesCompatiblePartialUsingRangeAndValidator()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "fixture.mp4");
        await File.WriteAllBytesAsync(destination + ".part", Encoding.ASCII.GetBytes("01234"));
        await File.WriteAllTextAsync(destination + ".part.json", """
            {
              "SchemaVersion": 1,
              "SourceIdentity": "Fixture123_:22",
              "ExpectedLength": 10,
              "EntityTag": "\"fixture-v1\"",
              "LastModified": null
            }
            """);

        using var handler = new StubHandler((request, _) =>
        {
            var range = request.Headers.Range?.Ranges.Single();
            Assert.Equal(5L, range?.From);
            Assert.Equal(9L, range?.To);
            Assert.Equal("\"fixture-v1\"", request.Headers.IfRange?.EntityTag?.Tag);
            var response = Response(HttpStatusCode.PartialContent, Encoding.ASCII.GetBytes("56789"));
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, 9, 10);
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var engine = Engine(client);

        var result = await engine.DownloadAsync(Request(destination, 10));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value.Resumed);
        Assert.Equal("0123456789", await File.ReadAllTextAsync(destination));
    }

    [Test]
    public static async Task RetriesTransientHttpFailureThenSucceeds()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "fixture.mp4");
        var attempts = 0;
        var delays = new List<TimeSpan>();
        using var handler = new StubHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(attempts == 1
                ? Response(HttpStatusCode.ServiceUnavailable, [])
                : Response(HttpStatusCode.OK, Encoding.ASCII.GetBytes("done")));
        });
        using var client = new HttpClient(handler);
        var engine = new DirectDownloadEngine(
            client,
            DownloadUriPolicy.YouTubeMediaAndLoopback,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await engine.DownloadAsync(Request(destination, 4));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, attempts);
        Assert.Equal(1, delays.Count);
        Assert.True(delays[0] >= TimeSpan.FromMilliseconds(500));
    }

    [Test]
    public static async Task ClassifiesForbiddenAsARejectedStreamLinkWithoutRetrying()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "fixture.mp4");
        var attempts = 0;
        using var handler = new StubHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(Response(HttpStatusCode.Forbidden, []));
        });
        using var client = new HttpClient(handler);
        var engine = new DirectDownloadEngine(
            client,
            DownloadUriPolicy.YouTubeMediaAndLoopback,
            (_, _) => Task.CompletedTask);

        var result = await engine.DownloadAsync(Request(destination, 4));

        Assert.False(result.IsSuccess);
        // Retrying the same expired link cannot succeed; the caller re-resolves instead.
        Assert.Equal(DirectDownloadEngine.MediaUrlRejectedCode, result.Error?.Code);
        Assert.False(result.Error?.IsTransient == true);
        Assert.Equal(1, attempts);
    }

    [Test]
    public static async Task RespectsConfiguredRetryAttemptLimit()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "fixture.mp4");
        var attempts = 0;
        using var handler = new StubHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(Response(HttpStatusCode.ServiceUnavailable, []));
        });
        using var client = new HttpClient(handler);
        var engine = new DirectDownloadEngine(
            client,
            DownloadUriPolicy.YouTubeMediaAndLoopback,
            (_, _) => Task.CompletedTask,
            maximumAttempts: 1);

        var result = await engine.DownloadAsync(Request(destination, 4));

        Assert.False(result.IsSuccess);
        Assert.Equal(1, attempts);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new DirectDownloadEngine(client, maximumAttempts: 6));
    }

    [Test]
    public static async Task HonorsBoundedRetryAfterBeforeRetryingRateLimit()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "fixture.mp4");
        var attempts = 0;
        var delays = new List<TimeSpan>();
        using var handler = new StubHandler((_, _) =>
        {
            attempts++;
            if (attempts > 1)
            {
                return Task.FromResult(Response(HttpStatusCode.OK, Encoding.ASCII.GetBytes("done")));
            }

            var response = Response(HttpStatusCode.TooManyRequests, []);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(11));
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var engine = new DirectDownloadEngine(
            client,
            DownloadUriPolicy.YouTubeMediaAndLoopback,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await engine.DownloadAsync(Request(destination, 4));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, attempts);
        Assert.SequenceEqual(new[] { TimeSpan.FromSeconds(11) }, delays);
    }

    [Test]
    public static async Task LeavesPartialWhenServerEndsEarly()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "fixture.mp4");
        var attempts = 0;
        using var handler = new StubHandler((_, _) =>
        {
            attempts++;
            var response = Response(HttpStatusCode.OK, Encoding.ASCII.GetBytes("short"));
            response.Content.Headers.ContentLength = 10;
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var engine = Engine(client);

        var result = await engine.DownloadAsync(Request(destination, 10));

        Assert.False(result.IsSuccess);
        Assert.Equal("Download.Incomplete", result.Error?.Code);
        Assert.Equal(DownloadRetryPolicy.MaximumAttempts, attempts);
        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(destination + ".part"));
        Assert.Equal(5L, new FileInfo(destination + ".part").Length);
    }

    [Test]
    public static async Task CancellationStopsWithoutRetry()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "fixture.mp4");
        var attempts = 0;
        using var handler = new StubHandler(async (_, cancellationToken) =>
        {
            attempts++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        });
        using var client = new HttpClient(handler);
        var engine = Engine(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await engine.DownloadAsync(Request(destination, 10), cancellationToken: cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("Operation.Cancelled", result.Error?.Code);
        Assert.Equal(1, attempts);
    }

    [Test]
    public static async Task RejectsSpoofedMediaHostBeforeRequest()
    {
        using var directory = new TestDirectory();
        var requests = 0;
        using var handler = new StubHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(Response(HttpStatusCode.OK, []));
        });
        using var client = new HttpClient(handler);
        var engine = new DirectDownloadEngine(client);
        var request = Request(Path.Combine(directory.Path, "fixture.mp4"), 1) with
        {
            SourceUrl = new Uri("https://googlevideo.com.evil.test/media")
        };

        var result = await engine.DownloadAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("Download.UnsafeSource", result.Error?.Code);
        Assert.Equal(0, requests);
    }

    [Test]
    public static async Task ResumesAfterRealSocketDropsMidResponse()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "socket-fixture.mp4");
        var payload = Encoding.ASCII.GetBytes("hello-world");
        await using var server = LoopbackHttpFaultServer.StartTruncatedThenResumable(payload, 5);
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var engine = Engine(client);
        var request = Request(destination, payload.Length) with { SourceUrl = server.MediaUri };

        var result = await engine.DownloadAsync(request);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value.Resumed);
        Assert.SequenceEqual(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(2, server.Requests.Count);
        Assert.True(server.Requests[1].Contains("Range: bytes=5-", StringComparison.OrdinalIgnoreCase));
        Assert.True(server.Requests[1].Contains("If-Range: \"socket-v1\"", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public static async Task RefusesToFinalizePayloadWithInvalidDeclaredContainer()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "invalid.mp4");
        var payload = Encoding.ASCII.GetBytes("not-an-mp4-container");
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, payload)));
        using var client = new HttpClient(handler);
        var engine = Engine(client);
        var request = Request(destination, payload.Length) with
        {
            ExpectedContainer = TubeForge.Core.Media.MediaContainer.Mp4
        };

        var result = await engine.DownloadAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("Media.InvalidStructure", result.Error?.Code);
        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(destination + ".part"));
    }

    [Test]
    public static async Task DownloadsThroughExplicitHttpProxy()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "proxy-fixture.mp4");
        var payload = Encoding.ASCII.GetBytes("proxied-media");
        await using var proxy = LoopbackHttpResponseServer.Start(IPAddress.Loopback, payload);
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxy.EndpointUri, BypassOnLocal: false),
            UseProxy = true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var engine = Engine(client);
        var request = Request(destination, payload.Length) with
        {
            SourceUrl = new Uri("http://localhost:49152/media")
        };

        var result = await engine.DownloadAsync(request);
        var proxyRequest = await proxy.Request;

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.SequenceEqual(payload, await File.ReadAllBytesAsync(destination));
        Assert.True(proxyRequest.StartsWith(
            "GET http://localhost:49152/media HTTP/1.1",
            StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public static async Task DownloadsOverIpv4AndIpv6Loopback()
    {
        using var directory = new TestDirectory();
        var payload = Encoding.ASCII.GetBytes("address-family-media");

        await DownloadFromLoopbackAsync(
            IPAddress.Loopback,
            Path.Combine(directory.Path, "ipv4.mp4"),
            payload);

        if (await HasIpv6LoopbackConnectivityAsync())
        {
            await DownloadFromLoopbackAsync(
                IPAddress.IPv6Loopback,
                Path.Combine(directory.Path, "ipv6.mp4"),
                payload);
        }
    }

    private static async Task<bool> HasIpv6LoopbackConnectivityAsync()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return false;
        }

        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        try
        {
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(endpoint, timeout.Token);
            using var accepted = await listener.AcceptSocketAsync(timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public static async Task ContinuesAfterStalledResponseResumes()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "stalled-response.mp4");
        var payload = Enumerable.Range(0, 256 * 1024).Select(index => (byte)(index % 251)).ToArray();
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var contentStream = new GatedReadStream(payload, stalled, resume.Task);
        using var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(contentStream)
            };
            response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var engine = Engine(client);

        var download = engine.DownloadAsync(Request(destination, payload.Length));
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(download.IsCompleted);
        resume.SetResult();
        var result = await download;

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.SequenceEqual(payload, await File.ReadAllBytesAsync(destination));
    }

    [Test]
    public static async Task BackpressureFromSlowDestinationDoesNotLoseBytes()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "slow-destination.mp4");
        var payload = Enumerable.Range(0, 256 * 1024).Select(index => (byte)(index % 241)).ToArray();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, payload)));
        using var client = new HttpClient(handler);
        var engine = new DirectDownloadEngine(
            client,
            DownloadUriPolicy.YouTubeMediaAndLoopback,
            (_, _) => Task.CompletedTask,
            (path, append) => new GatedWriteStream(
                new FileStream(
                    path,
                    append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan),
                writeStarted,
                releaseWrite.Task));

        var download = engine.DownloadAsync(Request(destination, payload.Length));
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(download.IsCompleted);
        releaseWrite.SetResult();
        var result = await download;

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.SequenceEqual(payload, await File.ReadAllBytesAsync(destination));
    }

    [Test]
    public static async Task SegmentedTransferUsesConcurrentValidatedRangesWithoutByteLoss()
    {
        const string providerUserAgent =
            "Mozilla/5.0 (ChromiumStylePlatform) Cobalt/25.lts.30.1034943-gold " +
            "(unlike Gecko), Unknown_TV_Unknown_0/Unknown (Unknown, Unknown)";
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "segmented.mp4");
        var payload = Enumerable.Range(0, 1024 * 1024).Select(index => (byte)(index % 239)).ToArray();
        const int segmentBytes = 64 * 1024;
        var allRangesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rangeCount = 0;
        var active = 0;
        var maximumActive = 0;
        using var handler = new StubHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(providerUserAgent, request.Headers.GetValues("User-Agent").Single());
            var range = request.Headers.Range?.Ranges.Single();
            Assert.True(range?.From is not null && range.To is not null);
            var currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, currentActive);
            if (Interlocked.Increment(ref rangeCount) == 4)
            {
                allRangesStarted.SetResult();
            }

            await allRangesStarted.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref active);
            return RangeResponse(payload, range!.From!.Value, range.To!.Value, "\"segmented-v1\"");
        });
        using var client = new HttpClient(handler);
        var engine = Engine(client);

        var result = await engine.DownloadAsync(SegmentedRequest(destination, payload.Length) with
        {
            HttpUserAgent = providerUserAgent,
            SegmentedTransferChunkBytes = segmentBytes
        });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.Value.Resumed);
        Assert.Equal(payload.Length / segmentBytes, rangeCount);
        Assert.Equal(4, maximumActive);
        Assert.SequenceEqual(payload, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
        Assert.False(File.Exists(destination + ".part.segments.json"));
    }

    [Test]
    public static async Task SegmentedGoogleVideoUsesConcurrentQueryRangesWithoutHeaderRanges()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "segmented-googlevideo.mp4");
        var payload = Enumerable.Range(0, 1024 * 1024).Select(index => (byte)(index % 227)).ToArray();
        var allRangesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestNumbers = new ConcurrentBag<string>();
        var rangeCount = 0;
        var active = 0;
        var maximumActive = 0;
        using var handler = new StubHandler(async (request, cancellationToken) =>
        {
            Assert.True(request.Headers.Range is null);
            var bounds = QueryValue(request.RequestUri!, "range")!
                .Split('-')
                .Select(long.Parse)
                .ToArray();
            requestNumbers.Add(QueryValue(request.RequestUri!, "rn")!);
            var currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, currentActive);
            if (Interlocked.Increment(ref rangeCount) == 4)
            {
                allRangesStarted.SetResult();
            }

            await allRangesStarted.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref active);
            var length = checked((int)(bounds[1] - bounds[0] + 1));
            var content = new byte[length];
            Buffer.BlockCopy(payload, checked((int)bounds[0]), content, 0, length);
            return Response(HttpStatusCode.OK, content);
        });
        using var client = new HttpClient(handler);
        var engine = Engine(client);

        var result = await engine.DownloadAsync(SegmentedRequest(destination, payload.Length) with
        {
            SourceUrl = new Uri("https://r1---sn-fixture.googlevideo.com/videoplayback?id=fixture")
        });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(4, rangeCount);
        Assert.Equal(4, maximumActive);
        Assert.Equal(4, requestNumbers.Distinct(StringComparer.Ordinal).Count());
        Assert.SequenceEqual(payload, await File.ReadAllBytesAsync(destination));
    }

    [Test]
    public static async Task SegmentedTransferResumesCompletedRangesAfterTransientFailure()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "segmented-resume.mp4");
        var payload = Enumerable.Range(0, 512 * 1024).Select(index => (byte)(index % 233)).ToArray();
        var segmentBytes = payload.Length / 4;
        await using (var partial = new FileStream(destination + ".part", FileMode.CreateNew, FileAccess.Write))
        {
            partial.SetLength(payload.Length);
            await partial.WriteAsync(payload.AsMemory(0, segmentBytes));
        }

        await SegmentedDownloadStateStore.WriteAsync(
            destination + ".part.segments.json",
            new SegmentedDownloadState
            {
                SourceIdentity = "Fixture123_:22",
                ExpectedLength = payload.Length,
                SegmentCount = 4,
                SegmentBytes = segmentBytes,
                Completed = [true, false, false, false]
            },
            CancellationToken.None);
        var requestsByStart = new ConcurrentDictionary<long, int>();
        using var handler = new StubHandler((request, _) =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var start = range.From!.Value;
            var requestCount = requestsByStart.AddOrUpdate(start, 1, (_, count) => count + 1);
            if (start == segmentBytes && requestCount == 1)
            {
                return Task.FromResult(Response(HttpStatusCode.ServiceUnavailable, []));
            }

            return Task.FromResult(RangeResponse(
                payload,
                start,
                range.To!.Value,
                "\"segmented-v1\""));
        });
        using var client = new HttpClient(handler);
        var engine = Engine(client);

        var result = await engine.DownloadAsync(SegmentedRequest(destination, payload.Length) with
        {
            EnableSegmentedTransfer = false
        });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value.Resumed);
        Assert.False(requestsByStart.ContainsKey(0));
        Assert.Equal(2, requestsByStart[segmentBytes]);
        Assert.SequenceEqual(payload, await File.ReadAllBytesAsync(destination));
    }

    [Test]
    public static async Task SegmentedTransferFallsBackWhenRangesAreUnsupported()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "segmented-fallback.mp4");
        var payload = Encoding.ASCII.GetBytes("range-fallback-payload");
        var rangeRequests = 0;
        var wholeObjectRequests = 0;
        using var handler = new StubHandler((request, _) =>
        {
            var range = request.Headers.Range?.Ranges.Single();
            if (range?.From == 0 && range.To == payload.Length - 1)
            {
                Interlocked.Increment(ref wholeObjectRequests);
            }
            else
            {
                Interlocked.Increment(ref rangeRequests);
            }

            return Task.FromResult(Response(HttpStatusCode.OK, payload));
        });
        using var client = new HttpClient(handler);
        var engine = Engine(client);

        var result = await engine.DownloadAsync(SegmentedRequest(destination, payload.Length));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(rangeRequests is >= 1 and <= 4);
        Assert.Equal(1, wholeObjectRequests);
        Assert.SequenceEqual(payload, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part.segments.json"));
    }

    [Test]
    public static async Task SegmentedTransferRejectsValidatorMismatchWithoutPublishingOutput()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "segmented-mismatch.mp4");
        var payload = Enumerable.Range(0, 256 * 1024).Select(index => (byte)(index % 229)).ToArray();
        using var handler = new StubHandler((request, _) =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var tag = range.From == 0 ? "\"version-a\"" : "\"version-b\"";
            return Task.FromResult(RangeResponse(payload, range.From!.Value, range.To!.Value, tag));
        });
        using var client = new HttpClient(handler);
        var engine = Engine(client);

        var result = await engine.DownloadAsync(SegmentedRequest(destination, payload.Length));

        Assert.False(result.IsSuccess);
        Assert.Equal("Download.RemoteChanged", result.Error?.Code);
        Assert.False(File.Exists(destination));
    }

    [Test]
    public static async Task SegmentedProgressCountsCompletedRangesInsteadOfPreallocatedLength()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Path, "segmented-progress.mp4");
        await using (var partial = new FileStream(destination + ".part", FileMode.CreateNew, FileAccess.Write))
        {
            partial.SetLength(100);
        }

        await SegmentedDownloadStateStore.WriteAsync(
            destination + ".part.segments.json",
            new SegmentedDownloadState
            {
                SourceIdentity = "Fixture123_:22",
                ExpectedLength = 100,
                SegmentCount = 4,
                SegmentBytes = 25,
                Completed = [true, false, true, false]
            },
            CancellationToken.None);

        Assert.Equal(50L, SegmentedTransferProgress.GetCompletedBytes(destination));

        await SegmentedDownloadStateStore.WriteAsync(
            destination + ".part.segments.json",
            new SegmentedDownloadState
            {
                SchemaVersion = 1,
                SourceIdentity = "Fixture123_:22",
                ExpectedLength = 100,
                SegmentCount = 4,
                Completed = [true, false, true, false]
            },
            CancellationToken.None);

        Assert.Equal(50L, SegmentedTransferProgress.GetCompletedBytes(destination));
    }

    private static async Task DownloadFromLoopbackAsync(
        IPAddress address,
        string destination,
        byte[] payload)
    {
        await using var server = LoopbackHttpResponseServer.Start(address, payload);
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var result = await Engine(client).DownloadAsync(Request(destination, payload.Length) with
        {
            SourceUrl = server.EndpointUri
        });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.SequenceEqual(payload, await File.ReadAllBytesAsync(destination));
        Assert.True((await server.Request).StartsWith("GET /media HTTP/1.1", StringComparison.Ordinal));
    }

    private static DirectDownloadEngine Engine(HttpClient client) => new(
        client,
        DownloadUriPolicy.YouTubeMediaAndLoopback,
        (_, _) => Task.CompletedTask);

    private static DownloadRequest Request(string destination, long expectedLength) => new()
    {
        SourceUrl = new Uri("http://localhost/media"),
        SourceIdentity = "Fixture123_:22",
        DestinationPath = destination,
        ExpectedLength = expectedLength
    };

    private static DownloadRequest SegmentedRequest(string destination, long expectedLength) =>
        Request(destination, expectedLength) with
        {
            EnableSegmentedTransfer = true,
            MaximumSegments = 4,
            SegmentedTransferMinimumBytes = 1,
            SegmentedTransferChunkBytes = Math.Max(1, (expectedLength + 3) / 4)
        };

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] content) => new(status)
    {
        Content = new ByteArrayContent(content)
    };

    private static HttpResponseMessage RangeResponse(
        byte[] payload,
        long from,
        long to,
        string entityTag)
    {
        var length = checked((int)(to - from + 1));
        var content = new byte[length];
        Buffer.BlockCopy(payload, checked((int)from), content, 0, length);
        var response = Response(HttpStatusCode.PartialContent, content);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, payload.Length);
        response.Headers.ETag = new EntityTagHeaderValue(entityTag);
        return response;
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
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

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responseFactory(request, cancellationToken);
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private sealed class GatedReadStream(
        byte[] payload,
        TaskCompletionSource stalled,
        Task resume) : Stream
    {
        private readonly MemoryStream _inner = new(payload, writable: false);
        private bool _firstRead = true;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_firstRead)
            {
                _firstRead = false;
                return await _inner.ReadAsync(buffer[..Math.Min(buffer.Length, payload.Length / 2)], cancellationToken);
            }

            stalled.TrySetResult();
            await resume.WaitAsync(cancellationToken);
            return await _inner.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class GatedWriteStream(
        Stream inner,
        TaskCompletionSource writeStarted,
        Task releaseWrite) : Stream
    {
        private bool _firstWrite = true;

        public override bool CanRead => false;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_firstWrite)
            {
                _firstWrite = false;
                writeStarted.TrySetResult();
                await releaseWrite.WaitAsync(cancellationToken);
            }

            await inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
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
