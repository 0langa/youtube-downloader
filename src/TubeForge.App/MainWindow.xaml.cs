using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using TubeForge.App.Diagnostics;
using TubeForge.App.ViewModels;

namespace TubeForge.App;

public partial class MainWindow : Window
{
    private readonly DesktopPerformanceProbe? _performanceProbe;
    private readonly MainViewModel _viewModel;
    private Version? _promptedUpdateVersion;

    public MainWindow() : this(performanceProbe: null)
    {
    }

    internal MainWindow(DesktopPerformanceProbe? performanceProbe)
    {
        _performanceProbe = performanceProbe;
        _viewModel = new MainViewModel(performanceProbe?.ApplicationDataDirectory);
        _performanceProbe?.MarkPhase("viewModelConstructed");
        InitializeComponent();
        _performanceProbe?.MarkPhase("xamlLoaded");
        DataContext = _viewModel;
        _viewModel.UpdateAvailable += ViewModel_OnUpdateAvailable;
        _viewModel.UpdateInstallerStarted += ViewModel_OnUpdateInstallerStarted;
        Loaded += MainWindow_OnLoaded;
        Closed += MainWindow_OnClosed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var enabled = 1;
        var handle = new WindowInteropHelper(this).Handle;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        _performanceProbe?.MarkPhase("windowLoaded");
        await _viewModel.InitializeAsync();
        _performanceProbe?.MarkPhase("stateLoaded");
        if (_viewModel.ShowResponsibleUseNotice)
        {
            ResponsibleUseAcceptButton.Focus();
        }
        else
        {
            UrlTextBox.Focus();
        }

        if (_performanceProbe is not null)
        {
            await _performanceProbe.CaptureAsync();
        }
    }

    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose download folder",
            InitialDirectory = Directory.Exists(_viewModel.DownloadFolder)
                ? _viewModel.DownloadFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.DownloadFolder = dialog.FolderName;
        }
    }

    private void UrlTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.AnalyzeCommand.CanExecute(null))
        {
            _viewModel.AnalyzeCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void CopyDiagnosticsButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.BuildDiagnosticReport());
            _viewModel.SetDiagnosticsStatus("Redacted report copied to clipboard.");
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
        {
            _viewModel.SetDiagnosticsStatus($"Clipboard unavailable: {exception.GetType().Name}");
        }
    }

    private async void ExportDiagnosticsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export redacted TubeForge diagnostics",
            FileName = $"tubeforge-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json",
            DefaultExt = ".json",
            Filter = "JSON report (*.json)|*.json",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(
                dialog.FileName,
                _viewModel.BuildDiagnosticReport(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _viewModel.SetDiagnosticsStatus("Redacted report exported.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            _viewModel.SetDiagnosticsStatus($"Export failed: {exception.GetType().Name}");
        }
    }

    private async void ExportLibraryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export TubeForge Library history",
            FileName = $"tubeforge-library-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json",
            DefaultExt = ".json",
            Filter = "TubeForge Library (*.json)|*.json",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.ExportLibraryAsync(dialog.FileName);
        }
    }

    private async void ImportLibraryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import TubeForge Library history",
            DefaultExt = ".json",
            Filter = "TubeForge Library (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var answer = MessageBox.Show(
            "Import this Library export?\n\nRecords are merged with your current history. Existing records and downloaded media are not deleted.",
            "Import TubeForge Library",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            await _viewModel.ImportLibraryAsync(dialog.FileName);
        }
    }

    private async void RescanLibraryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose folder containing moved downloads",
            InitialDirectory = Directory.Exists(_viewModel.DownloadFolder)
                ? _viewModel.DownloadFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.RescanLibraryAsync(dialog.FolderName);
        }
    }

    private void ViewModel_OnUpdateAvailable(object? sender, UpdateAvailableEventArgs e)
    {
        if (_promptedUpdateVersion is not null && _promptedUpdateVersion >= e.Version)
        {
            return;
        }

        _promptedUpdateVersion = e.Version;
        var dialog = new UpdateAvailableWindow(_viewModel, e.Version)
        {
            Owner = this
        };
        _ = dialog.ShowDialog();
    }

    private static void ViewModel_OnUpdateInstallerStarted(object? sender, EventArgs e) =>
        Application.Current.Shutdown();

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _viewModel.UpdateAvailable -= ViewModel_OnUpdateAvailable;
        _viewModel.UpdateInstallerStarted -= ViewModel_OnUpdateInstallerStarted;
        _viewModel.Dispose();
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
