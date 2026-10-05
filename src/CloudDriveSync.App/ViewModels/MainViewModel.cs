using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

public enum Page
{
    Overview,
    Accounts,
    Activity,
    Trash,
    Settings,
}

public enum StartupState
{
    Starting,
    Ready,
    Failed,
}

/// <summary>An entry of the navigation on the left, with a counter (e.g. open decisions).</summary>
public sealed partial class NavItem(Page page, string title, string glyph) : ObservableObject
{
    public Page Page { get; } = page;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;

    [ObservableProperty] public partial int Badge { get; set; }

    /// <summary>Also the name screen readers announce.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// The main window: starts CloudDrive-Sync, shows the synchronisations, accounts, activity and settings, and keeps them up
/// to date. Everything that happens in the background arrives here and is moved to the window's thread.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clock;
    private readonly Dictionary<string, DateTimeOffset?> _lastRuns = new(StringComparer.OrdinalIgnoreCase);
    private bool _subscribed;
    private DateTime _lastQuota = DateTime.MinValue;

    public MainViewModel(CloudDriveSyncHost host, IDialogs dialogs, Dispatcher dispatcher)
    {
        Host = host;
        Dialogs = dialogs;
        _dispatcher = dispatcher;
        Navigation =
        [
            new NavItem(Page.Overview, "Übersicht", Glyphs.Home),
            new NavItem(Page.Accounts, "Konten", Glyphs.Accounts),
            new NavItem(Page.Activity, "Aktivität", Glyphs.History),
            new NavItem(Page.Trash, "Papierkorb", Glyphs.Trash),
            new NavItem(Page.Settings, "Einstellungen", Glyphs.Settings),
        ];
        SelectedNav = Navigation[0];
        Settings = new SettingsViewModel(this);
        Trash = new TrashViewModel(this);
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
    }

    public CloudDriveSyncHost Host { get; }
    public IDialogs Dialogs { get; }
    public SettingsViewModel Settings { get; }
    public TrashViewModel Trash { get; }
    public IReadOnlyList<NavItem> Navigation { get; }

    public ObservableCollection<SyncPairViewModel> Pairs { get; } = [];
    public ObservableCollection<AccountViewModel> Accounts { get; } = [];
    public ObservableCollection<ActivityItem> Activity { get; } = [];
    public ObservableCollection<ConflictItem> Conflicts { get; } = [];

    [ObservableProperty] public partial NavItem SelectedNav { get; set; }
    [ObservableProperty] public partial StartupState Startup { get; set; } = StartupState.Starting;
    [ObservableProperty] public partial string StartupText { get; set; } = "CloudDrive-Sync startet …";
    [ObservableProperty] public partial string StartupErrorTitle { get; set; } = "";
    [ObservableProperty] public partial string StartupErrorText { get; set; } = "";
    [ObservableProperty] public partial bool CanResetConfiguration { get; set; }
    [ObservableProperty] public partial Tone SummaryTone { get; set; } = Tone.Neutral;
    [ObservableProperty] public partial string SummaryGlyph { get; set; } = Glyphs.Cloud;
    [ObservableProperty] public partial string SummaryTitle { get; set; } = "";
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial bool HasAccounts { get; set; }
    [ObservableProperty] public partial bool HasPairs { get; set; }
    [ObservableProperty] public partial bool AllPaused { get; set; }

    public Page CurrentPage => SelectedNav.Page;
    public bool ShowWelcome => !HasAccounts;
    public bool ShowNoPairs => HasAccounts && !HasPairs;

    /// <summary>For Windows notifications (only when the user wants them).</summary>
    public event EventHandler<SyncNotice>? Notice;

    /// <summary>The summary changed (for the symbol in the notification area).</summary>
    public event EventHandler? SummaryChanged;

    partial void OnSelectedNavChanged(NavItem value)
    {
        OnPropertyChanged(nameof(CurrentPage));
        if (value.Page == Page.Trash) Trash.Refresh();
    }

    partial void OnHasAccountsChanged(bool value) => NotifyEmptyStates();

    partial void OnHasPairsChanged(bool value) => NotifyEmptyStates();

