using System.Windows;
using System.Windows.Threading;
using TubeForge.App.Diagnostics;

namespace TubeForge.App;

public partial class App : Application
{
    private UnhandledErrorReporter? _errorReporter;
    private bool _isShowingFailureNotice;

    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        var probe = DesktopPerformanceProbe.TryCreate(e.Args);
        _errorReporter = new UnhandledErrorReporter(probe?.ApplicationDataDirectory);
        DispatcherUnhandledException += App_OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += App_OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += App_OnUnobservedTaskException;
        var window = new MainWindow(probe);
        MainWindow = window;
        window.Show();
        probe?.MarkPhase("windowShown");
    }

    /// <summary>
    /// Keeps the process alive after a failure that reached the dispatcher. Losing an in-flight
    /// action is recoverable; losing the window, the queue state and every active transfer is not.
    /// </summary>
    private void App_OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = _errorReporter?.Report("DispatcherUnhandledException", e.Exception);
        e.Handled = true;
        ShowFailureNotice(e.Exception, logPath);
    }

    private void App_OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _errorReporter?.Report("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private void App_OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _errorReporter?.Report(
                e.IsTerminating ? "UnhandledException (terminating)" : "UnhandledException",
                exception);
        }
    }

    private void ShowFailureNotice(Exception exception, string? logPath)
    {
        if (_isShowingFailureNotice)
        {
            return;
        }

        _isShowingFailureNotice = true;
        try
        {
            var location = logPath is null
                ? "TubeForge could not write a local error log."
                : $"Details were written to:\n{logPath}";
            MessageBox.Show(
                MainWindow,
                "TubeForge hit an unexpected problem and stopped that action.\n\n" +
                $"{exception.GetType().Name}: {exception.Message}\n\n" +
                $"{location}\n\n" +
                "The app is still running. Downloads already written to disk are unaffected.",
                "TubeForge",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException)
        {
            // A window-less or shutting-down app cannot show the notice; the log already has it.
        }
        finally
        {
            _isShowingFailureNotice = false;
        }
    }
}
