using System.Collections.ObjectModel;
using System.IO;
using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

/// <summary>The steps of the window "Ordner synchronisieren" (first setup of a synchronisation).</summary>
public enum AddSyncStep
{
    Folder,
    Selection,
    Local,
    Options,
    Summary,
}

/// <summary>
/// Setting up a synchronisation step by step: cloud folder, what of it (everything or chosen folders and files),
/// where on this PC and whether as files on demand (preselected where the folder allows it) or all files, how
/// (conflicts, interval, deletion guard) - and a summary with the expected size. An existing
/// synchronisation is changed on one page instead (<see cref="SyncSettingsViewModel"/>).
/// </summary>
public sealed partial class AddSyncViewModel : ObservableObject
{
    private readonly CloudDriveSyncHost _host;
    private bool _localTyped;
    private bool _settingLocal;
    // The user chose "all files" on purpose: a folder that allows files on demand does not switch it back.
    private bool _classicChosen;
    private bool _settingMode;

    public AddSyncViewModel(CloudDriveSyncHost host, string? accountId)
    {
        _host = host;
        Selection = new SelectionTree(host);
        foreach (var account in host.Accounts.Accounts) Accounts.Add(account);
        ConflictPolicy = ConflictPolicy.NewerWins;
        IntervalMinutes = 5;
        OnLocalChange = true;
        MaxDeletePercent = 50;
        Account = Accounts.FirstOrDefault(a => a.Id == accountId) ?? Accounts.FirstOrDefault();
    }

    public ObservableCollection<AccountSettings> Accounts { get; } = [];
    public bool ShowAccountChoice => Accounts.Count > 1;
    public ObservableCollection<FolderNode> FolderRoots { get; } = [];
    public SelectionTree Selection { get; }
    public ObservableCollection<FolderWarning> Warnings { get; } = [];

    [ObservableProperty] public partial AddSyncStep Step { get; set; }
    [ObservableProperty] public partial AccountSettings? Account { get; set; }
    [ObservableProperty] public partial FolderNode? SelectedFolder { get; set; }
    [ObservableProperty] public partial FolderNode? ChosenFolder { get; set; }
    [ObservableProperty] public partial bool SelectAll { get; set; } = true;
    [ObservableProperty] public partial string LocalPath { get; set; } = "";
    [ObservableProperty] public partial string LocalError { get; set; } = "";
    [ObservableProperty] public partial bool AcceptWarnings { get; set; }
    [ObservableProperty] public partial ConflictPolicy ConflictPolicy { get; set; }
    [ObservableProperty] public partial int IntervalMinutes { get; set; }
    [ObservableProperty] public partial bool OnLocalChange { get; set; }
    [ObservableProperty] public partial int MaxDeletePercent { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string PreviewText { get; set; } = "";
    /// <summary>The cloud is still being counted; starting is possible meanwhile.</summary>
    [ObservableProperty] public partial bool IsCounting { get; set; }
    [ObservableProperty] public partial bool NotEnoughSpace { get; set; }
    [ObservableProperty] public partial bool AcceptSpace { get; set; }
    [ObservableProperty] public partial bool OnDemand { get; set; } = true;
    [ObservableProperty] public partial string OnDemandProblem { get; set; } = "";

    public SyncPairSettings? Result { get; private set; }

    public event EventHandler? CloseRequested;

    public IReadOnlyList<Choice<int>> IntervalChoices => SyncChoices.Intervals;
    public IReadOnlyList<Choice<int>> DeleteChoices => SyncChoices.DeleteLimits;
    public string IntervalHint => SyncChoices.IntervalHint(IntervalMinutes);
    public string IntervalExplanation => SyncChoices.IntervalExplanation;

    public string StepTitle => Step switch
    {
        AddSyncStep.Folder => "Welcher Cloud-Ordner?",
        AddSyncStep.Selection => "Was davon?",
        AddSyncStep.Local => "Wo auf diesem PC?",
        AddSyncStep.Options => "Wie synchronisieren?",
        _ => "Bereit",
    };

    public string StepText => $"Schritt {(int)Step + 1} von {Enum.GetValues<AddSyncStep>().Length}";
    public bool CanGoBack => !IsBusy && Step != AddSyncStep.Folder;
    public string PrimaryText => Step == AddSyncStep.Summary ? "Synchronisation starten" : "Weiter";
    public bool IsLastStep => Step == AddSyncStep.Summary;
    public bool HasWarnings => Warnings.Count > 0;
    public bool HasError => Error.Length > 0;
    public bool HasLocalError => LocalError.Length > 0;
    public bool CanUseOnDemand => OnDemandProblem.Length == 0;
    public bool HasOnDemandProblem => OnDemandProblem.Length > 0;
    public bool CloudWithoutTimes => Account is { Kind: not WebDavKind.Nextcloud };

    public string ChosenFolderText => ChosenFolder is null || Account is null ? "" : $"{Account.Label} › {CloudFolderNames.ShowPath(Account.Kind, ChosenFolder.Path)}";

    public string SummarySelection => SelectAll ? "Alles in diesem Ordner" : Format.Count(Selection.Collect().Count, "ausgewähltes Element", "ausgewählte Elemente");

    public string SummaryOptions =>
        (OnDemand ? "Dateien bei Bedarf · " : "Alle Dateien auf diesem PC · ") +
        $"{SyncChoices.ConflictTitle(ConflictPolicy)} · {SyncChoices.IntervalTitle(IntervalMinutes)}" +
        (OnLocalChange ? " · Änderungen am PC sofort" : "") +
        (MaxDeletePercent >= 100 ? " · ohne Löschschutz" : $" · Löschschutz ab {MaxDeletePercent} %");

    partial void OnStepChanged(AddSyncStep value)
    {
        Error = "";
        foreach (var name in new[] { nameof(StepTitle), nameof(StepText), nameof(CanGoBack), nameof(PrimaryText), nameof(IsLastStep), nameof(SummarySelection), nameof(SummaryOptions), nameof(ChosenFolderText), nameof(SelectionHint), nameof(HasSelectionHint) })
            OnPropertyChanged(name);
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanGoBack));

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnLocalErrorChanged(string value) => OnPropertyChanged(nameof(HasLocalError));

