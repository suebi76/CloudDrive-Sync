using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

/// <summary>
/// The settings of an existing synchronisation on one page: what is synchronised, how often, what happens with
/// conflicts, when the deletion guard stops. Setting one up goes step by step instead (<see cref="AddSyncViewModel"/>).
/// </summary>
public sealed partial class SyncSettingsViewModel : ObservableObject
{
    private readonly CloudDriveSyncHost _host;
    private SyncPairSettings _pair;

    public SyncSettingsViewModel(CloudDriveSyncHost host, SyncPairSettings pair)
    {
        _host = host;
        _pair = pair;
        Selection = new SelectionTree(host);
        var account = host.Accounts.Find(pair.AccountId);
        CloudText = $"{account?.Label ?? pair.AccountId} › {CloudFolderNames.ShowPath(account?.Kind ?? WebDavKind.Other, pair.RemotePath)}";
        CloudWithoutTimes = account is { Kind: not WebDavKind.Nextcloud };
        SelectAll = pair.Selection.Mode == SelectionMode.All;
        IntervalMinutes = pair.IntervalMinutes;
        OnLocalChange = pair.OnLocalChange;
        ConflictPolicy = pair.Conflicts;
        MaxDeletePercent = pair.MaxDeletePercent;
        SwitchProblem = pair.Mode == SyncMode.Classic && SyncService.OnDemandSupported ? SyncService.OnDemandProblem(pair.LocalPath) ?? "" : "";
    }

    /// <summary>Asks before switching (title, text, button); the window shows the question over itself.</summary>
    public Func<string, string, string, bool>? Confirm { get; set; }

    public bool IsOnDemand => _pair.Mode == SyncMode.OnDemand;

    public string ModeTitle => IsOnDemand ? "Dateien bei Bedarf" : "Alle Dateien auf diesem PC";

    public string ModeText => IsOnDemand
        ? "Alle Dateien sind im Explorer zu sehen, belegen aber erst Platz, wenn du sie öffnest oder „Immer auf diesem Gerät beibehalten“ wählst."
        : "Jede Datei liegt vollständig auf diesem PC.";

    public string SwitchText => IsOnDemand ? "Auf „Alle Dateien auf diesem PC“ umstellen …" : "Auf „Dateien bei Bedarf“ umstellen …";

    /// <summary>Why this folder cannot hold files on demand ("" when it can).</summary>
    public string SwitchProblem { get; }

