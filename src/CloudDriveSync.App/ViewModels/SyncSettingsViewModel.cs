using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
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
    private readonly SyncPairSettings _pair;

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
