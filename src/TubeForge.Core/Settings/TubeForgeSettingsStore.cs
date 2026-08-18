using System.Text.Json;
using TubeForge.Core.Errors;
using TubeForge.Core.Files;
using TubeForge.Core.Networking;
using TubeForge.Core.Results;

namespace TubeForge.Core.Settings;

public sealed class TubeForgeSettingsStore
{
    private const long MaximumFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public TubeForgeSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
    }

    public string StoragePath => _path;

    public async Task<Result<TubeForgeSettings>> LoadAsync(
        TubeForgeSettings defaults,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var defaultError = Validate(defaults);
        if (defaultError is not null)
        {
            return Result<TubeForgeSettings>.Failure(defaultError);
        }

        var lockTaken = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            if (!File.Exists(_path))
            {
                return Result<TubeForgeSettings>.Success(defaults);
            }

            if (new FileInfo(_path).Length > MaximumFileBytes)
            {
                return Corrupt(_path);
            }

            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var settings = await JsonSerializer.DeserializeAsync<TubeForgeSettings>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            if (settings is null)
            {
                return Corrupt(_path);
            }

            settings = Migrate(settings);
            var validation = Validate(settings);
            if (validation is null)
            {
                return Result<TubeForgeSettings>.Success(settings);
            }

            // The file is readable but this build cannot use it, which happens after a downgrade.
            // Preserve it so the first save from defaults does not discard the user's real
            // settings for good.
            PreserveUnusableFile(_path);
            return Result<TubeForgeSettings>.Failure(validation);
        }
        catch (OperationCanceledException)
        {
            return Failure("Operation.Cancelled", "The settings operation was cancelled.");
        }
        catch (JsonException)
        {
            return Corrupt(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(
                "Settings.ReadFailed",
                "TubeForge cannot read local settings.",
                exception.GetType().Name);
        }
        finally
        {
            if (lockTaken)
            {
                _gate.Release();
            }
        }
    }

    public async Task<Result<bool>> SaveAsync(
        TubeForgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validation = Validate(settings);
        if (validation is not null)
        {
            return Result<bool>.Failure(validation);
        }

        var lockTaken = false;
        var temporaryPath = _path + ".new";
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return Failure<bool>("Settings.InvalidPath", "The settings storage path is invalid.");
            }

            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 8 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            return Result<bool>.Success(true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            return Failure<bool>("Operation.Cancelled", "The settings operation was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporaryPath);
            return Failure<bool>(
                "Settings.WriteFailed",
                "TubeForge cannot save local settings.",
                exception.GetType().Name);
        }
        finally
        {
            if (lockTaken)
            {
                _gate.Release();
            }
        }
    }

    private static TubeForgeError? Validate(TubeForgeSettings settings)
    {
        if (settings.SchemaVersion != TubeForgeSettings.CurrentSchemaVersion)
        {
            return new TubeForgeError(
                "Settings.UnsupportedSchema",
                "The local settings use an unsupported schema version.");
        }

        if (settings.MaximumConcurrentDownloads is < 1 or > 4 ||
            settings.PerHostConcurrency is < 1 or > 4 ||
            settings.MetadataTimeoutSeconds is < 5 or > 120 ||
            settings.DownloadRetryAttempts is < 1 or > 5 ||
            !Enum.IsDefined(settings.ProxyMode) ||
            settings.ProxyMode == NetworkProxyMode.Manual &&
                !NetworkProxyPolicy.TryParseManualUri(settings.ManualProxyUri, out _) ||
            settings.ProxyMode != NetworkProxyMode.Manual &&
                !string.IsNullOrEmpty(settings.ManualProxyUri) ||
            !Enum.IsDefined(settings.LibrarySortOrder) ||
            !Enum.IsDefined(settings.DefaultDownloadPreset) ||
            !FileNameTemplate.IsValid(settings.FileNameTemplate) ||
            string.IsNullOrWhiteSpace(settings.DownloadFolder) ||
            settings.DownloadFolder.Length > 32_767 ||
            settings.DownloadFolder.Any(char.IsControl))
        {
            return InvalidState();
        }

        try
        {
            if (!System.IO.Path.IsPathFullyQualified(settings.DownloadFolder) ||
                System.IO.Path.GetFullPath(settings.DownloadFolder) != settings.DownloadFolder)
            {
                return InvalidState();
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return InvalidState();
        }

        return null;
    }

    private static TubeForgeSettings Migrate(TubeForgeSettings settings)
    {
        var migrated = settings.SchemaVersion switch
        {
            1 => settings with
            {
                SchemaVersion = TubeForgeSettings.CurrentSchemaVersion,
                LibrarySortOrder = LibrarySortOrder.NewestFirst,
                EnableAcceleratedTransfers = true
            },
            2 => settings with
            {
                SchemaVersion = TubeForgeSettings.CurrentSchemaVersion,
                EnableAcceleratedTransfers = true
            },
            3 => settings with
            {
                SchemaVersion = TubeForgeSettings.CurrentSchemaVersion,
                ProxyMode = NetworkProxyMode.System,
                ManualProxyUri = string.Empty,
                MetadataTimeoutSeconds = 20,
                DownloadRetryAttempts = 3,
                PerHostConcurrency = 2
            },
            4 => settings with
            {
                SchemaVersion = TubeForgeSettings.CurrentSchemaVersion,
                DefaultDownloadPreset = PreferredDownloadPreset.BestOriginal,
                ShowAdvancedDownloadOptions = false
            },
            5 => settings with
            {
                SchemaVersion = TubeForgeSettings.CurrentSchemaVersion
            },
            _ => settings
        };

        if (settings.SchemaVersion is < 1 or >= TubeForgeSettings.CurrentSchemaVersion)
        {
            return migrated;
        }

        var fileNameTemplate = FileNameTemplate.MigrateLegacyOutputTokens(
            migrated.FileNameTemplate,
            out var includeQuality);
        return migrated with
        {
            FileNameTemplate = fileNameTemplate,
            IncludeQualityInFileName = includeQuality
        };
    }

    /// <summary>
    /// Copies a settings file this build cannot read to a one-time sidecar. Without it the next
    /// successful save silently overwrites the user's configuration with defaults.
    /// </summary>
    private static void PreserveUnusableFile(string path)
    {
        try
        {
            var backup = path + ".unreadable";
            if (File.Exists(path) && !File.Exists(backup))
            {
                File.Copy(path, backup);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            // Best effort: failing to preserve must not turn a recoverable state into an error.
        }
    }

    private static TubeForgeError InvalidState() => new(
        "Settings.InvalidState",
        "One or more local settings are invalid.");

    private static Result<TubeForgeSettings> Corrupt(string path)
    {
        PreserveUnusableFile(path);
        return CorruptResult();
    }

    private static Result<TubeForgeSettings> CorruptResult() =>
        Failure("Settings.Corrupt", "The local settings file is malformed and was left unchanged.");

    private static Result<TubeForgeSettings> Failure(string code, string message, string? detail = null) =>
        Result<TubeForgeSettings>.Failure(new TubeForgeError(code, message, detail));

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