    private void NotifyEmptyStates()
    {
        OnPropertyChanged(nameof(ShowWelcome));
        OnPropertyChanged(nameof(ShowNoPairs));
    }

    public async Task StartAsync()
    {
        Startup = StartupState.Starting;
        StartupText = "CloudDrive-Sync startet …";
        try
        {
            await Host.StartAsync(new Progress<string>(text => StartupText = text));
            if (!_subscribed)
            {
                Host.Sync.StateChanged += (_, state) => _dispatcher.InvokeAsync(() => OnStateChanged(state));
                Host.Sync.Notice += (_, notice) => _dispatcher.InvokeAsync(() =>
                {
                    if (Host.Settings.Current.Preferences.Notifications) Notice?.Invoke(this, notice);
                });
                Host.Settings.Changed += (_, _) => _dispatcher.InvokeAsync(Reload);
                _subscribed = true;
            }
            Reload();
            Settings.ApplyAutostart();
            Startup = StartupState.Ready;
            _clock.Start();
            _ = RefreshQuotasAsync();
        }
        catch (Exception e)
        {
            Log.Error("App", $"Start failed: {e}");
            var code = CdException.CodeOf(e);
            var entry = ErrorCatalog.Get(code);
            StartupErrorTitle = entry.Title;
            StartupErrorText = $"{entry.Fix} ({code})";
            CanResetConfiguration = code == "CD-2003";
            Startup = StartupState.Failed;
        }
    }

    /// <summary>Rebuilds the lists from the settings, keeping the existing cards.</summary>
    public void Reload()
    {
        var settings = Host.Settings.Snapshot();
        Settings.Load(settings.Preferences);
        var accounts = settings.Accounts.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        var pairs = new List<SyncPairViewModel>();
        foreach (var pair in settings.Syncs)
        {
            accounts.TryGetValue(pair.AccountId, out var account);
            var existing = Pairs.FirstOrDefault(p => p.Id == pair.Id);
            if (existing is null)
            {
                var state = Host.Sync.GetState(pair.Id);
                existing = new SyncPairViewModel(this, pair, account, state);
                _lastRuns[pair.Id] = state?.LastRun;
            }
            else
            {
                existing.Update(pair, account);
            }
            pairs.Add(existing);
        }
        Replace(Pairs, pairs);

        var cards = new List<AccountViewModel>();
        foreach (var account in settings.Accounts)
        {
            var existing = Accounts.FirstOrDefault(a => a.Id == account.Id) ?? new AccountViewModel(this, account);
            existing.Update(account, settings.Syncs.Count(s => s.AccountId == account.Id));
            cards.Add(existing);
        }
        Replace(Accounts, cards);

        HasAccounts = Accounts.Count > 0;
        HasPairs = Pairs.Count > 0;
        RefreshActivity();
        UpdateSummary();
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        for (var i = target.Count - 1; i >= 0; i--)
            if (!items.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < items.Count; i++)
        {
            var index = target.IndexOf(items[i]);
            if (index < 0) target.Insert(i, items[i]);
            else if (index != i) target.Move(index, i);
        }
    }

    private void OnStateChanged(SyncPairState state)
    {
        var pair = Pairs.FirstOrDefault(p => p.Id == state.Id);
        pair?.Apply(state);
        if (!_lastRuns.TryGetValue(state.Id, out var last) || last != state.LastRun)
        {
            _lastRuns[state.Id] = state.LastRun;
            RefreshActivity();
            if (CurrentPage == Page.Trash) Trash.Refresh();
        }
        UpdateSummary();
    }

    private void Tick()
    {
        foreach (var pair in Pairs) pair.Refresh();
        UpdateSummary();
        if (DateTime.Now - _lastQuota > TimeSpan.FromMinutes(30)) _ = RefreshQuotasAsync();
    }

