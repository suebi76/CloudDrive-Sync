using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

/// <summary>
/// Ending a synchronisation - or removing an account with all of its synchronisations: first the question what stays
/// on this PC, then the work with its progress. When the folder is to leave the PC and files stayed that exist only
/// here, the window names them and lets them go, too, once the user says so - nothing is left behind unasked.
/// </summary>
public sealed partial class EndSyncViewModel : ObservableObject
{
    private readonly CloudDriveSyncHost _host;
    private readonly IReadOnlyList<SyncPairSettings> _pairs;
    private readonly string? _accountId;

    // The folders of ended synchronisations in which files stayed although nothing was to stay.
    private readonly List<(string Folder, IReadOnlyList<string> Stayed)> _rest = [];

    /// <param name="accountId">Set when the account goes, too - after its synchronisations.</param>
    public EndSyncViewModel(CloudDriveSyncHost host, IReadOnlyList<SyncPairSettings> pairs, string heading, string intro, string primaryText, string? accountId = null)
    {
        _host = host;
        _pairs = pairs;
        _accountId = accountId;
        Heading = heading;
        Intro = intro;
        PrimaryText = primaryText;
        HasOnDemand = pairs.Any(p => p.Mode == SyncMode.OnDemand);
        var online = pairs.Where(p => p.Mode == SyncMode.OnDemand).Select(p => host.Sync.GetState(p.Id)?.Space).OfType<SpaceUse>().Sum(s => Math.Max(0, s.CloudBytes - s.OnPcBytes));
        EverythingText = "Lädt vorher alle Dateien herunter, die nur online liegen" + (online > 0 ? $" (etwa {Format.Bytes(online)})" : "") +
            ". Danach liegt eine vollständige Kopie auf diesem PC; reicht der Platz nicht, bleibt die Synchronisation bestehen.";
    }

    public string Heading { get; }
    public string Intro { get; }
    public bool HasPairs => _pairs.Count > 0;
    public bool HasOnDemand { get; }

    public string KeepTitle => HasOnDemand ? "Heruntergeladene Dateien behalten" : "Dateien auf diesem PC behalten";

    public string KeepText => HasOnDemand
        ? "Dateien, die schon auf diesem PC liegen, bleiben als normale Dateien. Was nur online lag, verschwindet vom PC – in der Cloud bleibt es."
        : "Der Ordner bleibt, wie er ist; er wird nur nicht mehr abgeglichen.";

    public string EverythingText { get; }

    public string NothingText =>
        "Erst wird alles hochgeladen und geprüft, dann kommt der Ordner mit allem darin in den Papierkorb von Windows; in der Cloud bleibt alles. " +
        "Gibt es Dateien nur auf diesem PC, fragt CloudDrive-Sync vorher. Klappt das Hochladen nicht, bleibt die Synchronisation bestehen.";

