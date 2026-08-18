using System.Diagnostics;
using System.Text.Json;
using TubeForge.YouTube.Extraction;

const double AnalysisP95BudgetMilliseconds = 25;
const int AnalysisIterations = 300;
var repositoryRoot = FindRepositoryRoot();
var coreReport = MeasureAnalysis(repositoryRoot);
JsonElement? desktopReport = null;

if (!args.Contains("--core-only", StringComparer.OrdinalIgnoreCase))
{
    desktopReport = await MeasureDesktopAsync(repositoryRoot);
}

var desktopPassed = desktopReport is null || desktopReport.Value.GetProperty("passed").GetBoolean();
var combined = new
{
    schemaVersion = 1,
    generatedAtUtc = DateTimeOffset.UtcNow,
    core = coreReport,
    desktop = desktopReport,
    passed = coreReport.Passed && desktopPassed
};
Console.WriteLine(JsonSerializer.Serialize(combined, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
}));
return combined.passed ? 0 : 1;

static AnalysisPerformanceReport MeasureAnalysis(string repositoryRoot)
{
    var fixturePath = Path.Combine(
        repositoryRoot,
        "tests",
        "TubeForge.Tests",
        "Fixtures",
        "watch-page-basic.html");
    var html = File.ReadAllText(fixturePath);
    for (var iteration = 0; iteration < 25; iteration++)
    {
        EnsureParseSucceeded(html);
    }

    var samples = new double[AnalysisIterations];
    for (var iteration = 0; iteration < samples.Length; iteration++)
    {
        var started = Stopwatch.GetTimestamp();
        EnsureParseSucceeded(html);
        samples[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    Array.Sort(samples);
    var p50 = Percentile(samples, 0.50);
    var p95 = Percentile(samples, 0.95);
    return new AnalysisPerformanceReport(
        Iterations: samples.Length,
        P50Milliseconds: p50,
        P95Milliseconds: p95,
        BudgetP95Milliseconds: AnalysisP95BudgetMilliseconds,
        Passed: p95 <= AnalysisP95BudgetMilliseconds);
}

static async Task<JsonElement> MeasureDesktopAsync(string repositoryRoot)
{
    var buildDirectory = Path.Combine(
        repositoryRoot,
        "src",
        "TubeForge.App",
        "bin",
        "Release",
        "net10.0-windows");
    // Measure the application host rather than "dotnet TubeForge.dll": the host is what users
    // launch, and routing through the muxer adds startup cost that never ships.
    var appPath = Path.Combine(buildDirectory, "TubeForge.exe");
    var usesApplicationHost = File.Exists(appPath);
    if (!usesApplicationHost)
    {
        appPath = Path.Combine(buildDirectory, "TubeForge.dll");
    }

    if (!File.Exists(appPath))
    {
        throw new FileNotFoundException(
            "Build TubeForge.App in Release configuration before running the desktop probe.",
            appPath);
    }

    var safeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TubeForge.Performance"));
    var workingDirectory = Path.Combine(safeRoot, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workingDirectory);
    var reportPath = Path.Combine(workingDirectory, "desktop-report.json");
    try
    {
        var startInfo = new ProcessStartInfo(usesApplicationHost ? appPath : "dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        if (!usesApplicationHost)
        {
            startInfo.ArgumentList.Add(appPath);
        }

        startInfo.ArgumentList.Add("--performance-report");
        startInfo.ArgumentList.Add(reportPath);
        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("The desktop performance probe did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("The desktop performance probe exceeded 20 seconds.");
        }

        if (!File.Exists(reportPath))
        {
            throw new InvalidOperationException(
                $"The desktop probe exited with code {process.ExitCode} without a report.");
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        return document.RootElement.Clone();
    }
    finally
    {
        var resolved = Path.GetFullPath(workingDirectory);
        if (!resolved.StartsWith(safeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to clean a performance directory outside the safe root.");
        }

        if (Directory.Exists(resolved))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }
}

static void EnsureParseSucceeded(string html)
{
    var result = YouTubeWatchPageParser.Parse(html);
    if (!result.IsSuccess)
    {
        throw new InvalidOperationException(result.Error!.Message);
    }
}

static double Percentile(IReadOnlyList<double> orderedValues, double percentile)
{
    var index = Math.Clamp(
        (int)Math.Ceiling(percentile * orderedValues.Count) - 1,
        0,
        orderedValues.Count - 1);
    return orderedValues[index];
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "TubeForge.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Run this tool from the TubeForge repository.");
}

internal sealed record AnalysisPerformanceReport(
    int Iterations,
    double P50Milliseconds,
    double P95Milliseconds,
    double BudgetP95Milliseconds,
    bool Passed);