    partial void OnOnDemandChanged(bool value)
    {
        if (!_settingMode) _classicChosen = !value;
    }

    partial void OnOnDemandProblemChanged(string value)
    {
        OnPropertyChanged(nameof(CanUseOnDemand));
        OnPropertyChanged(nameof(HasOnDemandProblem));
        _settingMode = true;
        // Where files on demand are not possible, all files it is; where they are, they are preselected again.
        OnDemand = value.Length == 0 && !_classicChosen;
        _settingMode = false;
    }

    partial void OnIntervalMinutesChanged(int value) => OnPropertyChanged(nameof(IntervalHint));

    partial void OnAccountChanged(AccountSettings? value)
    {
        OnPropertyChanged(nameof(CloudWithoutTimes));
        if (value is null) return;
        FolderRoots.Clear();
        var root = new FolderNode($"{value.Label} – alles", "", true, 0, null, LoadFoldersAsync);
        FolderRoots.Add(root);
        root.IsSelected = true;
        root.IsExpanded = true;
        SelectedFolder = root;
    }

    partial void OnLocalPathChanged(string value)
    {
        if (!_settingLocal) _localTyped = true;
        AcceptWarnings = false;
        CheckLocal(showErrors: false);
    }

    private async Task<IReadOnlyList<FolderNode>> LoadFoldersAsync(FolderNode parent)
    {
        var entries = await _host.Accounts.ListAsync(Account!.Id, parent.Path, includeFiles: false);
        return entries.Select(e => new FolderNode(CloudFolderNames.Show(Account.Kind, e.Name, e.Path), e.Path, true, 0, parent, LoadFoldersAsync)).ToList();
    }

    private string BasePath => ChosenFolder?.Path ?? "";

    /// <summary>
    /// For IServ's top folders, which hold nothing but other folders: the whole account ("Eigene Dateien" and "Gruppen")
    /// and "Gruppen" with a folder per group. Everything can be synchronised, or the folders ticked one by one.
    /// </summary>
    public string SelectionHint => Account?.Kind != WebDavKind.IServ || ChosenFolder is null ? "" : ChosenFolder.Path.Trim('/') switch
    {
        "" => "Das ganze Konto: „Eigene Dateien“ und „Gruppen“. Synchronisiere alles – oder hake an, welche Ordner und Gruppen auf diesen PC sollen.",
        "Groups" => "Hier liegen die Ordner deiner Gruppen. Synchronisiere alle Gruppen – oder hake an, welche auf diesen PC sollen.",
        _ => "",
    };

    public bool HasSelectionHint => SelectionHint.Length > 0;

