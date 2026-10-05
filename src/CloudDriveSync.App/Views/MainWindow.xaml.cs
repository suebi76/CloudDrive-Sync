using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CloudDriveSync.App.ViewModels;

namespace CloudDriveSync.App.Views;

/// <summary>The main window. Closing it only hides it - CloudDrive-Sync keeps synchronising in the background.</summary>
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

    /// <summary>The "…" buttons open their menu below themselves.</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
