using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TubeForge.App.ViewModels;
using TubeForge.Core.Settings;
using TubeForge.Tests.Framework;

namespace TubeForge.Tests.App;

public static class MainViewModelUpdateTests
{
    [Test]
    public static async Task StartupCheckRaisesPromptAndEnablesUpdateAction()
    {
        var applicationDataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"tubeforge-startup-update-{Guid.NewGuid():N}");
        try
        {
            var settings = new TubeForgeSettings
            {
                DownloadFolder = Path.GetFullPath(applicationDataDirectory),
                EnableAutomaticUpdateChecks = true,
                ResponsibleUseAccepted = true
            };
            var save = await new TubeForgeSettingsStore(
                    Path.Combine(applicationDataDirectory, "settings.json"))
                .SaveAsync(settings);
            Assert.True(save.IsSuccess, save.Error?.Message);

            var constructor = typeof(MainViewModel).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(string), typeof(HttpMessageHandler), typeof(Version)],
                modifiers: null)
                ?? throw new MissingMethodException(
                    typeof(MainViewModel).FullName,
                    ".ctor(string, HttpMessageHandler, Version)");
            using var viewModel = (MainViewModel)(constructor.Invoke(
                [applicationDataDirectory, new LatestReleaseHandler(), new Version(2, 2, 1)])
                ?? throw new InvalidOperationException("Update test view model was not created."));
            var promptedVersion = new TaskCompletionSource<Version>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.UpdateAvailable += (_, eventArgs) =>
                promptedVersion.TrySetResult(eventArgs.Version);

            await viewModel.InitializeAsync();
            var version = await promptedVersion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(new Version(2, 2, 2), version);
            Assert.Equal("2.2.2", viewModel.AvailableUpdateVersion);
            Assert.True(viewModel.AvailableUpdateSummary.Contains("2.2.1", StringComparison.Ordinal));
            Assert.True(viewModel.AvailableUpdateSummary.Contains("2.2.2", StringComparison.Ordinal));
            Assert.True(viewModel.AvailableUpdateSummary.Contains("MB installer", StringComparison.Ordinal));
            Assert.Equal("Ready to update", viewModel.UpdateProgressStage);
            Assert.Equal("0%", viewModel.UpdateProgressPercent);
            Assert.Equal("Update now", viewModel.UpdateActionLabel);
            Assert.True(viewModel.IsUpdateActionAvailable);
            Assert.True(viewModel.UpdateNowCommand.CanExecute(null));
        }
        finally
        {
            if (Directory.Exists(applicationDataDirectory))
            {
                Directory.Delete(applicationDataDirectory, recursive: true);
            }
        }
    }

    [Test]
    public static void GeneralCommandRefreshInvalidatesUpdateAction()
    {
        using var viewModel = new MainViewModel();
        var invalidations = 0;
        viewModel.UpdateNowCommand.CanExecuteChanged += (_, _) => invalidations++;

        var refresh = typeof(MainViewModel).GetMethod(
            "RefreshCommands",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainViewModel).FullName, "RefreshCommands");
        _ = refresh.Invoke(viewModel, null);

        Assert.Equal(1, invalidations);
    }

    [Test]
    public static void UpdateInstallerLaunchUsesWindowsShellWaitsForAppAndRelaunches()
    {
        var factory = typeof(MainViewModel).GetMethod(
            "CreateUpdateInstallerStartInfo",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(MainViewModel).FullName,
                "CreateUpdateInstallerStartInfo");

        var start = (ProcessStartInfo)(factory.Invoke(null, ["C:\\staging\\TubeForge-Setup.exe", 4321])
            ?? throw new InvalidOperationException("Update installer launch plan was not created."));

        Assert.True(start.UseShellExecute);
        Assert.Equal("C:\\staging\\TubeForge-Setup.exe", start.FileName);
        Assert.Equal("C:\\staging", start.WorkingDirectory);
        Assert.SequenceEqual(
            new[] { "/update", "/quiet", "/wait-pid", "4321", "/launch" },
            start.ArgumentList);
    }

    [Test]
    public static void WholeUpdateProgressLocksPromptAndReportsPercent()
    {
        using var viewModel = new MainViewModel();

        SetPrivateProperty(viewModel, nameof(MainViewModel.IsUpdateInProgress), true);
        SetPrivateProperty(viewModel, nameof(MainViewModel.UpdateDownloadFraction), 0.424);
        SetPrivateProperty(viewModel, nameof(MainViewModel.UpdateProgressStage), "Final safety check");

        Assert.False(viewModel.CanDismissUpdatePrompt);
        Assert.Equal("Updating…", viewModel.UpdateActionLabel);
        Assert.Equal("42%", viewModel.UpdateProgressPercent);
        Assert.Equal("Final safety check", viewModel.UpdateProgressStage);

        SetPrivateProperty(viewModel, nameof(MainViewModel.IsUpdateInProgress), false);
        Assert.True(viewModel.CanDismissUpdatePrompt);
        Assert.Equal("Update now", viewModel.UpdateActionLabel);
    }

    [Test]
    public static async Task FinalInstallerVerificationReportsMonotonicProgress()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tubeforge-update-hash-{Guid.NewGuid():N}.bin");
        var bytes = Enumerable.Range(0, 1024 * 1024)
            .Select(index => (byte)(index * 17))
            .ToArray();
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            var progress = new CapturingProgress();
            var method = typeof(MainViewModel).GetMethod(
                "ComputeFileSha256Async",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(MainViewModel).FullName, "ComputeFileSha256Async");
            var task = (Task<string>)(method.Invoke(
                null,
                [path, bytes.LongLength, progress, CancellationToken.None])
                ?? throw new InvalidOperationException("Update hash task was not created."));

            var actual = await task;
            var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            Assert.Equal(expected, actual);
            Assert.True(progress.Values.Count > 3);
            Assert.Equal(0d, progress.Values[0]);
            Assert.Equal(1d, progress.Values[^1]);
            Assert.True(progress.Values.Zip(progress.Values.Skip(1), (left, right) => right >= left).All(value => value));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void SetPrivateProperty(MainViewModel viewModel, string name, object value)
    {
        var property = typeof(MainViewModel).GetProperty(name)
            ?? throw new MissingMemberException(typeof(MainViewModel).FullName, name);
        property.SetValue(viewModel, value);
    }

    private sealed class CapturingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    private sealed class LatestReleaseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host != "api.github.com")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            const string setupName = "TubeForge-2.2.2-win-x64-setup.exe";
            const string setupHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var checksumBytes = Encoding.UTF8.GetBytes($"{setupHash}  {setupName}\n");
            var checksumHash = Convert.ToHexString(SHA256.HashData(checksumBytes)).ToLowerInvariant();
            var json = JsonSerializer.Serialize(new
            {
                tag_name = "v2.2.2",
                html_url = "https://github.com/0langa/TubeForge/releases/tag/v2.2.2",
                draft = false,
                prerelease = false,
                assets = new object[]
                {
                    new
                    {
                        name = setupName,
                        size = 1024 * 1024,
                        digest = "sha256:" + setupHash,
                        browser_download_url = $"https://github.com/0langa/TubeForge/releases/download/v2.2.2/{setupName}"
                    },
                    new
                    {
                        name = "SHA256SUMS.txt",
                        size = checksumBytes.LongLength,
                        digest = "sha256:" + checksumHash,
                        browser_download_url = "https://github.com/0langa/TubeForge/releases/download/v2.2.2/SHA256SUMS.txt"
                    }
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
