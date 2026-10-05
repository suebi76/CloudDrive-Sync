using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.App.ViewModels;

public enum DialogChoice
{
    None,
    Primary,
    Secondary,
}

/// <summary>The dialogs the view models open; the views decide how they look.</summary>
public interface IDialogs
{
    /// <summary>Adds an account. Returns it, and whether the user wants to synchronise a folder of it next.</summary>
    (AccountSettings? Account, bool SyncNext) AddAccount();

    bool Relogin(AccountSettings account);

    SyncPairSettings? AddSync(string? accountId);

    bool EditSync(SyncPairSettings pair);

    DialogChoice Ask(string title, string text, string primary, string? secondary = null, string close = "Abbrechen", bool danger = false);
}
