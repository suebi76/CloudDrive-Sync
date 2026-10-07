using System.Windows;
using CloudDriveSync.App.ViewModels;

namespace CloudDriveSync.App.Views;

/// <summary>Ending a synchronisation or removing an account: what stays on this PC, then the work.</summary>
public partial class EndSyncWindow : Window
{
    public EndSyncWindow(EndSyncViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        // While the work runs the window stays; it closes by itself when nothing is left to say.
        Closing += (_, e) => e.Cancel = viewModel.IsWorking;
    }
}
