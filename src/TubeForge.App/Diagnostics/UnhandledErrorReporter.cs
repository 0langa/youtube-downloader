using System.IO;
using System.Text;

namespace TubeForge.App.Diagnostics;

/// <summary>
/// Records unhandled failures to a bounded local log so a crash leaves actionable evidence.
/// The log stays on the local machine: it records exception types, messages and stack traces
/// only, and never media URLs, video identifiers, titles, channels, headers or signatures.
/// </summary>
internal sealed class UnhandledErrorReporter
{
    private const int MaximumLogBytes = 512 * 1024;
    private const int RetainedLogs = 5;
    private readonly object _gate = new();
    private readonly string _directory;

    public UnhandledErrorReporter(string? applicationDataDirectory)
    {
        applicationDataDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TubeForge");
        _directory = Path.Combine(Path.GetFullPath(applicationDataDirectory), "logs");
    }

    public string Directory => _directory;

    /// <summary>
    /// Appends one redacted entry and returns the log path, or null when the log is unavailable.
    /// Never throws: reporting a failure must not create a second failure.
    /// </summary>
    public string? Report(string origin, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        if (exception is null)
        {
            return null;
        }

        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(_directory);
                var path = Path.Combine(
                    _directory,
                    $"errors-{DateTimeOffset.UtcNow:yyyyMMdd}.log");
                RollIfOversized(path);
                File.AppendAllText(path, Compose(origin, exception), new UTF8Encoding(false));
                PruneOldLogs();
                return path;
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or
                                        NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static string Compose(string origin, Exception exception)
    {
        var builder = new StringBuilder(1024);
        builder.Append("---- ").Append(DateTimeOffset.UtcNow.ToString("O")).Append(' ').AppendLine(origin);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            builder.Append(current.GetType().FullName).Append(": ").AppendLine(current.Message);
            if (!string.IsNullOrEmpty(current.StackTrace))
            {
                builder.AppendLine(current.StackTrace);
            }

            if (current.InnerException is not null)
            {
                builder.AppendLine("-- caused by --");
            }
        }

        builder.AppendLine();
        return builder.ToString();
    }

    private static void RollIfOversized(string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length > MaximumLogBytes)
        {
            File.Move(path, path + $".{DateTimeOffset.UtcNow:HHmmss}.old", overwrite: true);
        }
    }

    private void PruneOldLogs()
    {
        var files = new DirectoryInfo(_directory)
            .GetFiles("errors-*")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(RetainedLogs)
            .ToArray();
        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A locked log is not worth failing the report for.
            }
        }
    }
}
