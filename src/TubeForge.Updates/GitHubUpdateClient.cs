using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using TubeForge.Core.Errors;
using TubeForge.Core.Results;

namespace TubeForge.Updates;

public sealed class GitHubUpdateClient(HttpClient httpClient)
{
    private const int MaximumReleaseJsonBytes = 2 * 1024 * 1024;
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/0langa/TubeForge/releases/latest");
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<Result<UpdateRelease?>> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        try
        {
            using var request = CreateRequest(HttpMethod.Get, LatestReleaseUri, "application/vnd.github+json");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Result<UpdateRelease?>.Success(null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failure<UpdateRelease?>(
                    "Update.CheckFailed",
                    "TubeForge could not check for updates.",
                    $"HTTP {(int)response.StatusCode}");
            }

            var bytes = await ReadBoundedAsync(
                response,
                MaximumReleaseJsonBytes,
                cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return Failure<UpdateRelease?>(
                    "Update.InvalidRelease",
                    "The update release metadata exceeded TubeForge limits.");
            }

            return GitHubReleasePolicy.ParseLatest(Encoding.UTF8.GetString(bytes), currentVersion);
        }
        catch (OperationCanceledException)
        {
            return Failure<UpdateRelease?>("Operation.Cancelled", "The update check was cancelled.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Failure<UpdateRelease?>(
                "Update.CheckFailed",
                "TubeForge could not check for updates.",
                exception.GetType().Name);
        }
    }

    public async Task<Result<UpdateDownloadReceipt>> DownloadInstallerAsync(
        UpdateRelease release,
        string updateDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(updateDirectory);
        var temporary = string.Empty;
        progress?.Report(0);
        try
        {
            var directory = Path.GetFullPath(updateDirectory);
            Directory.CreateDirectory(directory);
            var destination = DirectChild(directory, release.SetupAssetName);
            temporary = destination + ".part";

            var checksumBytes = await DownloadBytesAsync(
                release.ChecksumsDownloadUri,
                release.ChecksumsLength,
                GitHubReleasePolicy.MaximumChecksumsBytes,
                release.ChecksumsSha256,
                cancellationToken).ConfigureAwait(false);
            if (!checksumBytes.IsSuccess)
            {
                return Result<UpdateDownloadReceipt>.Failure(checksumBytes.Error!);
            }

            var checksum = GitHubReleasePolicy.ParseSetupChecksum(
                checksumBytes.Value,
                release.SetupAssetName);
            if (!checksum.IsSuccess ||
                !checksum.Value.Equals(release.SetupSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Failure<UpdateDownloadReceipt>(
                    "Update.DigestMismatch",
                    "The release API digest and checksum manifest disagree.");
            }

            progress?.Report(0.05);

            if (File.Exists(destination) &&
                await FileMatchesAsync(
                    destination,
                    release.SetupLength,
                    release.SetupSha256,
                    value => progress?.Report(0.05 + (value * 0.10)),
                    cancellationToken).ConfigureAwait(false))
            {
                progress?.Report(1);
                return Result<UpdateDownloadReceipt>.Success(new UpdateDownloadReceipt(
                    destination,
                    release.Version,
                    release.SetupLength,
                    release.SetupSha256));
            }

            progress?.Report(0.15);
            TryDelete(temporary);
            using var response = await SendAssetRequestAsync(
                release.SetupDownloadUri,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength is long contentLength &&
                contentLength != release.SetupLength)
            {
                return Failure<UpdateDownloadReceipt>(
                    "Update.DownloadFailed",
                    "The update installer response failed validation.",
                    $"HTTP {(int)response.StatusCode}");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > release.SetupLength || total > GitHubReleasePolicy.MaximumInstallerBytes)
                {
                    return Failure<UpdateDownloadReceipt>(
                        "Update.DownloadTooLarge",
                        "The update installer exceeded its declared size.");
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(0.15 + (0.85 * total / release.SetupLength));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (total != release.SetupLength ||
                !actualHash.Equals(release.SetupSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Failure<UpdateDownloadReceipt>(
                    "Update.DigestMismatch",
                    "The downloaded update installer failed SHA-256 validation.");
            }

            await output.DisposeAsync().ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(1);
            return Result<UpdateDownloadReceipt>.Success(new UpdateDownloadReceipt(
                destination,
                release.Version,
                total,
                actualHash));
        }
        catch (OperationCanceledException)
        {
            return Failure<UpdateDownloadReceipt>("Operation.Cancelled", "The update download was cancelled.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return Failure<UpdateDownloadReceipt>(
                "Update.DownloadFailed",
                "TubeForge could not download the update installer.",
                exception.GetType().Name);
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporary))
            {
                TryDelete(temporary);
            }
        }
    }

    private async Task<Result<byte[]>> DownloadBytesAsync(
        Uri uri,
        long expectedLength,
        long maximumLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        using var response = await SendAssetRequestAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode ||
            response.Content.Headers.ContentLength is long contentLength && contentLength != expectedLength)
        {
            return Failure<byte[]>(
                "Update.DownloadFailed",
                "The update checksum response failed validation.");
        }

        var bytes = await ReadBoundedAsync(response, maximumLength, cancellationToken).ConfigureAwait(false);
        if (bytes is null || bytes.LongLength != expectedLength)
        {
            return Failure<byte[]>(
                "Update.DownloadFailed",
                "The update checksum response had an invalid size.");
        }

        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)
            ? Result<byte[]>.Success(bytes)
            : Failure<byte[]>(
                "Update.DigestMismatch",
                "The update checksum file failed SHA-256 validation.");
    }

    private async Task<HttpResponseMessage> SendAssetRequestAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        if (!IsGitHubReleaseUri(uri))
        {
            throw new HttpRequestException("Update asset URL failed host policy.");
        }

        using var firstRequest = CreateRequest(HttpMethod.Get, uri, "application/octet-stream");
        var response = await _httpClient.SendAsync(
            firstRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!IsRedirect(response.StatusCode))
        {
            return response;
        }

        var redirect = response.Headers.Location;
        response.Dispose();
        if (redirect is null || !redirect.IsAbsoluteUri ||
            redirect.Scheme != Uri.UriSchemeHttps ||
            redirect.Host != "release-assets.githubusercontent.com")
        {
            throw new HttpRequestException("Update asset redirect failed host policy.");
        }

        using var redirectedRequest = CreateRequest(HttpMethod.Get, redirect, "application/octet-stream");
        return await _httpClient.SendAsync(
            redirectedRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string accept)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("TubeForge-Updater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        return request;
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpResponseMessage response,
        long maximumLength,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > maximumLength)
        {
            return null;
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumLength)
            {
                return null;
            }

            output.Write(buffer, 0, read);
        }
    }

    private static async Task<bool> FileMatchesAsync(
        string path,
        long expectedLength,
        string expectedSha256,
        Action<double>? progress,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedLength)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        progress?.Invoke(0);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > expectedLength)
            {
                return false;
            }

            hash.AppendData(buffer.AsSpan(0, read));
            progress?.Invoke((double)total / expectedLength);
        }

        if (total != expectedLength)
        {
            return false;
        }

        progress?.Invoke(1);
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string DirectChild(string directory, string fileName)
    {
        if (fileName.Length is 0 or > 160 ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Path.GetFileName(fileName) != fileName)
        {
            throw new IOException("Update filename failed path policy.");
        }

        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Update path escaped staging directory.");
        }

        return path;
    }

    private static bool IsGitHubReleaseUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host == "github.com" &&
        uri.AbsolutePath.StartsWith("/0langa/TubeForge/releases/download/", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static Result<T> Failure<T>(string code, string message, string? detail = null) =>
        Result<T>.Failure(new TubeForgeError(code, message, detail));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
