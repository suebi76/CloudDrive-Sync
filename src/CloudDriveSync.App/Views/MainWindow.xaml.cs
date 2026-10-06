using System.ComponentModel;
using System.Windows;
using CloudDriveSync.App.ViewModels;

namespace CloudDriveSync.App.Views;

/// <summary>
/// The main window: the start screen, the navigation and the pages (Views\Pages). Closing it only hides it -
/// CloudDrive-Sync keeps synchronising in the background.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>Set when CloudDrive-Sync ends; then the window really closes.</summary>
    public bool AllowClose { get; set; }

    public event EventHandler? HiddenToTray;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }
        base.OnClosing(e);
    }
}
