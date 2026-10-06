using System.Windows;
using CloudDriveSync.App.ViewModels;

namespace CloudDriveSync.App.Views;

/// <summary>The settings of an existing synchronisation on one page.</summary>
public partial class SyncSettingsWindow : Window
{
    public SyncSettingsWindow(SyncSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        // Questions appear over this window.
        viewModel.Confirm = (title, text, primary) => MessageDialog.Show(this, title, text, primary, null, "Abbrechen", false) == DialogChoice.Primary;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }
}
