using System.Windows;
using CloudDriveSync.App.ViewModels;

namespace CloudDriveSync.App.Views;

/// <summary>"Abgleich überprüfen" for one synchronisation.</summary>
public partial class VerifyWindow : Window
{
    public VerifyWindow(VerifyViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        Closed += (_, _) => viewModel.Cancel();
    }
}