    private async Task LoadSelectionAsync()
    {
        try
        {
            await Selection.LoadAsync(Account!.Id, BasePath, []);
        }
        catch (CdException e)
        {
            Error = $"Der Ordner konnte nicht gelesen werden: {ErrorCatalog.Get(e.Code).Title}";
        }
    }

    private SyncPairSettings BuildDraft() => new()
    {
        AccountId = Account!.Id,
        RemotePath = BasePath,
        LocalPath = LocalPath.Trim(),
        Selection = new SyncSelection
        {
            Mode = SelectAll ? SelectionMode.All : SelectionMode.Selected,
            Include = SelectAll ? [] : Selection.Collect(),
        },
        Mode = OnDemand && CanUseOnDemand ? SyncMode.OnDemand : SyncMode.Classic,
        Conflicts = ConflictPolicy,
        IntervalMinutes = IntervalMinutes,
        OnLocalChange = OnLocalChange,
        MaxDeletePercent = MaxDeletePercent,
    };

    private void SetLocal(string path)
    {
        _settingLocal = true;
        LocalPath = path;
        _settingLocal = false;
    }

    private string DefaultLocalPath()
    {
        return LocalFolderCheck.SuggestDefaultPath(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Account?.Label ?? "Cloud",
            ChosenFolder is { Path.Length: > 0 } ? ChosenFolder.Name : null,
            _host.Sync.Pairs) ?? "";
    }

    /// <summary>Checks the local folder; warnings appear at once, errors only when going on.</summary>
    private bool CheckLocal(bool showErrors)
    {
        var previousWarnings = Warnings.Select(w => (w.Kind, w.Text)).ToArray();
        Warnings.Clear();
        try
        {
            foreach (var warning in _host.Sync.CheckFolder(LocalPath.Trim(), null)) Warnings.Add(warning);
            LocalError = "";
            OnDemandProblem = SyncService.OnDemandProblem(LocalPath.Trim()) ?? "";
            return true;
        }
        catch (CdException e)
        {
            LocalError = e.Code is "CD-4506" or "CD-4513"
                ? ErrorCatalog.Get(e.Code).Fix
                : showErrors || LocalPath.Trim().Length > 0
                    ? "Bitte gib einen vollständigen Ordnerpfad auf einem verfügbaren Laufwerk an, z. B. C:\\Users\\Name\\CloudDrive-Sync."
                    : "";
            OnDemandProblem = "";
            return false;
        }
        finally
        {
            if (!previousWarnings.SequenceEqual(Warnings.Select(w => (w.Kind, w.Text)))) AcceptWarnings = false;
            OnPropertyChanged(nameof(HasWarnings));
        }
    }

    public void ChooseLocalFolder(string folder)
    {
        _localTyped = true;
        SetLocal(folder);
        CheckLocal(showErrors: true);
    }

    [RelayCommand]
    private void Back()
    {
        StopCounting();
        Error = "";
        if (Step != AddSyncStep.Folder) Step -= 1;
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        Error = "";
        switch (Step)
        {
            case AddSyncStep.Folder:
                if (Account is null || SelectedFolder is null || SelectedFolder.IsPlaceholder)
                {
                    Error = "Bitte wähle einen Ordner aus.";
                    return;
                }

                if (ChosenFolder?.Path != SelectedFolder.Path || Selection.Roots.Count == 0)
                {
                    ChosenFolder = SelectedFolder;
                    await LoadSelectionAsync();
                }
                if (!_localTyped) SetLocal(DefaultLocalPath());
                Step = AddSyncStep.Selection;
                break;
            case AddSyncStep.Selection:
                if (!SelectAll && Selection.Collect().Count == 0)
                {
                    Error = "Bitte setze bei mindestens einem Ordner oder einer Datei einen Haken – oder wähle „Alles“.";
                    return;
                }
                Step = AddSyncStep.Local;
                CheckLocal(showErrors: false);
                break;
            case AddSyncStep.Local:
                if (!CheckLocal(showErrors: true)) return;
                if (Warnings.Count > 0 && !AcceptWarnings)
                {
                    Error = "Bitte lies die Hinweise und bestätige, dass du diesen Ordner trotzdem verwenden möchtest.";
                    return;
                }
                Step = AddSyncStep.Options;
                break;
            case AddSyncStep.Options:
                Step = AddSyncStep.Summary;
                await PreviewAsync();
                break;
            case AddSyncStep.Summary:
                await FinishAsync();
                break;
        }
    }

    private CancellationTokenSource? _counting;

