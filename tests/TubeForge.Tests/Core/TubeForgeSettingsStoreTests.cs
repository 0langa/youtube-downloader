using TubeForge.Core.Settings;
using TubeForge.Tests.Framework;
using System.Text.Json;

namespace TubeForge.Tests.Core;

public static class TubeForgeSettingsStoreTests
{
    [Test]
    public static async Task ReturnsDefaultsThenPersistsValidatedSettings()
    {
        using var directory = new TestDirectory();
        var store = new TubeForgeSettingsStore(Path.Combine(directory.Path, "settings.json"));
        var defaults = Settings(directory.Path);

        var missing = await store.LoadAsync(defaults);
        var save = await store.SaveAsync(defaults with
        {
            MaximumConcurrentDownloads = 4,
            FileNameTemplate = "{channel} - {title}",
            IncludeQualityInFileName = true,
            EnableAcceleratedTransfers = false,
            EnableAutomaticUpdateChecks = false,
            ProxyMode = NetworkProxyMode.Manual,
            ManualProxyUri = "http://127.0.0.1:8080/",
            MetadataTimeoutSeconds = 45,
            DownloadRetryAttempts = 5,
            PerHostConcurrency = 1,
            DefaultDownloadPreset = PreferredDownloadPreset.SmallFile,
            ShowAdvancedDownloadOptions = true,
            ResponsibleUseAccepted = true
        });
        var loaded = await store.LoadAsync(defaults);

        Assert.True(missing.IsSuccess);
        Assert.Equal(2, missing.Value.MaximumConcurrentDownloads);
        Assert.True(save.IsSuccess);
        Assert.True(loaded.IsSuccess);
        Assert.Equal(4, loaded.Value.MaximumConcurrentDownloads);
        Assert.Equal("{channel} - {title}", loaded.Value.FileNameTemplate);
        Assert.True(loaded.Value.IncludeQualityInFileName);
        Assert.False(loaded.Value.EnableAcceleratedTransfers);
        Assert.False(loaded.Value.EnableAutomaticUpdateChecks);
        Assert.Equal(NetworkProxyMode.Manual, loaded.Value.ProxyMode);
        Assert.Equal("http://127.0.0.1:8080/", loaded.Value.ManualProxyUri);
        Assert.Equal(45, loaded.Value.MetadataTimeoutSeconds);
        Assert.Equal(5, loaded.Value.DownloadRetryAttempts);
        Assert.Equal(1, loaded.Value.PerHostConcurrency);
        Assert.Equal(PreferredDownloadPreset.SmallFile, loaded.Value.DefaultDownloadPreset);
        Assert.True(loaded.Value.ShowAdvancedDownloadOptions);
        Assert.True(loaded.Value.ResponsibleUseAccepted);
    }

    [Test]
    public static async Task PreservesSettingsWrittenByANewerBuildBeforeDefaultsCanReplaceThem()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var future = """
            {"schemaVersion":999,"downloadFolder":"C:\Downloads","maximumConcurrentDownloads":2}
            """;
        await File.WriteAllTextAsync(path, future);
        var store = new TubeForgeSettingsStore(path);

        var load = await store.LoadAsync(Settings(directory.Path));

        Assert.False(load.IsSuccess);
        // Without the sidecar, the first save from defaults silently discards a configuration the
        // user can still use after reinstalling the newer build.
        var preserved = path + ".unreadable";
        Assert.True(File.Exists(preserved));
        Assert.Equal(future, await File.ReadAllTextAsync(preserved));

