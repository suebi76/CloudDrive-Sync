using System.Collections.ObjectModel;
using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

/// <summary>Files of one kind of difference, e.g. "Nur in der Cloud".</summary>
public sealed record DifferenceGroup(string Title, string Hint, IReadOnlyList<string> Files)
{
    private const int Shown = 500;

    public string Header => $"{Title} ({Format.Number(Files.Count)})";
    public string List => string.Join(Environment.NewLine, Files.Take(Shown).Select(f => f.Replace("/", " › "))) + (Files.Count > Shown ? Environment.NewLine + "…" : "");
}

/// <summary>
/// "Abgleich überprüfen": compares the PC and the cloud side of a synchronisation file by file and shows what differs.
/// Nothing is changed by it.
/// </summary>
public sealed partial class VerifyViewModel : ObservableObject
{
    private readonly CloudDriveSyncHost _host;
    private readonly string _id;
    private CancellationTokenSource? _cancel;

    public VerifyViewModel(CloudDriveSyncHost host, SyncPairSettings pair, string title)
    {
        _host = host;
        _id = pair.Id;
        Title = title;
    }

    public string Title { get; }
    public ObservableCollection<DifferenceGroup> Groups { get; } = [];

    [ObservableProperty] public partial bool CompareContent { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; } = "";
    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial Tone Tone { get; set; }
    [ObservableProperty] public partial string ResultGlyph { get; set; } = Glyphs.Done;
    [ObservableProperty] public partial string ResultTitle { get; set; } = "";
    [ObservableProperty] public partial string ResultText { get; set; } = "";
    [ObservableProperty] public partial bool CanSyncNow { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = "";

    public bool HasError => Error.Length > 0;
    public bool CanCheck => !IsRunning;

    public event EventHandler? CloseRequested;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanCheck));

    /// <summary>Called when the window closes: a check still running is stopped.</summary>
    public void Cancel() => _cancel?.Cancel();

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        HasResult = false;
        CanSyncNow = false;
        Error = "";
        Groups.Clear();
        ProgressText = "Vergleicht PC und Cloud …";
        _cancel = new CancellationTokenSource();
        // Created here, so progress arrives on the window's thread.
        IProgress<JobProgress> progress = new Progress<JobProgress>(p =>
            ProgressText = p.Checks > 0 ? $"{Format.Count(p.Checks, "Datei", "Dateien")} verglichen …" : "Vergleicht PC und Cloud …");
        try
        {
            Show(await _host.Sync.VerifyAsync(_id, CompareContent, progress.Report, _cancel.Token));
        }
        catch (CdException e)
        {
            var entry = ErrorCatalog.Get(e.Code);
            Error = $"{entry.Title}: {entry.Fix}";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void SyncNow()
    {
        _host.Sync.RunNow(_id);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Show(VerifyResult result)
    {
        HasResult = true;
        if (result.InStep)
        {
            Tone = Tone.Ok;
            ResultGlyph = Glyphs.Done;
            ResultTitle = "Alles stimmt überein";
            var files = Format.Count(result.Matching, "Datei ist", "Dateien sind");
            ResultText = result.ComparedContent
                ? $"{files} auf beiden Seiten gleich – auch im Inhalt."
                : $"{files} auf beiden Seiten gleich. Verglichen wurden Namen und Größen und, wo der Server es kann, Prüfsummen.";
            return;
        }
        Tone = Tone.Warning;
        ResultGlyph = Glyphs.Warning;
        ResultTitle = result.Differences == 1 ? "1 Abweichung" : $"{Format.Number(result.Differences)} Abweichungen";
        ResultText = "Abweichungen können auch Änderungen sein, die gerade noch übertragen werden. Synchronisiere jetzt und prüfe danach erneut.";
        CanSyncNow = true;
        Add("Nur auf diesem PC", "kommt beim nächsten Abgleich in die Cloud – oder wurde dort gelöscht", result.OnlyOnPc);
        Add("Nur in der Cloud", "kommt beim nächsten Abgleich auf den PC – oder wurde hier gelöscht", result.OnlyInCloud);
        Add("Unterschiedlich", "Größe oder Inhalt weichen ab", result.Different);
        Add("Nicht lesbar", "konnte nicht gelesen werden, z. B. weil sie gerade geöffnet ist", result.Unreadable);
    }

    private void Add(string title, string hint, IReadOnlyList<string> files)
    {
        if (files.Count > 0) Groups.Add(new DifferenceGroup(title, hint, files));
    }
}
