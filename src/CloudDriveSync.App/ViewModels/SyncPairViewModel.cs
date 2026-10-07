using System.Text.RegularExpressions;
using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

/// <summary>One synchronisation as a card: what it is, how it stands, what the user can do.</summary>
public sealed partial class SyncPairViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private SyncPairSettings _pair;
    private AccountSettings? _account;
    private SyncPairState? _state;

    public SyncPairViewModel(MainViewModel main, SyncPairSettings pair, AccountSettings? account, SyncPairState? state)
    {
        _main = main;
        _pair = pair;
        _account = account;
        _state = state;
        Refresh();
    }

    public string Id => _pair.Id;
    public SyncPairSettings Settings => _pair;
    public SyncPairState? State => _state;
    public AccountSettings? Account => _account;

    [ObservableProperty] public partial string Title { get; set; } = "";
    /// <summary>The cloud folder without the account ("Unterricht › Mathe"), for the account page.</summary>
    [ObservableProperty] public partial string FolderTitle { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    /// <summary>Files on demand: "1,2 GB von 18 GB auf diesem PC" (empty before the first run and in classic mode).</summary>
    [ObservableProperty] public partial string SpaceText { get; set; } = "";
    [ObservableProperty] public partial string LocalPath { get; set; } = "";
    [ObservableProperty] public partial string KindGlyph { get; set; } = Glyphs.Cloud;
    [ObservableProperty] public partial Tone Tone { get; set; }
    [ObservableProperty] public partial string StatusGlyph { get; set; } = Glyphs.Done;
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool IsSyncing { get; set; }
    [ObservableProperty] public partial bool IsIndeterminate { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; } = "";
    [ObservableProperty] public partial bool IsPaused { get; set; }
    [ObservableProperty] public partial string PauseText { get; set; } = "Anhalten";
    [ObservableProperty] public partial bool ShowAttention { get; set; }
    [ObservableProperty] public partial bool AttentionIsCritical { get; set; }
    [ObservableProperty] public partial string AttentionTitle { get; set; } = "";
    [ObservableProperty] public partial string AttentionText { get; set; } = "";
    [ObservableProperty] public partial string AttentionDetail { get; set; } = "";
    [ObservableProperty] public partial string? PrimaryActionText { get; set; }
    [ObservableProperty] public partial string? SecondaryActionText { get; set; }
    [ObservableProperty] public partial int ConflictCount { get; set; }
    [ObservableProperty] public partial bool HasLocalOnly { get; set; }
    [ObservableProperty] public partial string LocalOnlyTitle { get; set; } = "";
    [ObservableProperty] public partial string LocalOnlyList { get; set; } = "";
    [ObservableProperty] public partial string ConflictText { get; set; } = "";

    public bool NeedsUser => _state?.Status == SyncStatus.NeedsAttention;
    public bool HasError => _state?.Status == SyncStatus.Error;
    public bool IsBusy => _state?.Status is SyncStatus.Syncing or SyncStatus.Waiting;
    public DateTimeOffset? LastSuccess => _state?.LastSuccess;
    public IReadOnlyList<Choice<int>> IntervalChoices => SyncChoices.Intervals;

    /// <summary>How often the cloud is asked for news; changed directly on the account page.</summary>
    public int IntervalMinutes
    {
        get => _pair.IntervalMinutes;
        set
        {
            if (value <= 0 || value == _pair.IntervalMinutes) return;
            _pair.IntervalMinutes = value; // shown at once; the saved settings arrive with the next reload
            _main.Host.Sync.Update(Id, pair => pair.IntervalMinutes = value);
            Refresh();
        }
    }

    public void Update(SyncPairSettings pair, AccountSettings? account)
    {
        _pair = pair;
        _account = account;
        Refresh();
    }

    public void Apply(SyncPairState state)
    {
        _state = state;
        Refresh();
    }

    /// <summary>Recomputes everything shown (also every minute, for "vor 5 Min.").</summary>
    public void Refresh()
    {
        var accountName = _account?.Label ?? _pair.AccountId;
        FolderTitle = CloudFolderNames.ShowPath(_account?.Kind ?? WebDavKind.Other, _pair.RemotePath);
        Title = $"{accountName} › {FolderTitle}";
        OnPropertyChanged(nameof(IntervalMinutes));
        LocalPath = _pair.LocalPath;
        KindGlyph = Glyphs.Of(_account?.Kind ?? WebDavKind.Other);
        var selection = _pair.Selection.Mode == SelectionMode.All
            ? "alles"
            : Format.Count(_pair.Selection.Include.Count, "ausgewähltes Element", "ausgewählte Elemente");
        var onDemand = _pair.Mode == SyncMode.OnDemand;
        Subtitle = $"{Glyphs.NameOf(_account?.Kind ?? WebDavKind.Other)} · {selection}{(onDemand ? " · Dateien bei Bedarf" : "")} · {SyncChoices.IntervalTitle(_pair.IntervalMinutes)}";
        SpaceText = onDemand && _state?.Space is { } space ? $"{Format.Bytes(space.OnPcBytes)} von {Format.Bytes(space.CloudBytes)} auf diesem PC" : "";
        IsPaused = _pair.Paused;
        PauseText = _pair.Paused ? "Fortsetzen" : "Anhalten";

        var state = _state;
        var status = state?.Status ?? (_pair.Paused ? SyncStatus.Paused : SyncStatus.Idle);
        IsSyncing = status == SyncStatus.Syncing;
        ShowAttention = false;
        PrimaryActionText = null;
        SecondaryActionText = null;
        AttentionDetail = "";
        switch (status)
        {
            case SyncStatus.Syncing:
                Tone = Tone.Busy;
                StatusGlyph = Glyphs.Sync;
                StatusText = string.IsNullOrEmpty(state?.Activity) ? "Wird synchronisiert …" : state!.Activity;
                ShowProgress(state?.Progress ?? JobProgress.None);
                break;
            case SyncStatus.Waiting:
                Tone = Tone.Busy;
                StatusGlyph = Glyphs.Clock;
                StatusText = string.IsNullOrEmpty(state?.Activity) ? "Wartet, bis ein anderer Abgleich fertig ist …" : state!.Activity;
                break;
            case SyncStatus.Paused:
                Tone = Tone.Paused;
                StatusGlyph = Glyphs.Pause;
                StatusText = state?.LastSuccess is null ? "Angehalten" : $"Angehalten · zuletzt aktuell {Format.Ago(state.LastSuccess)}";
                break;
            case SyncStatus.NeedsAttention:
                Tone = Tone.Warning;
                StatusGlyph = Glyphs.Warning;
                StatusText = "Angehalten – deine Entscheidung ist nötig";
                DescribeDecision(state!);
                break;
            case SyncStatus.Error when state?.ErrorCode == "CD-4510":
                // Not an error of its own: the file goes as soon as the other program lets it go.
                Tone = Tone.Warning;
                StatusGlyph = Glyphs.Clock;
                StatusText = "Wartet auf eine geöffnete Datei – neuer Versuch jede Minute";
                DescribeError(state);
                AttentionIsCritical = false;
                break;
            case SyncStatus.Error when state?.ErrorCode == "CD-4511":
                // Everything else keeps being synchronised; the changes the server did not take stay on the PC.
                Tone = Tone.Warning;
                StatusGlyph = Glyphs.Warning;
                StatusText = "Nicht alles hochgeladen – in manchen Ordnern darfst du nur lesen";
                DescribeError(state);
                AttentionIsCritical = false;
                break;
            case SyncStatus.Error:
                Tone = Tone.Error;
                StatusGlyph = Glyphs.Error;
                StatusText = $"Fehler – CloudDrive-Sync versucht es spätestens in {Format.Count(_pair.IntervalMinutes, "Minute", "Minuten")} erneut";
                DescribeError(state!);
                break;
            default:
                Tone = Tone.Ok;
                StatusGlyph = Glyphs.Done;
                StatusText = state?.FirstSyncDone == true ? $"Aktuell · zuletzt abgeglichen {Format.Ago(state.LastSuccess)}" : "Erster Abgleich startet gleich …";
                break;
        }

        // Files the server did not take (a folder to read only) stay on the PC.
        var localOnly = _pair.LocalOnly;
        HasLocalOnly = localOnly.Count > 0;
        LocalOnlyTitle = localOnly.Count == 1
            ? $"„{System.IO.Path.GetFileName(localOnly[0])}“ bleibt nur auf diesem PC"
            : $"{localOnly.Count} Dateien bleiben nur auf diesem PC";
        LocalOnlyList = string.Join(Environment.NewLine, localOnly.Take(50)) + (localOnly.Count > 50 ? Environment.NewLine + "…" : "");

        var conflicts = state?.Conflicts.Count ?? 0;
        ConflictCount = conflicts;
        ConflictText = conflicts == 0 ? "" : $"{Format.Count(conflicts, "Konflikt", "Konflikte")} – beide Fassungen sind erhalten. Anzeigen";
        OnPropertyChanged(nameof(NeedsUser));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsBusy));
    }

    private void ShowProgress(JobProgress progress)
    {
        if (progress.TotalTransfers > 0)
        {
            IsIndeterminate = false;
            Progress = progress.TotalBytes > 0 ? 100.0 * progress.Bytes / progress.TotalBytes : 100.0 * progress.Transfers / progress.TotalTransfers;
            var speed = progress.BytesPerSecond > 0 ? $" · {Format.Speed(progress.BytesPerSecond)}" : "";
            ProgressText = $"{Format.Number(progress.Transfers)} von {Format.Count(progress.TotalTransfers, "Datei", "Dateien")} · {Format.Bytes(progress.Bytes)} von {Format.Bytes(progress.TotalBytes)}{speed}";
        }
        else
        {
            IsIndeterminate = true;
            Progress = 0;
            // A large folder takes a while to read - the count shows it is still going.
            ProgressText = progress.Checks > 0 ? $"{Format.Count(progress.Checks, "Datei", "Dateien")} verglichen"
                : progress.Listed > 0 ? $"Liest Cloud und PC: {Format.Count(progress.Listed, "Eintrag", "Einträge")} …"
                : "Vergleicht Cloud und PC …";
        }
    }

    private void DescribeDecision(SyncPairState state)
    {
        ShowAttention = true;
        AttentionIsCritical = false;
        var entry = ErrorCatalog.Get(state.ErrorCode ?? "CD-9000");
        AttentionTitle = entry.Title;
        AttentionText = entry.Fix;
        AttentionDetail = state.ErrorDetail ?? "";
        switch (state.Decision)
        {
            case SyncDecision.Deletions when state.ErrorCode == "CD-4509":
                PrimaryActionText = "Neu aufbauen (nichts löschen)";
                SecondaryActionText = "Änderungen übernehmen …";
                break;
            case SyncDecision.Deletions:
                AttentionText = DescribeDeletions(state.ErrorDetail) + " Willst du sie wiederherstellen oder die Löschungen übernehmen?";
                PrimaryActionText = "Dateien wiederherstellen";
                SecondaryActionText = "Löschungen übernehmen …";
                break;
            case SyncDecision.Rebuild:
                PrimaryActionText = "Neu aufbauen";
                break;
            case SyncDecision.SignIn:
                PrimaryActionText = "Neu anmelden …";
                break;
            case SyncDecision.Folder when state.ErrorCode == "CD-4503":
                AttentionText = entry.Fix + " Wurde der Ordner verschoben oder umbenannt, wähle „Reparieren“: CloudDrive-Sync legt die Wächterdateien neu an und baut den Abgleich auf, ohne etwas zu löschen.";
                PrimaryActionText = "Reparieren";
                break;
            case SyncDecision.Folder:
                PrimaryActionText = "Erneut versuchen";
                break;
        }
    }

    private void DescribeError(SyncPairState state)
    {
        ShowAttention = true;
        AttentionIsCritical = true;
        var entry = ErrorCatalog.Get(state.ErrorCode ?? "CD-9000");
        AttentionTitle = entry.Title;
        AttentionText = entry.Fix;
        AttentionDetail = state.ErrorDetail ?? "";
        PrimaryActionText = entry.NeedsSignIn ? "Neu anmelden …" : "Erneut versuchen";
    }

    /// <summary>"8 von 10 Dateien wurden am PC gelöscht." from bisync's report.</summary>
    internal static string DescribeDeletions(string? detail)
    {
        var match = Regex.Match(detail ?? "", @"\(>?\d+%, (\d+) of (\d+)\) on (Path1|Path2)");
        if (!match.Success) return "Es würden mehr Dateien gelöscht, als erlaubt ist.";
        var side = match.Groups[3].Value == "Path1" ? "in der Cloud" : "am PC";
        return $"{match.Groups[1].Value} von {match.Groups[2].Value} Dateien wurden {side} gelöscht.";
    }

    [RelayCommand]
    private void SyncNow() => _main.Host.Sync.RunNow(Id);

    [RelayCommand]
    private void OpenFolder() => Shell.OpenFolder(_pair.LocalPath);

    [RelayCommand]
    private void TogglePause() => _main.Host.Sync.SetPaused(Id, !_pair.Paused);

    [RelayCommand]
    private void OpenSettings() => _main.OpenSyncSettings(this);

    [RelayCommand]
    private void Verify() => _main.Dialogs.Verify(_pair, Title);

    [RelayCommand]
    private void Rebuild()
    {
        var answer = _main.Dialogs.Ask("Abgleich neu aufbauen?",
            "CloudDrive-Sync vergleicht beide Seiten vollständig und führt sie zusammen. Dabei wird nichts gelöscht; abweichende Dateien bleiben als Kopie erhalten.",
            "Neu aufbauen");
        if (answer == DialogChoice.Primary) _main.Host.Sync.Rebuild(Id);
    }

    [RelayCommand]
    private Task RemoveAsync() => _main.RemoveSyncAsync(this);

    [RelayCommand]
    private void ShowConflicts() => _main.ShowActivity();

    [RelayCommand]
    private void ShowTrash() => _main.ShowTrash();

    [RelayCommand]
    private void ShowLocalOnly()
    {
        if (_pair.LocalOnly.FirstOrDefault() is { } first) Shell.ShowInFolder(System.IO.Path.Combine(_pair.LocalPath, first.Replace('/', '\\')));
    }

    [RelayCommand]
    private void RetryLocalOnly() => _main.Host.Sync.RetryLocalOnly(Id);

    [RelayCommand]
    private async Task PrimaryActionAsync()
    {
        var state = _state;
        if (state is null) return;
        if (state.Status == SyncStatus.Error)
        {
            if (ErrorCatalog.Get(state.ErrorCode ?? "CD-9000").NeedsSignIn) await SignInAgainAsync();
            else _main.Host.Sync.RunNow(Id);
            return;
        }
        switch (state.Decision)
        {
            case SyncDecision.Deletions when state.ErrorCode == "CD-4509":
                _main.Host.Sync.Rebuild(Id);
                break;
            case SyncDecision.Deletions:
                _main.Host.Sync.ResolveDeletions(Id, apply: false);
                break;
            case SyncDecision.Rebuild:
                _main.Host.Sync.Rebuild(Id);
                break;
            case SyncDecision.SignIn:
                await SignInAgainAsync();
                break;
            case SyncDecision.Folder when state.ErrorCode == "CD-4503":
                await _main.RunGuardedAsync(() => _main.Host.Sync.RepairAsync(Id));
                break;
            case SyncDecision.Folder:
                _main.Host.Sync.Retry(Id);
                break;
        }
    }

    [RelayCommand]
    private void SecondaryAction()
    {
        var state = _state;
        if (state?.Decision != SyncDecision.Deletions) return;
        var text = state.ErrorCode == "CD-4509"
            ? "Die Änderungen werden auf die andere Seite übertragen. Überschriebene Dateien landen im Papierkorb von CloudDrives."
            : DescribeDeletions(state.ErrorDetail) + " Sollen sie auch auf der anderen Seite gelöscht werden? Gelöschte Dateien landen im Papierkorb – bei Nextcloud in ihrem eigenen, sonst im Ordner „.clouddrive-papierkorb“.";
        var answer = _main.Dialogs.Ask(state.ErrorCode == "CD-4509" ? "Änderungen übernehmen?" : "Löschungen übernehmen?", text,
            state.ErrorCode == "CD-4509" ? "Übernehmen" : "Löschen", danger: true);
        if (answer == DialogChoice.Primary) _main.Host.Sync.ResolveDeletions(Id, apply: true);
    }

    private Task SignInAgainAsync()
    {
        // All synchronisations of the account continue after the new sign-in.
        if (_account is not null) _main.Relogin(_account.Id);
        return Task.CompletedTask;
    }
}
