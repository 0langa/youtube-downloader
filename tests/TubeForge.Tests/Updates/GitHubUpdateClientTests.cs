using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TubeForge.Tests.Framework;
using TubeForge.Updates;

namespace TubeForge.Tests.Updates;

public static class GitHubUpdateClientTests
{
    [Test]
    public static void ParsesStrictStableReleaseAndChecksumAgreement()
    {
        var fixture = ReleaseFixture.Create();
        var result = GitHubReleasePolicy.ParseLatest(fixture.Json, new Version(1, 0, 0));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new Version(1, 1, 0), result.Value!.Version);
        Assert.Equal(fixture.SetupHash, result.Value.SetupSha256);
        var checksum = GitHubReleasePolicy.ParseSetupChecksum(
            fixture.ChecksumBytes,
            fixture.SetupName);
        Assert.True(checksum.IsSuccess);
        Assert.Equal(fixture.SetupHash, checksum.Value);
    }

    [Test]
    public static void RejectsPrereleaseWrongRepositoryAndAmbiguousChecksum()
    {
        var fixture = ReleaseFixture.Create();
        var prerelease = fixture.Json.Replace("\"prerelease\":false", "\"prerelease\":true", StringComparison.Ordinal);
        var wrongRepository = fixture.Json.Replace("/0langa/TubeForge/", "/attacker/TubeForge/", StringComparison.Ordinal);
        var ambiguous = fixture.ChecksumBytes
            .Concat(fixture.ChecksumBytes)
            .ToArray();

        Assert.False(GitHubReleasePolicy.ParseLatest(prerelease, new Version(1, 0, 0)).IsSuccess);
        Assert.False(GitHubReleasePolicy.ParseLatest(wrongRepository, new Version(1, 0, 0)).IsSuccess);
        Assert.False(GitHubReleasePolicy.ParseSetupChecksum(ambiguous, fixture.SetupName).IsSuccess);
        Assert.True(GitHubReleasePolicy.ParseLatest(fixture.Json, new Version(1, 1, 0)).IsSuccess);
        Assert.True(GitHubReleasePolicy.ParseLatest(fixture.Json, new Version(1, 1, 0)).Value is null);
    }

    [Test]
    public static async Task DownloadsRedirectedInstallerWithApiAndManifestDigests()
    {
        var fixture = ReleaseFixture.Create();
        using var client = new HttpClient(new ReleaseHandler(fixture))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var updates = new GitHubUpdateClient(client);
        var release = await updates.CheckForUpdateAsync(new Version(1, 0, 0));
        Assert.True(release.IsSuccess && release.Value is not null, release.Error?.Message);
        var available = release.Value!;

        var directory = Path.Combine(Path.GetTempPath(), $"tubeforge-update-{Guid.NewGuid():N}");
        try
        {
            var download = await updates.DownloadInstallerAsync(available, directory);
            Assert.True(download.IsSuccess, download.Error?.Message);
            Assert.Equal(fixture.SetupHash, download.Value.Sha256);
            Assert.Equal(fixture.SetupBytes.LongLength, download.Value.BytesWritten);
            Assert.True(File.Exists(download.Value.InstallerPath));
            Assert.False(File.Exists(download.Value.InstallerPath + ".part"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public static async Task RejectsTamperedInstallerAndCleansPartialFile()
    {
        var fixture = ReleaseFixture.Create();
        using var client = new HttpClient(new ReleaseHandler(fixture, tamperSetup: true))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var updates = new GitHubUpdateClient(client);
        var release = GitHubReleasePolicy.ParseLatest(fixture.Json, new Version(1, 0, 0)).Value!;
        var directory = Path.Combine(Path.GetTempPath(), $"tubeforge-update-{Guid.NewGuid():N}");
        try
        {
            var download = await updates.DownloadInstallerAsync(release, directory);
            Assert.False(download.IsSuccess);
            Assert.Equal("Update.DigestMismatch", download.Error!.Code);
            Assert.False(Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public static async Task CachedInstallerVerificationReportsMonotonicProgress()
    {
        var fixture = ReleaseFixture.Create();
        using var client = new HttpClient(new ReleaseHandler(fixture))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var updates = new GitHubUpdateClient(client);
        var release = GitHubReleasePolicy.ParseLatest(fixture.Json, new Version(1, 0, 0)).Value!;
        var directory = Path.Combine(Path.GetTempPath(), $"tubeforge-update-cache-{Guid.NewGuid():N}");
        try
        {
            var first = await updates.DownloadInstallerAsync(release, directory);
            Assert.True(first.IsSuccess, first.Error?.Message);

            var progress = new CapturingProgress();
            var cached = await updates.DownloadInstallerAsync(release, directory, progress);

            Assert.True(cached.IsSuccess, cached.Error?.Message);
            Assert.True(progress.Values.Count > 3);
            Assert.Equal(0d, progress.Values[0]);
            Assert.Equal(1d, progress.Values[^1]);
            Assert.True(progress.Values.Zip(progress.Values.Skip(1), (left, right) => right >= left).All(value => value));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class CapturingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    private sealed record ReleaseFixture(
        string Json,
        string SetupName,
        byte[] SetupBytes,
        string SetupHash,
        byte[] ChecksumBytes,
        string ChecksumHash)
    {
        public static ReleaseFixture Create()
        {
            var setupName = "TubeForge-1.1.0-win-x64-setup.exe";
            var setupBytes = Enumerable.Range(0, 1024 * 1024)
                .Select(index => (byte)(index * 31))
                .ToArray();
            var setupHash = Hash(setupBytes);
            var checksums = Encoding.UTF8.GetBytes($"{setupHash}  {setupName}\n");
            var checksumHash = Hash(checksums);
            var json = JsonSerializer.Serialize(new
            {
                tag_name = "v1.1.0",
                html_url = "https://github.com/0langa/TubeForge/releases/tag/v1.1.0",
                draft = false,
                prerelease = false,
                assets = new object[]
                {
                    new
                    {
                        name = setupName,
                        size = setupBytes.LongLength,
                        digest = "sha256:" + setupHash,
                        browser_download_url = $"https://github.com/0langa/TubeForge/releases/download/v1.1.0/{setupName}"
                    },
                    new
                    {
                        name = "SHA256SUMS.txt",
                        size = checksums.LongLength,
                        digest = "sha256:" + checksumHash,
                        browser_download_url = "https://github.com/0langa/TubeForge/releases/download/v1.1.0/SHA256SUMS.txt"
                    }
                }
            });
            return new ReleaseFixture(json, setupName, setupBytes, setupHash, checksums, checksumHash);
        }

        private static string Hash(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class ReleaseHandler(ReleaseFixture fixture, bool tamperSetup = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.github.com")
            {
                return Response(HttpStatusCode.OK, new StringContent(
                    fixture.Json,
                    Encoding.UTF8,
                    "application/json"));
            }

            if (uri.Host == "github.com")
            {
                var target = uri.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                    ? "https://release-assets.githubusercontent.com/checksums"
                    : "https://release-assets.githubusercontent.com/setup";
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri(target);
                return Task.FromResult(redirect);
            }

            if (uri.AbsolutePath == "/checksums")
            {
                return Response(HttpStatusCode.OK, new ByteArrayContent(fixture.ChecksumBytes));
            }

            if (uri.AbsolutePath == "/setup")
            {
                var bytes = fixture.SetupBytes.ToArray();
                if (tamperSetup)
                {
                    bytes[^1] ^= 0xff;
                }

                return Response(HttpStatusCode.OK, new ByteArrayContent(bytes));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Response(HttpStatusCode status, HttpContent content) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = content });
    }
}
