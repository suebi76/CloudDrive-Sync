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
/// where on this PC, how (conflicts, interval, deletion guard) - and a summary with the expected size. The same
/// dialog changes an existing synchronisation (selection and options).
/// </summary>
public sealed partial class AddSyncViewModel : ObservableObject
{
    private readonly CloudDriveSyncHost _host;
    private readonly SyncPairSettings? _editing;
    private readonly List<string> _originalIncludes = [];
    private bool _localTyped;
    private bool _settingLocal;

    public AddSyncViewModel(CloudDriveSyncHost host, string? accountId, SyncPairSettings? editing = null)
    {
        _host = host;
        _editing = editing;
        foreach (var account in host.Accounts.Accounts) Accounts.Add(account);
        ConflictPolicy = editing?.Conflicts ?? ConflictPolicy.NewerWins;
        IntervalMinutes = editing?.IntervalMinutes ?? 5;
        OnLocalChange = editing?.OnLocalChange ?? true;
        MaxDeletePercent = editing?.MaxDeletePercent ?? 50;
        if (editing is not null)
        {
            Account = Accounts.FirstOrDefault(a => a.Id == editing.AccountId);
            var name = editing.RemotePath.Length == 0 ? "Alles" : editing.RemotePath.Split('/')[^1];
            ChosenFolder = new FolderNode(name, editing.RemotePath, true, 0, null, null);
            SelectAll = editing.Selection.Mode == SelectionMode.All;
            _originalIncludes.AddRange(editing.Selection.Include);
            SetLocal(editing.LocalPath);
            Step = AddSyncStep.Selection;
        }
        else
        {
            Account = Accounts.FirstOrDefault(a => a.Id == accountId) ?? Accounts.FirstOrDefault();
        }
    }

    public bool IsEditing => _editing is not null;
    public ObservableCollection<AccountSettings> Accounts { get; } = [];
    public bool ShowAccountChoice => !IsEditing && Accounts.Count > 1;
    public ObservableCollection<FolderNode> FolderRoots { get; } = [];
    public ObservableCollection<FolderNode> SelectionRoots { get; } = [];
    public ObservableCollection<FolderWarning> Warnings { get; } = [];

    [ObservableProperty] public partial AddSyncStep Step { get; set; }
    [ObservableProperty] public partial AccountSettings? Account { get; set; }
    [ObservableProperty] public partial FolderNode? SelectedFolder { get; set; }
    [ObservableProperty] public partial FolderNode? ChosenFolder { get; set; }
    [ObservableProperty] public partial bool SelectAll { get; set; } = true;
    [ObservableProperty] public partial bool SelectionLoading { get; set; }
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
    [ObservableProperty] public partial bool NotEnoughSpace { get; set; }
    [ObservableProperty] public partial bool AcceptSpace { get; set; }

    public SyncPairSettings? Result { get; private set; }
    public bool Saved { get; private set; }

    public event EventHandler? CloseRequested;

    public IReadOnlyList<Choice<int>> IntervalChoices { get; } =
    [
        new(1, "jede Minute"), new(5, "alle 5 Minuten"), new(15, "alle 15 Minuten"), new(30, "alle 30 Minuten"), new(60, "jede Stunde"),
        new(240, "alle 4 Stunden"),
    ];

    public IReadOnlyList<Choice<int>> DeleteChoices { get; } =
    [
        new(10, "mehr als 10 % der Dateien gelöscht würden"), new(25, "mehr als 25 % der Dateien gelöscht würden"),
        new(50, "mehr als die Hälfte der Dateien gelöscht würde (empfohlen)"), new(75, "mehr als 75 % der Dateien gelöscht würden"),
        new(100, "nie – Löschungen immer übernehmen"),
    ];

    public string Heading => IsEditing ? "Synchronisation ändern" : "Ordner synchronisieren";

    public string StepTitle => Step switch
    {
        AddSyncStep.Folder => "Welcher Cloud-Ordner?",
        AddSyncStep.Selection => "Was davon?",
        AddSyncStep.Local => "Wo auf diesem PC?",
        AddSyncStep.Options => "Wie synchronisieren?",
        _ => IsEditing ? "Änderungen speichern" : "Bereit",
    };

    public string StepText
    {
        get
        {
            var steps = IsEditing ? new[] { AddSyncStep.Selection, AddSyncStep.Options, AddSyncStep.Summary } : Enum.GetValues<AddSyncStep>();
            return $"Schritt {Array.IndexOf(steps, Step) + 1} von {steps.Length}";
        }
    }

    public bool CanGoBack => !IsBusy && (IsEditing ? Step != AddSyncStep.Selection : Step != AddSyncStep.Folder);
    public string PrimaryText => Step == AddSyncStep.Summary ? (IsEditing ? "Speichern" : "Synchronisation starten") : "Weiter";
    public bool IsLastStep => Step == AddSyncStep.Summary;
    public bool HasWarnings => Warnings.Count > 0;
    public bool HasError => Error.Length > 0;
    public bool HasLocalError => LocalError.Length > 0;
    public bool CloudWithoutTimes => Account is { Kind: not WebDavKind.Nextcloud };

