using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace TubeForge.App.Diagnostics;

internal sealed class DesktopPerformanceProbe
{
    public const double StartupTargetMilliseconds = 2_000;
    public const double StartupBudgetMilliseconds = 4_000;
    public const double IdleCpuBudgetPercent = 5;
    public const double WorkingSetBudgetMebibytes = 256;
    public const double UiFrameP95TargetMilliseconds = 34;
    public const double UiFrameP95BudgetMilliseconds = 50;
    public const double LongFrameBudgetPercent = 5;
    private static readonly TimeSpan WarmupDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FrameCaptureDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleCpuCaptureDuration = TimeSpan.FromSeconds(3);
    private readonly string _reportPath;
    private readonly Dictionary<string, double> _phases = [];
    private readonly Stopwatch _phaseClock = Stopwatch.StartNew();

    private DesktopPerformanceProbe(string reportPath)
    {
        _reportPath = reportPath;
        ApplicationDataDirectory = reportPath + ".data";
    }

    public string ApplicationDataDirectory { get; }

    /// <summary>
    /// Records the elapsed time from probe creation to a named startup milestone. Without a
    /// breakdown a slow launch only reports one number, and every diagnosis starts from scratch.
    /// </summary>
    public void MarkPhase(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _phases[name] = _phaseClock.Elapsed.TotalMilliseconds;
    }

    public static DesktopPerformanceProbe? TryCreate(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 2 ||
            !arguments[0].Equals("--performance-report", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(arguments[1]))
        {
            return null;
        }

        return new DesktopPerformanceProbe(Path.GetFullPath(arguments[1]));
    }

    public async Task CaptureAsync()
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var startupMilliseconds = Math.Max(0, (DateTime.Now - process.StartTime).TotalMilliseconds);
        await Task.Delay(WarmupDuration);
        var frameIntervals = new List<double>(240);
        TimeSpan? previousRenderingTime = null;

        EventHandler handler = (_, eventArgs) =>
        {
            if (eventArgs is not RenderingEventArgs rendering)
            {
                return;
            }

            if (previousRenderingTime is not null)
            {
                var interval = (rendering.RenderingTime - previousRenderingTime.Value).TotalMilliseconds;
                if (interval is > 0 and < 1_000)
                {
                    frameIntervals.Add(interval);
                }
            }

            previousRenderingTime = rendering.RenderingTime;
        };
        CompositionTarget.Rendering += handler;
        await Task.Delay(FrameCaptureDuration);
        CompositionTarget.Rendering -= handler;

        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var wallClock = Stopwatch.StartNew();
        await Task.Delay(IdleCpuCaptureDuration);
        wallClock.Stop();
        process.Refresh();
        var cpuMilliseconds = Math.Max(0, (process.TotalProcessorTime - cpuStart).TotalMilliseconds);
        var idleCpuPercent = wallClock.Elapsed.TotalMilliseconds > 0
            ? cpuMilliseconds / wallClock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100
            : 0;
        var workingSetMebibytes = process.WorkingSet64 / (1024d * 1024d);
        var frameP95 = frameIntervals.Count == 0 ? 1_000 : Percentile(frameIntervals, 0.95);
        var longFramePercent = frameIntervals.Count == 0
            ? 100
            : frameIntervals.Count(interval => interval > UiFrameP95BudgetMilliseconds) * 100d / frameIntervals.Count;
        var passed = startupMilliseconds <= StartupBudgetMilliseconds &&
                     idleCpuPercent <= IdleCpuBudgetPercent &&
                     workingSetMebibytes <= WorkingSetBudgetMebibytes &&
                     frameIntervals.Count >= 30 &&
                     frameP95 <= UiFrameP95BudgetMilliseconds &&
                     longFramePercent <= LongFrameBudgetPercent;
        var report = new
        {
            schemaVersion = 1,
            generatedAtUtc = DateTimeOffset.UtcNow,
            applicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "development",
            metrics = new
            {
                startupMilliseconds,
                idleCpuPercent,
                workingSetMebibytes,
                uiFrameP95Milliseconds = frameP95,
                uiLongFramePercent = longFramePercent,
                uiFrameSamples = frameIntervals.Count,
                startupPhaseMilliseconds = _phases
            },
            budgets = new
            {
                startupMilliseconds = StartupBudgetMilliseconds,
                startupTargetMilliseconds = StartupTargetMilliseconds,
                idleCpuPercent = IdleCpuBudgetPercent,
                workingSetMebibytes = WorkingSetBudgetMebibytes,
                uiFrameP95Milliseconds = UiFrameP95BudgetMilliseconds,
                uiFrameP95TargetMilliseconds = UiFrameP95TargetMilliseconds,
                uiLongFramePercent = LongFrameBudgetPercent,
                minimumUiFrameSamples = 30
            },
            passed
        };
        await WriteReportAsync(report);
        Application.Current.Shutdown(passed ? 0 : 1);
    }

    private async Task WriteReportAsync(object report)
    {
        var directory = Path.GetDirectoryName(_reportPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The performance report path is invalid.");
        }

        Directory.CreateDirectory(directory);
        var pendingPath = _reportPath + ".new";
        await using (var stream = new FileStream(
                         pendingPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true });
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
        }

        File.Move(pendingPath, _reportPath, overwrite: true);
    }

    private static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return double.PositiveInfinity;
        }

        var ordered = values.Order().ToArray();
        var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }
}
