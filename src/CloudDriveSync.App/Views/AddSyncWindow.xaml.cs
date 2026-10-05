using System.IO;
using System.Windows;
using CloudDriveSync.App.ViewModels;
using Microsoft.Win32;

namespace CloudDriveSync.App.Views;

/// <summary>Setting up a synchronisation step by step.</summary>
public partial class AddSyncWindow : Window
{
    private readonly AddSyncViewModel _viewModel;

    public AddSyncWindow(AddSyncViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }

    private void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode { IsPlaceholder: false } node) _viewModel.SelectedFolder = node;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Speicherort auf diesem PC wählen" };
        var current = _viewModel.LocalPath.Trim();
        var existing = current;
        while (existing.Length > 0 && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing) ?? "";
        if (existing.Length > 0) dialog.InitialDirectory = existing;
        if (dialog.ShowDialog(this) == true) _viewModel.ChooseLocalFolder(dialog.FolderName);
    }
}
