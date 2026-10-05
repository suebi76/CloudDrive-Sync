using System.Collections.ObjectModel;
using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

/// <summary>One account as a card: who is signed in where, storage used, and its synchronisations.</summary>
public sealed partial class AccountViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private AccountSettings _account;

    public AccountViewModel(MainViewModel main, AccountSettings account)
    {
        _main = main;
        _account = account;
        Update(account, []);
    }

    public string Id => _account.Id;
    public AccountSettings Settings => _account;

    [ObservableProperty] public partial string Label { get; set; } = "";
    [ObservableProperty] public partial string Glyph { get; set; } = Glyphs.Cloud;
    [ObservableProperty] public partial string Details { get; set; } = "";
    [ObservableProperty] public partial bool HasQuota { get; set; }
    [ObservableProperty] public partial double QuotaPercent { get; set; }
    [ObservableProperty] public partial string QuotaText { get; set; } = "";
    [ObservableProperty] public partial bool HasSyncs { get; set; }
    [ObservableProperty] public partial string AddSyncText { get; set; } = "Ordner synchronisieren …";

    /// <summary>The synchronisations of this account - the same ones the overview shows as cards.</summary>
    public ObservableCollection<SyncPairViewModel> Syncs { get; } = [];

    public void Update(AccountSettings account, IReadOnlyList<SyncPairViewModel> syncs)
    {
        _account = account;
        Label = account.Label;
        Glyph = Glyphs.Of(account.Kind);
        Details = $"{Glyphs.NameOf(account.Kind)} · {account.Identity?.Name ?? ""}";
        if (!Syncs.SequenceEqual(syncs))
        {
            Syncs.Clear();
            foreach (var sync in syncs) Syncs.Add(sync);
        }
        HasSyncs = Syncs.Count > 0;
        AddSyncText = HasSyncs ? "Weiteren Ordner synchronisieren …" : "Ordner synchronisieren …";
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
    private void ShowTrash() => _main.ShowTrash();

    [RelayCommand]
    private Task RemoveAsync() => _main.RemoveAccountAsync(this);
}