    [ObservableProperty] public partial bool KeepOnPc { get; set; } = true;
    [ObservableProperty] public partial bool KeepEverything { get; set; }
    [ObservableProperty] public partial bool KeepNothing { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChoose)), NotifyCanExecuteChangedFor(nameof(EndCommand)), NotifyCanExecuteChangedFor(nameof(CloseCommand)),
     NotifyCanExecuteChangedFor(nameof(RemoveRestCommand))]
    public partial bool IsWorking { get; set; }

    [ObservableProperty] public partial string Progress { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] public partial string Error { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasResult)), NotifyPropertyChangedFor(nameof(CanChoose)), NotifyPropertyChangedFor(nameof(CloseText)),
     NotifyCanExecuteChangedFor(nameof(EndCommand))]
    public partial string Result { get; set; } = "";

    /// <summary>Files stayed that may go, too - the window asks.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CloseText)), NotifyCanExecuteChangedFor(nameof(RemoveRestCommand))]
    public partial bool HasRest { get; set; }

    public string PrimaryText { get; }

    public string CloseText => HasRest ? "Behalten" : HasResult ? "Schließen" : "Abbrechen";

    public bool CanChoose => !IsWorking && !HasResult;
    public bool HasError => Error.Length > 0;
    public bool HasResult => Result.Length > 0;

    /// <summary>Something was ended or removed - the overview shows what is left.</summary>
    public bool Changed { get; private set; }

    public event EventHandler? CloseRequested;

    private bool CanEnd => !IsWorking && !HasResult;

    [RelayCommand(CanExecute = nameof(CanEnd))]
    private async Task EndAsync()
    {
        Error = "";
        IsWorking = true;
        var keep = KeepNothing ? Core.Sync.KeepOnPc.Nothing : KeepEverything && HasOnDemand ? Core.Sync.KeepOnPc.Everything : Core.Sync.KeepOnPc.OnPc;
        var progress = new Progress<string>(text => Progress = text);
        try
        {
            var held = false;
            foreach (var pair in _pairs)
            {
                var result = await _host.Sync.RemoveAsync(pair.Id, keep, progress);
                Changed = true;
                held |= result.StillHeld;
                if (result.Stayed.Count > 0) _rest.Add((pair.LocalPath, result.Stayed));
            }
            if (_accountId is not null)
            {
                Progress = "Entfernt die Anmeldung …";
                await _host.Accounts.RemoveAsync(_accountId);
                Changed = true;
            }
            if (_rest.Count == 0 && !held)
            {
                // Done: the window closes - it may now, the work is over.
                IsWorking = false;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            var text = held
                ? "Ein Programm hält gerade noch Dateien im Ordner geöffnet (z. B. ein Explorer-Fenster oder Office). Sobald es sie freigibt, " +
                  "räumt CloudDrive-Sync den Ordner von selbst fertig auf – auch nach einem Neustart. "
                : "";
            if (_rest.Count > 0)
            {
                var stayed = _rest.SelectMany(r => r.Stayed).ToList();
                var one = stayed.Count == 1;
                text += (one
                        ? "Eine Datei gibt es nur auf diesem PC (z. B. eine Sperrdatei von Office), oder sie wurde gerade geändert: "
                        : $"{Format.Count(stayed.Count, "Datei", "Dateien")} gibt es nur auf diesem PC (z. B. Sperrdateien von Office), oder sie wurden gerade geändert: ") +
                    Names(stayed) + (one ? ". Soll sie auch in den Papierkorb?" : ". Sollen sie auch in den Papierkorb?") + " Dann ist der Ordner ganz weg.";
                HasRest = true;
            }
            Result = text.TrimEnd();
        }
        catch (CdException e)
        {
            var entry = ErrorCatalog.Get(e.Code);
            Error = $"{entry.Title}: {entry.Fix}";
        }
        finally
        {
            IsWorking = false;
            Progress = "";
        }
    }

    /// <summary>What stayed goes into the recycle bin, too; the folders go with it.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveRest))]
    private async Task RemoveRestAsync()
    {
        Error = "";
        IsWorking = true;
        var progress = new Progress<string>(text => Progress = text);
        try
        {
            var left = new List<(string Folder, IReadOnlyList<string> Stayed)>();
            foreach (var (folder, _) in _rest)
            {
                var result = await _host.Sync.RecycleRestAsync(folder, progress);
                if (result.Stayed.Count > 0) left.Add((folder, result.Stayed));
            }
            _rest.Clear();
            _rest.AddRange(left);
            if (_rest.Count == 0)
            {
                IsWorking = false;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            var stayed = _rest.SelectMany(r => r.Stayed).ToList();
            Result = (stayed.Count == 1
                    ? "Eine Datei ist trotzdem geblieben – ein Programm hält sie offen, oder sie war zu groß für den Papierkorb und sollte bleiben: "
                    : $"{Format.Count(stayed.Count, "Datei", "Dateien")} sind trotzdem geblieben – ein Programm hält sie offen, oder sie waren zu groß für den Papierkorb und sollten bleiben: ") +
                Names(stayed) + ". Schließe das Programm und versuche es noch einmal.";
        }
        catch (CdException e)
        {
            var entry = ErrorCatalog.Get(e.Code);
            Error = $"{entry.Title}: {entry.Fix}";
        }
        finally
        {
            IsWorking = false;
            Progress = "";
        }
    }

    private bool CanRemoveRest => !IsWorking && HasRest;

    private static string Names(List<string> files) => string.Join(", ", files.Take(5)) + (files.Count > 5 ? " …" : "");

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>While the work runs, the window stays - it says when it is done.</summary>
    private bool CanClose => !IsWorking;
}