    private void UpdateSummary()
    {
        var count = Pairs.Count;
        var attention = Pairs.Count(p => p.NeedsUser);
        var errors = Pairs.Count(p => p.HasError);
        var busy = Pairs.Count(p => p.IsBusy);
        var paused = Pairs.Count(p => p.IsPaused);
        if (!HasAccounts)
        {
            (SummaryTone, SummaryGlyph, SummaryTitle) = (Tone.Neutral, Glyphs.Cloud, "Willkommen bei CloudDrive-Sync");
            SummaryText = "Verbinde deine Nextcloud, dein IServ oder einen anderen WebDAV-Speicher.";
        }
        else if (count == 0)
        {
            (SummaryTone, SummaryGlyph, SummaryTitle) = (Tone.Neutral, Glyphs.Cloud, "Noch keine Synchronisation");
            SummaryText = "Wähle einen Cloud-Ordner, den CloudDrive-Sync auf diesem PC aktuell hält.";
        }
        else if (attention > 0)
        {
            (SummaryTone, SummaryGlyph) = (Tone.Warning, Glyphs.Warning);
            SummaryTitle = attention == 1 ? "Eine Synchronisation wartet auf dich" : $"{attention} Synchronisationen warten auf dich";
            SummaryText = "Zur Sicherheit wurde angehalten. Bei der Synchronisation steht, was zu tun ist.";
        }
        else if (errors > 0)
        {
            (SummaryTone, SummaryGlyph) = (Tone.Error, Glyphs.Error);
            SummaryTitle = errors == 1 ? "Bei einer Synchronisation gibt es ein Problem" : $"Bei {errors} Synchronisationen gibt es ein Problem";
            SummaryText = "CloudDrive-Sync versucht es automatisch erneut.";
        }
        else if (busy > 0)
        {
            (SummaryTone, SummaryGlyph, SummaryTitle) = (Tone.Busy, Glyphs.Sync, "Wird synchronisiert …");
            SummaryText = count == 1 ? "Gleich ist alles aktuell." : $"{busy} von {count} Synchronisationen laufen gerade.";
        }
        else if (paused == count)
        {
            (SummaryTone, SummaryGlyph, SummaryTitle) = (Tone.Paused, Glyphs.Pause, "Alles angehalten");
            SummaryText = "Fortsetzen kannst du über das Menü einer Synchronisation oder das Symbol im Infobereich.";
        }
        else
        {
            (SummaryTone, SummaryGlyph, SummaryTitle) = (Tone.Ok, Glyphs.Done, "Alles aktuell");
            var last = Pairs.Max(p => p.LastSuccess);
            SummaryText = $"{Format.Count(count, "Synchronisation", "Synchronisationen")} · zuletzt abgeglichen {Format.Ago(last)}";
        }
        Navigation[0].Badge = attention + errors;
        Navigation[2].Badge = Conflicts.Count;
        AllPaused = count > 0 && paused == count;
        SummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshActivity()
    {
        var items = new List<ActivityItem>();
        var conflicts = new List<ConflictItem>();
        foreach (var pair in Pairs)
        {
            // Runs without changes are left out - every few minutes one of them would only crowd the list.
            foreach (var run in Host.Sync.History(pair.Id, 200).Where(r => !r.Success || r.Transfers > 0 || r.Deletes > 0 || r.Conflicts > 0).Take(40))
                items.Add(new ActivityItem(pair.Title, run));
            foreach (var conflict in pair.State?.Conflicts ?? []) conflicts.Add(new ConflictItem(pair.Title, pair.LocalPath, conflict));
        }
        Activity.Clear();
        foreach (var item in items.OrderByDescending(i => i.Started).Take(100)) Activity.Add(item);
        Conflicts.Clear();
        foreach (var conflict in conflicts) Conflicts.Add(conflict);
        Navigation[2].Badge = Conflicts.Count;
    }

    private async Task RefreshQuotasAsync()
    {
        _lastQuota = DateTime.Now;
        foreach (var account in Accounts.ToList())
        {
            try
            {
                account.ShowQuota(await Host.Accounts.GetQuotaAsync(account.Id));
            }
            catch (CdException e)
            {
                Log.Info("App", $"Storage of '{account.Id}' not available: {e.Code}");
            }
        }
    }

    [RelayCommand]
    private void SyncAll() => Host.Sync.RunAll();

    public void PauseAll(bool paused)
    {
        foreach (var pair in Pairs.ToList()) Host.Sync.SetPaused(pair.Id, paused);
    }

    [RelayCommand]
    private void AddAccount()
    {
        var (account, syncNext) = Dialogs.AddAccount();
        if (account is null) return;
        Reload();
        _ = RefreshQuotasAsync();
        if (syncNext) AddSyncFor(account.Id);
    }

    [RelayCommand]
    private void AddSync() => AddSyncFor(null);

    public void AddSyncFor(string? accountId)
    {
        if (Accounts.Count == 0)
        {
            AddAccount();
            return;
        }
        if (Dialogs.AddSync(accountId) is null) return;
        Reload();
        SelectedNav = Navigation[0];
    }

    public void EditSync(SyncPairViewModel pair)
    {
        if (Dialogs.EditSync(pair.Settings)) Reload();
    }

    /// <summary>Signs an account in again; its synchronisations continue at once.</summary>
    public void Relogin(string accountId)
    {
        var account = Host.Accounts.Find(accountId);
        if (account is null || !Dialogs.Relogin(account)) return;
        foreach (var pair in Pairs.Where(p => p.Settings.AccountId == accountId)) Host.Sync.Retry(pair.Id);
        _ = RefreshQuotasAsync();
    }

    public async Task RemoveSyncAsync(SyncPairViewModel pair)
    {
        var answer = Dialogs.Ask("Synchronisation beenden?",
            $"„{pair.Title}“ wird nicht mehr abgeglichen. Alle Dateien bleiben erhalten – im Ordner {pair.LocalPath} und in der Cloud.",
            "Beenden", danger: true);
        if (answer != DialogChoice.Primary) return;
        await RunGuardedAsync(() => Host.Sync.RemoveAsync(pair.Id));
        Reload();
    }

    public async Task RemoveAccountAsync(AccountViewModel account)
    {
        var pairs = Pairs.Where(p => p.Settings.AccountId == account.Id).ToList();
        var text = pairs.Count == 0
            ? $"Die Anmeldung von „{account.Label}“ wird von diesem PC entfernt. In der Cloud ändert sich nichts."
            : $"Die Anmeldung von „{account.Label}“ und {Format.Count(pairs.Count, "seine Synchronisation", "seine Synchronisationen")} werden entfernt. Alle Dateien bleiben am PC und in der Cloud erhalten.";
        if (Dialogs.Ask("Konto entfernen?", text, "Entfernen", danger: true) != DialogChoice.Primary) return;
        await RunGuardedAsync(async () =>
        {
            foreach (var pair in pairs) await Host.Sync.RemoveAsync(pair.Id);
            await Host.Accounts.RemoveAsync(account.Id);
        });
        Reload();
    }

    public void ShowActivity() => SelectedNav = Navigation[2];

    public void ShowTrash() => SelectedNav = Navigation.First(n => n.Page == Page.Trash);

    [RelayCommand]
    private void ShowAccounts() => SelectedNav = Navigation[1];

    [RelayCommand]
    private Task RetryStartAsync() => StartAsync();

    /// <summary>After CD-2003 (key gone): keeps the old configuration aside and starts fresh; accounts sign in again.</summary>
    [RelayCommand]
    private async Task ResetConfigurationAsync()
    {
        var answer = Dialogs.Ask("Anmeldungen zurücksetzen?",
            "Der Schlüssel der gespeicherten Anmeldungen fehlt. CloudDrive-Sync legt die alte, verschlüsselte Datei beiseite und beginnt neu. Danach meldest du jedes Konto einmal neu an – Einstellungen und Dateien bleiben erhalten.",
            "Zurücksetzen");
        if (answer != DialogChoice.Primary) return;
        var config = Host.Paths.RcloneConfig;
        if (File.Exists(config)) File.Move(config, $"{config}.{DateTime.Now:yyyyMMdd-HHmmss}.bak");
        await StartAsync();
    }

    [RelayCommand]
    private void OpenLogs() => Shell.OpenFolder(Host.Paths.LogDir);

    /// <summary>Runs an action and explains errors in a dialog instead of letting them escape.</summary>
    public async Task RunGuardedAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (CdException e)
        {
            var entry = ErrorCatalog.Get(e.Code);
            Dialogs.Ask(entry.Title, $"{entry.Fix} ({e.Code})", "OK", close: "");
        }
    }
}