    public string ChosenFolderText => ChosenFolder is null ? "" : $"{Account?.Label} › {(ChosenFolder.Path.Length == 0 ? "Alles" : ChosenFolder.Path.Replace("/", " › "))}";

    public string SummarySelection => SelectAll ? "Alles in diesem Ordner" : Format.Count(CollectIncludes().Count, "ausgewähltes Element", "ausgewählte Elemente");

    public string SummaryOptions =>
        $"{ConflictTitle(ConflictPolicy)} · {IntervalChoices.FirstOrDefault(c => c.Value == IntervalMinutes)?.Title ?? $"alle {IntervalMinutes} Minuten"}" +
        (OnLocalChange ? " · Änderungen am PC sofort" : "") +
        (MaxDeletePercent >= 100 ? " · ohne Löschschutz" : $" · Löschschutz ab {MaxDeletePercent} %");

    partial void OnStepChanged(AddSyncStep value)
    {
        Error = "";
        foreach (var name in new[] { nameof(StepTitle), nameof(StepText), nameof(CanGoBack), nameof(PrimaryText), nameof(IsLastStep), nameof(SummarySelection), nameof(SummaryOptions), nameof(ChosenFolderText) })
            OnPropertyChanged(name);
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanGoBack));

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnLocalErrorChanged(string value) => OnPropertyChanged(nameof(HasLocalError));

    partial void OnAccountChanged(AccountSettings? value)
    {
        OnPropertyChanged(nameof(CloudWithoutTimes));
        if (IsEditing || value is null) return;
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
        CheckLocal(showErrors: false);
    }

    /// <summary>Called when the window has opened.</summary>
    public async Task InitializeAsync()
    {
        if (IsEditing) await LoadSelectionAsync();
    }

    private async Task<IReadOnlyList<FolderNode>> LoadFoldersAsync(FolderNode parent)
    {
        var entries = await _host.Accounts.ListAsync(Account!.Id, parent.Path, includeFiles: false);
        return entries.Select(e => new FolderNode(e.Name, e.Path, true, 0, parent, LoadFoldersAsync)).ToList();
    }

    private async Task<IReadOnlyList<FolderNode>> LoadSelectionChildrenAsync(FolderNode parent)
    {
        var entries = await _host.Accounts.ListAsync(Account!.Id, parent.Path, includeFiles: true);
        return entries.Select(e => Node(e, parent)).ToList();
    }

    private FolderNode Node(RemoteEntry entry, FolderNode? parent) =>
        new(entry.Name, entry.Path, entry.IsDirectory, entry.Size, parent, entry.IsDirectory ? LoadSelectionChildrenAsync : null, InitialCheck(entry, parent));

    /// <summary>Ticks as they were (when changing a synchronisation); inside a ticked folder everything is ticked.</summary>
    private bool? InitialCheck(RemoteEntry entry, FolderNode? parent)
    {
        if (parent?.IsChecked is bool inherited) return inherited;
        var relative = Relative(entry.Path);
        if (!entry.IsDirectory) return _originalIncludes.Contains(relative, StringComparer.OrdinalIgnoreCase);
        if (_originalIncludes.Contains(relative + "/", StringComparer.OrdinalIgnoreCase)) return true;
        return _originalIncludes.Any(i => i.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)) ? null : false;
    }

    private string BasePath => ChosenFolder?.Path ?? "";

    private string Relative(string path) =>
        BasePath.Length == 0 ? path : path.StartsWith(BasePath + "/", StringComparison.Ordinal) ? path[(BasePath.Length + 1)..] : path;

    private async Task LoadSelectionAsync()
    {
        SelectionRoots.Clear();
        SelectionLoading = true;
        try
        {
            var entries = await _host.Accounts.ListAsync(Account!.Id, BasePath, includeFiles: true);
            foreach (var entry in entries) SelectionRoots.Add(Node(entry, null));
        }
        catch (CdException e)
        {
            Error = $"Der Ordner konnte nicht gelesen werden: {ErrorCatalog.Get(e.Code).Title}";
        }
        finally
        {
            SelectionLoading = false;
        }
    }

    /// <summary>The ticked entries as the synchronisation's include list (folders end with "/").</summary>
    private List<string> CollectIncludes()
    {
        var result = new List<string>();
        void Walk(IEnumerable<FolderNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsPlaceholder) continue;
                var relative = Relative(node.Path);
                if (node.IsChecked == true) result.Add(node.IsDirectory ? relative + "/" : relative);
                else if (node.IsChecked is null)
                {
                    if (node.IsLoaded) Walk(node.Children);
                    else result.AddRange(_originalIncludes.Where(i => i.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)));
                }
            }
        }
        Walk(SelectionRoots);
        return result;
    }

    private SyncSelection BuildSelection() => new()
    {
        Mode = SelectAll ? SelectionMode.All : SelectionMode.Selected,
        Include = SelectAll ? [] : CollectIncludes(),
        Exclude = _editing?.Selection.Exclude.ToList() ?? [],
    };

    private SyncPairSettings BuildDraft() => new()
    {
        AccountId = Account!.Id,
        RemotePath = BasePath,
        LocalPath = LocalPath.Trim(),
        Selection = BuildSelection(),
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
        static string Clean(string name) =>
            string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudDrive-Sync", Clean(Account?.Label ?? "Cloud"));
        return ChosenFolder is null || ChosenFolder.Path.Length == 0 ? root : Path.Combine(root, Clean(ChosenFolder.Name));
    }

    /// <summary>Checks the local folder; warnings appear at once, errors only when going on.</summary>
    private bool CheckLocal(bool showErrors)
    {
        Warnings.Clear();
        AcceptWarnings = false;
        try
        {
            foreach (var warning in _host.Sync.CheckFolder(LocalPath.Trim(), _editing?.Id)) Warnings.Add(warning);
            LocalError = "";
            return true;
        }
        catch (CdException)
        {
            LocalError = showErrors || LocalPath.Trim().Length > 0
                ? "Bitte gib einen vollständigen Ordnerpfad auf einem verfügbaren Laufwerk an, z. B. C:\\Users\\Name\\CloudDrive-Sync."
                : "";
            return false;
        }
        finally
        {
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
        Error = "";
        Step = Step switch
        {
            AddSyncStep.Selection => AddSyncStep.Folder,
            AddSyncStep.Local => AddSyncStep.Selection,
            AddSyncStep.Options => IsEditing ? AddSyncStep.Selection : AddSyncStep.Local,
            AddSyncStep.Summary => AddSyncStep.Options,
            _ => Step,
        };
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
                if (ChosenFolder?.Path != SelectedFolder.Path || SelectionRoots.Count == 0)
                {
                    ChosenFolder = SelectedFolder;
                    _originalIncludes.Clear();
                    await LoadSelectionAsync();
                }
                if (!_localTyped) SetLocal(DefaultLocalPath());
                Step = AddSyncStep.Selection;
                break;
            case AddSyncStep.Selection:
                if (!SelectAll && CollectIncludes().Count == 0)
                {
                    Error = "Bitte setze bei mindestens einem Ordner oder einer Datei einen Haken – oder wähle „Alles“.";
                    return;
                }
                Step = IsEditing ? AddSyncStep.Options : AddSyncStep.Local;
                if (!IsEditing) CheckLocal(showErrors: false);
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

    private async Task PreviewAsync()
    {
        PreviewText = "Größe wird ermittelt …";
        NotEnoughSpace = false;
        AcceptSpace = false;
        IsBusy = true;
        try
        {
            var draft = BuildDraft();
            if (IsEditing) draft.LocalPath = _editing!.LocalPath;
            var preview = await _host.Sync.PreviewAsync(draft);
            var lines = new List<string>
            {
                $"In der Cloud: {Format.Count(preview.CloudFiles, "Datei", "Dateien")} · {Format.Bytes(preview.CloudBytes)}",
                preview.LocalFiles > 0
                    ? $"Schon am PC: {Format.Count(preview.LocalFiles, "Datei", "Dateien")} · {Format.Bytes(preview.LocalBytes)} – wird zusammengeführt, nichts wird gelöscht"
                    : "Am PC: noch leer",
            };
            if (preview.FreeBytes > 0) lines.Add($"Frei auf dem Laufwerk: {Format.Bytes(preview.FreeBytes)}");
            PreviewText = string.Join(Environment.NewLine, lines);
            NotEnoughSpace = preview.FreeBytes > 0 && !preview.EnoughSpace;
        }
        catch (CdException e)
        {
            PreviewText = $"Die Größe konnte nicht ermittelt werden ({ErrorCatalog.Get(e.Code).Title}). Du kannst trotzdem starten.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task FinishAsync()
    {
        if (NotEnoughSpace && !AcceptSpace)
        {
            Error = "Der freie Speicher reicht voraussichtlich nicht. Bestätige, dass du trotzdem starten möchtest – oder wähle weniger aus.";
            return;
        }
        IsBusy = true;
        try
        {
            if (_editing is not null)
            {
                var selection = BuildSelection();
                _host.Sync.Update(_editing.Id, pair =>
                {
                    pair.Selection = selection;
                    pair.Conflicts = ConflictPolicy;
                    pair.IntervalMinutes = IntervalMinutes;
                    pair.OnLocalChange = OnLocalChange;
                    pair.MaxDeletePercent = MaxDeletePercent;
                });
                Saved = true;
            }
            else
            {
                Result = await _host.Sync.AddAsync(BuildDraft());
            }
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

    public static string ConflictTitle(ConflictPolicy policy) => policy switch
    {
        ConflictPolicy.KeepBoth => "Konflikte: beide umbenennen",
        ConflictPolicy.CloudWins => "Konflikte: Cloud gewinnt",
        ConflictPolicy.PcWins => "Konflikte: PC gewinnt",
        _ => "Konflikte: neuere gewinnt",
    };
}