    public bool HasSwitchProblem => SwitchProblem.Length > 0;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SwitchModeCommand))]
    public partial bool IsSwitching { get; set; }

    [ObservableProperty] public partial string SwitchProgress { get; set; } = "";

    private bool CanSwitchMode => !IsSwitching && SyncService.OnDemandSupported && (IsOnDemand || !HasSwitchProblem);

    /// <summary>Switches between "all files on this PC" and files on demand, after a question that says what happens.</summary>
    [RelayCommand(CanExecute = nameof(CanSwitchMode))]
    private async Task SwitchModeAsync()
    {
        Error = "";
        var toOnDemand = !IsOnDemand;
        var missing = _host.Sync.GetState(_pair.Id)?.Space is { } space && space.CloudBytes > space.OnPcBytes ? $" (etwa {Format.Bytes(space.CloudBytes - space.OnPcBytes)})" : "";
        var asked = toOnDemand
            ? Confirm?.Invoke("Auf „Dateien bei Bedarf“ umstellen?",
                "CloudDrive-Sync gleicht beide Seiten noch einmal ab und macht dann aus den Dateien in diesem Ordner Dateien bei Bedarf – ohne etwas neu zu übertragen. Sie bleiben vorerst auf diesem PC; Platz schaffst du im Explorer mit „Speicherplatz freigeben“ oder automatisch (Einstellungen). Zurückstellen geht jederzeit.",
                "Umstellen")
            : Confirm?.Invoke("Auf „Alle Dateien auf diesem PC“ umstellen?",
                $"Dafür lädt CloudDrive-Sync alle Dateien herunter, die nur online liegen{missing}. Danach sind es normale Dateien auf diesem PC, und der Eintrag im Navigationsbereich des Explorers verschwindet.",
                "Umstellen");
        if (asked != true || !SyncService.OnDemandSupported) return;
        IsSwitching = true;
        var progress = new Progress<string>(text => SwitchProgress = text);
        try
        {
            if (toOnDemand) await _host.Sync.ConvertToOnDemandAsync(_pair.Id, progress);
            else await _host.Sync.ConvertToClassicAsync(_pair.Id, progress);
            _pair = _host.Sync.FindPair(_pair.Id) ?? _pair;
            // The overview shows the new mode.
            Saved = true;
            foreach (var name in new[] { nameof(IsOnDemand), nameof(ModeTitle), nameof(ModeText), nameof(SwitchText), nameof(CanChangeSelection), nameof(SelectionHint) })
                OnPropertyChanged(name);
        }
        catch (CdException e)
        {
            var entry = ErrorCatalog.Get(e.Code);
            Error = $"{entry.Title}: {entry.Fix}";
        }
        finally
        {
            IsSwitching = false;
            SwitchProgress = "";
        }
    }

    public SelectionTree Selection { get; }

    /// <summary>
    /// With files on demand a changed selection would leave files behind that no longer open; changing it follows in a
    /// later test version.
    /// </summary>
    public bool CanChangeSelection => _pair.Mode == SyncMode.Classic;

    public string SelectionHint => CanChangeSelection
        ? "Änderst du die Auswahl, baut CloudDrive-Sync den Abgleich danach neu auf – ohne etwas zu löschen. Abgewählte Ordner bleiben am PC liegen, werden aber nicht mehr abgeglichen."
        : "Bei „Dateien bei Bedarf“ lässt sich die Auswahl in dieser Testversion noch nicht ändern.";
    public string CloudText { get; }
    public string LocalPath => _pair.LocalPath;
    public bool CloudWithoutTimes { get; }

    public IReadOnlyList<Choice<int>> IntervalChoices => SyncChoices.Intervals;
    public IReadOnlyList<Choice<ConflictPolicy>> ConflictChoices => SyncChoices.Conflicts;
    public IReadOnlyList<Choice<int>> DeleteChoices => SyncChoices.DeleteLimits;
    public string IntervalExplanation => SyncChoices.IntervalExplanation;
    public string IntervalHint => SyncChoices.IntervalHint(IntervalMinutes);
    public string ConflictHint => SyncChoices.ConflictHint(ConflictPolicy);

    [ObservableProperty] public partial bool SelectAll { get; set; }
    [ObservableProperty] public partial int IntervalMinutes { get; set; }
    [ObservableProperty] public partial bool OnLocalChange { get; set; }
    [ObservableProperty] public partial ConflictPolicy ConflictPolicy { get; set; }
    [ObservableProperty] public partial int MaxDeletePercent { get; set; }
    [ObservableProperty] public partial string SelectionError { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";

    public bool HasError => Error.Length > 0;
    public bool HasSelectionError => SelectionError.Length > 0;
    public bool Saved { get; private set; }

    public event EventHandler? CloseRequested;

    partial void OnIntervalMinutesChanged(int value) => OnPropertyChanged(nameof(IntervalHint));

    partial void OnConflictPolicyChanged(ConflictPolicy value) => OnPropertyChanged(nameof(ConflictHint));

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnSelectionErrorChanged(string value) => OnPropertyChanged(nameof(HasSelectionError));

    /// <summary>Called when the window has opened: reads the cloud folder for the tree.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            await Selection.LoadAsync(_pair.AccountId, _pair.RemotePath, _pair.Selection.Include);
        }
        catch (CdException e)
        {
            // Everything else can still be changed; the chosen folders and files stay as they are.
            SelectionError = $"Der Cloud-Ordner konnte nicht gelesen werden ({ErrorCatalog.Get(e.Code).Title}). Die bisherige Auswahl bleibt erhalten.";
        }
    }

    [RelayCommand]
    private void OpenFolder() => Shell.OpenFolder(_pair.LocalPath);

    [RelayCommand]
    private void Save()
    {
        if (IsSwitching) return;
        Error = "";
        var includes = SelectAll ? [] : Selection.Collect();
        if (CanChangeSelection && !SelectAll && includes.Count == 0)
        {
            Error = "Bitte setze bei mindestens einem Ordner oder einer Datei einen Haken – oder wähle „Alles in diesem Ordner“.";
            return;
        }
        var selection = new SyncSelection
        {
            Mode = SelectAll ? SelectionMode.All : SelectionMode.Selected,
            Include = includes,
            Exclude = _pair.Selection.Exclude.ToList(),
        };
        try
        {
            _host.Sync.Update(_pair.Id, pair =>
            {
                if (CanChangeSelection) pair.Selection = selection;
                pair.Conflicts = ConflictPolicy;
                pair.IntervalMinutes = IntervalMinutes;
                pair.OnLocalChange = OnLocalChange;
                pair.MaxDeletePercent = MaxDeletePercent;
            });
            Saved = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (CdException e)
        {
            var entry = ErrorCatalog.Get(e.Code);
            Error = $"{entry.Title}: {entry.Fix}";
        }
    }
}
