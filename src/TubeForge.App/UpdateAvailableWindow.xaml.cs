using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TubeForge.App.ViewModels;

namespace TubeForge.App;

public partial class UpdateAvailableWindow : Window
{
    private readonly MainViewModel _viewModel;

    public UpdateAvailableWindow(MainViewModel viewModel, Version version)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ArgumentNullException.ThrowIfNull(version);
        InitializeComponent();
        DataContext = viewModel;
        VersionText.Text = viewModel.AvailableUpdateSummary;
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

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_viewModel.CanDismissUpdatePrompt)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private void LaterButton_OnClick(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
