using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

/// <summary>One account as a card: who is signed in where, storage used, synchronisations.</summary>
public sealed partial class AccountViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private AccountSettings _account;

    public AccountViewModel(MainViewModel main, AccountSettings account)
    {
        _main = main;
        _account = account;
        Update(account, 0);
    }

    public string Id => _account.Id;
    public AccountSettings Settings => _account;

    [ObservableProperty] public partial string Label { get; set; } = "";
    [ObservableProperty] public partial string Glyph { get; set; } = Glyphs.Cloud;
    [ObservableProperty] public partial string Details { get; set; } = "";
    [ObservableProperty] public partial string SyncText { get; set; } = "";
    [ObservableProperty] public partial bool HasQuota { get; set; }
    [ObservableProperty] public partial double QuotaPercent { get; set; }
    [ObservableProperty] public partial string QuotaText { get; set; } = "";
    [ObservableProperty] public partial int SyncCount { get; set; }

    public void Update(AccountSettings account, int syncCount)
    {
        _account = account;
        Label = account.Label;
        Glyph = Glyphs.Of(account.Kind);
        Details = $"{Glyphs.NameOf(account.Kind)} · {account.Identity?.Name ?? ""}";
        SyncCount = syncCount;
        SyncText = syncCount == 0 ? "Noch kein Ordner synchronisiert" : $"{Format.Count(syncCount, "Ordner", "Ordner")} synchronisiert";
    }

    public void ShowQuota(Quota quota)
    {
        if (quota.Used is not { } used)
        {
            HasQuota = false;
            return;
        }
        var total = quota.Total ?? (quota.Free is { } free ? used + free : null);
        HasQuota = true;
        QuotaPercent = total is > 0 ? Math.Clamp(100.0 * used / total.Value, 0, 100) : 0;
        QuotaText = total is > 0 ? $"{Format.Bytes(used)} von {Format.Bytes(total.Value)} belegt" : $"{Format.Bytes(used)} belegt";
    }

    [RelayCommand]
    private void AddSync() => _main.AddSyncFor(Id);

    [RelayCommand]
    private void Relogin() => _main.Relogin(Id);

    [RelayCommand]
    private Task RemoveAsync() => _main.RemoveAccountAsync(this);
}