        var save = await store.SaveAsync(Settings(directory.Path));
        Assert.True(save.IsSuccess, save.Error?.Message);
        Assert.Equal(future, await File.ReadAllTextAsync(preserved));
    }

    [Test]
    public static async Task RejectsInvalidValuesAndLeavesMalformedFileUntouched()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var store = new TubeForgeSettingsStore(path);
        var invalid = await store.SaveAsync(Settings(directory.Path) with { MaximumConcurrentDownloads = 5 });
        var unsafePath = await store.SaveAsync(Settings(directory.Path) with
        {
            DownloadFolder = Path.GetFullPath(directory.Path) + "\nprivate"
        });
        var invalidTemplate = await store.SaveAsync(Settings(directory.Path) with
        {
            FileNameTemplate = "{unknown}"
        });
        var credentialedProxy = await store.SaveAsync(Settings(directory.Path) with
        {
            ProxyMode = NetworkProxyMode.Manual,
            ManualProxyUri = "http://user:password@127.0.0.1:8080/"
        });
        var unusedProxy = await store.SaveAsync(Settings(directory.Path) with
        {
            ProxyMode = NetworkProxyMode.None,
            ManualProxyUri = "not-used"
        });
        var invalidPreset = await store.SaveAsync(Settings(directory.Path) with
        {
            DefaultDownloadPreset = (PreferredDownloadPreset)99
        });
        await File.WriteAllTextAsync(path, "{ malformed");

        var corrupt = await store.LoadAsync(Settings(directory.Path));

        Assert.False(invalid.IsSuccess);
        Assert.Equal("Settings.InvalidState", invalid.Error?.Code);
        Assert.False(unsafePath.IsSuccess);
        Assert.Equal("Settings.InvalidState", unsafePath.Error?.Code);
        Assert.False(invalidTemplate.IsSuccess);
        Assert.Equal("Settings.InvalidState", invalidTemplate.Error?.Code);
        Assert.False(credentialedProxy.IsSuccess);
        Assert.False(unusedProxy.IsSuccess);
        Assert.False(invalidPreset.IsSuccess);
        Assert.Equal("Settings.InvalidState", credentialedProxy.Error?.Code);
        Assert.Equal("Settings.InvalidState", invalidPreset.Error?.Code);
        Assert.False(corrupt.IsSuccess);
        Assert.Equal("Settings.Corrupt", corrupt.Error?.Code);
        Assert.Equal("{ malformed", await File.ReadAllTextAsync(path));
    }

    [Test]
    public static async Task MigratesPublishedSchemaOneSettingsWithStableDefaults()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            downloadFolder = Path.GetFullPath(directory.Path),
            maximumConcurrentDownloads = 3,
            fileNameTemplate = "{title}",
            enableSegmentedTransfers = true,
            enableAutomaticUpdateChecks = false,
            responsibleUseAccepted = true
        }));

        var loaded = await new TubeForgeSettingsStore(path).LoadAsync(Settings(directory.Path));

        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        Assert.Equal(TubeForgeSettings.CurrentSchemaVersion, loaded.Value.SchemaVersion);
        Assert.Equal(LibrarySortOrder.NewestFirst, loaded.Value.LibrarySortOrder);
        Assert.Equal(3, loaded.Value.MaximumConcurrentDownloads);
        Assert.True(loaded.Value.EnableAcceleratedTransfers);
    }

    [Test]
    public static async Task MigratesPublishedSchemaTwoToAutomaticAcceleration()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            downloadFolder = Path.GetFullPath(directory.Path),
            maximumConcurrentDownloads = 2,
            fileNameTemplate = "{title}",
            enableSegmentedTransfers = false,
            enableAutomaticUpdateChecks = true,
            librarySortOrder = 0,
            responsibleUseAccepted = true
        }));

        var loaded = await new TubeForgeSettingsStore(path).LoadAsync(Settings(directory.Path));

        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        Assert.Equal(TubeForgeSettings.CurrentSchemaVersion, loaded.Value.SchemaVersion);
        Assert.True(loaded.Value.EnableAcceleratedTransfers);
    }

    [Test]
    public static async Task MigratesPublishedSchemaThreeToSafeNetworkDefaults()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            downloadFolder = Path.GetFullPath(directory.Path),
            maximumConcurrentDownloads = 2,
            fileNameTemplate = "{title}",
            enableAcceleratedTransfers = true,
            enableAutomaticUpdateChecks = true,
            librarySortOrder = 0,
            responsibleUseAccepted = true
        }));

        var loaded = await new TubeForgeSettingsStore(path).LoadAsync(Settings(directory.Path));

        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        Assert.Equal(TubeForgeSettings.CurrentSchemaVersion, loaded.Value.SchemaVersion);
        Assert.Equal(NetworkProxyMode.System, loaded.Value.ProxyMode);
        Assert.Equal(string.Empty, loaded.Value.ManualProxyUri);
        Assert.Equal(20, loaded.Value.MetadataTimeoutSeconds);
        Assert.Equal(3, loaded.Value.DownloadRetryAttempts);
        Assert.Equal(2, loaded.Value.PerHostConcurrency);
    }

    [Test]
    public static async Task MigratesPublishedSchemaFourToSimpleBestOriginalDefaults()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 4,
            downloadFolder = Path.GetFullPath(directory.Path),
            maximumConcurrentDownloads = 2,
            fileNameTemplate = "{title}",
            enableAcceleratedTransfers = true,
            enableAutomaticUpdateChecks = true,
            proxyMode = 0,
            manualProxyUri = string.Empty,
            metadataTimeoutSeconds = 20,
            downloadRetryAttempts = 3,
            perHostConcurrency = 2,
            librarySortOrder = 0,
            responsibleUseAccepted = true
        }));

        var loaded = await new TubeForgeSettingsStore(path).LoadAsync(Settings(directory.Path));

        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        Assert.Equal(TubeForgeSettings.CurrentSchemaVersion, loaded.Value.SchemaVersion);
        Assert.Equal(PreferredDownloadPreset.BestOriginal, loaded.Value.DefaultDownloadPreset);
        Assert.False(loaded.Value.ShowAdvancedDownloadOptions);
    }

    [Test]
    public static async Task MigratesSchemaFiveOutputTokensToExplicitQualityPreference()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 5,
            downloadFolder = Path.GetFullPath(directory.Path),
            maximumConcurrentDownloads = 2,
            fileNameTemplate = "{title} {quality} {container}",
            enableAcceleratedTransfers = true,
            enableAutomaticUpdateChecks = true,
            proxyMode = 0,
            manualProxyUri = string.Empty,
            metadataTimeoutSeconds = 20,
            downloadRetryAttempts = 3,
            perHostConcurrency = 2,
            librarySortOrder = 0,
            defaultDownloadPreset = 0,
            showAdvancedDownloadOptions = false,
            responsibleUseAccepted = true
        }));

        var loaded = await new TubeForgeSettingsStore(path).LoadAsync(Settings(directory.Path));

        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        Assert.Equal(TubeForgeSettings.CurrentSchemaVersion, loaded.Value.SchemaVersion);
        Assert.Equal("{title}", loaded.Value.FileNameTemplate);
        Assert.True(loaded.Value.IncludeQualityInFileName);
    }

    private static TubeForgeSettings Settings(string folder) => new()
    {
        DownloadFolder = Path.GetFullPath(folder),
        MaximumConcurrentDownloads = 2
    };

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