    /// <summary>Ends a count still running (going back, starting, closing the window).</summary>
    public void StopCounting()
    {
        _counting?.Cancel();
        _counting = null;
        IsCounting = false;
    }

    /// <summary>
    /// Counts what lies in the chosen part of the cloud - a whole account can take minutes, so the count shows how far it
    /// got, and starting never waits for it. Files on demand need no room for the cloud's files anyway.
    /// </summary>
    private async Task PreviewAsync()
    {
        StopCounting();
        var counting = _counting = new CancellationTokenSource();
        NotEnoughSpace = false;
        AcceptSpace = false;
        IsCounting = true;
        PreviewText = CountingText(null);
        var progress = new Progress<ListingProgress>(p =>
        {
            if (!counting.IsCancellationRequested) PreviewText = CountingText(p);
        });
        try
        {
            var preview = await _host.Sync.PreviewAsync(BuildDraft(), progress, counting.Token);
            if (counting.IsCancellationRequested) return;
            var lines = new List<string> { $"In der Cloud: {Format.Count(preview.CloudFiles, "Datei", "Dateien")} · {Format.Bytes(preview.CloudBytes)}" };
            if (preview.UnreadableFolders > 0)
                lines.Add($"{Format.Count(preview.UnreadableFolders, "Ordner lässt", "Ordner lassen")} sich nicht lesen (z. B. Freigaben nur zum Hochladen) – sie bleiben außen vor.");
            if (OnDemand)
            {
                lines.Add("Auf diesem PC: braucht kaum Platz – geladen wird erst, was du öffnest oder immer behalten willst.");
                if (preview.LocalFiles > 0) lines.Add($"Schon am PC: {Format.Count(preview.LocalFiles, "Datei", "Dateien")} – wird zusammengeführt, nichts wird gelöscht");
            }
            else
            {
                lines.Add(preview.LocalFiles > 0
                    ? $"Schon am PC: {Format.Count(preview.LocalFiles, "Datei", "Dateien")} · {Format.Bytes(preview.LocalBytes)} – wird zusammengeführt, nichts wird gelöscht"
                    : "Am PC: noch leer");
                if (preview.FreeBytes > 0) lines.Add($"Frei auf dem Laufwerk: {Format.Bytes(preview.FreeBytes)}");
                NotEnoughSpace = preview.FreeBytes > 0 && !preview.EnoughSpace;
            }
            PreviewText = string.Join(Environment.NewLine, lines);
        }
        catch (OperationCanceledException)
        {
            // Gone back, started or closed meanwhile.
        }
        catch (CdException e)
        {
            if (!counting.IsCancellationRequested)
                PreviewText = $"Die Größe konnte nicht ermittelt werden ({ErrorCatalog.Get(e.Code).Title}). Du kannst trotzdem starten.";
        }
        finally
        {
            if (_counting == counting) IsCounting = false;
        }
    }

    /// <summary>What the count found so far - and that starting need not wait.</summary>
    private string CountingText(ListingProgress? progress)
    {
        var found = progress is { } p
            ? $"Zählt die Dateien in der Cloud … bisher {Format.Count(p.Folders, "Ordner", "Ordner")}, {Format.Count(p.Files, "Datei", "Dateien")}, {Format.Bytes(p.Bytes)}"
            : "Zählt die Dateien in der Cloud …";
        var start = OnDemand
            ? "Du kannst schon starten – bei „Dateien bei Bedarf“ braucht der PC dafür kaum Platz."
            : "Du kannst schon starten; ob der Platz reicht, ist dann aber nicht geprüft.";
        return found + Environment.NewLine + start;
    }

    private async Task FinishAsync()
    {
        if (NotEnoughSpace && !AcceptSpace)
        {
            Error = "Der freie Speicher reicht voraussichtlich nicht. Bestätige, dass du trotzdem starten möchtest – oder wähle weniger aus.";
            return;
        }
        StopCounting();
        IsBusy = true;
        try
        {
            Result = await _host.Sync.AddAsync(BuildDraft());
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        catch (CdException e)
        {
            var entry = ErrorCatalog.Get(e.Code);
            Error = $"{entry.Title}: {entry.Fix}";
        }
        catch (IOException e)
        {
            Error = $"Der Ordner konnte nicht angelegt werden: {e.Message}";
        }
        catch (UnauthorizedAccessException)
        {
            Error = "Auf diesen Ordner hat CloudDrive-Sync keinen Schreibzugriff. Bitte wähle einen anderen.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
