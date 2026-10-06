using System.Windows;
using CloudDriveSync.App.ViewModels;
using CloudDriveSync.Core;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.App.Views;

/// <summary>Opens CloudDrive-Sync's dialogs above the main window.</summary>
internal sealed class DialogService(CloudDriveSyncHost host, Func<Window?> owner) : IDialogs
{
    public (AccountSettings? Account, bool SyncNext) AddAccount()
    {
        using var viewModel = new AddAccountViewModel(host);
        Show(new AddAccountWindow(viewModel));
        return (viewModel.Result, viewModel.Result is not null && viewModel.SyncNext);
    }

    public bool Relogin(AccountSettings account)
    {
        using var viewModel = new AddAccountViewModel(host, account);
        Show(new AddAccountWindow(viewModel));
        return viewModel.Step == AddAccountStep.Done;
    }

    public SyncPairSettings? AddSync(string? accountId)
    {
        var viewModel = new AddSyncViewModel(host, accountId);
        Show(new AddSyncWindow(viewModel));
        return viewModel.Result;
    }

    public bool SyncSettings(SyncPairSettings pair)
    {
        var viewModel = new SyncSettingsViewModel(host, pair);
        Show(new SyncSettingsWindow(viewModel));
        return viewModel.Saved;
    }

    public void Verify(SyncPairSettings pair, string title) => Show(new VerifyWindow(new VerifyViewModel(host, pair, title)));

    public DialogChoice Ask(string title, string text, string primary, string? secondary = null, string close = "Abbrechen", bool danger = false) =>
        MessageDialog.Show(owner(), title, text, primary, secondary, close, danger);

    private void Show(Window window)
    {
        if (owner() is { } main) window.Owner = main;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }
}
