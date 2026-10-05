using System.Collections.ObjectModel;
using System.IO;
using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

/// <summary>
/// The recycle bin page: per synchronisation (account › folder) the files the synchronisation deleted or replaced on
/// the PC, to restore or to delete for good.
/// </summary>
public sealed partial class TrashViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<TrashGroupViewModel> Groups { get; } = [];

    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial string RetentionText { get; set; } = "";

    public MainViewModel Main => main;

    public void Refresh()
    {
        var days = main.Host.Settings.Current.Preferences.TrashDays;
        RetentionText = days <= 0
            ? "Der Papierkorb ist ausgeschaltet: Was der Abgleich jetzt löscht oder ersetzt, wird nicht aufbewahrt. Einschalten kannst du ihn in den Einstellungen."
            : $"Dateien bleiben {Format.Count(days, "Tag", "Tage")} im Papierkorb, danach werden sie endgültig gelöscht (änderbar in den Einstellungen).";
        Groups.Clear();
        foreach (var pair in main.Pairs)
        {
            var group = new TrashGroupViewModel(this, pair, main.Host.Sync.Trash(pair.Id));
            Groups.Add(group);
            _ = group.CheckCloudAsync();
        }
        UpdateEmpty();
    }

    public void UpdateEmpty() => IsEmpty = Groups.All(g => !g.IsVisible);
}

/// <summary>The recycle bin of one synchronisation.</summary>
public sealed partial class TrashGroupViewModel : ObservableObject
{
    private const int MaxShown = 200;
    private readonly TrashViewModel _page;
    private readonly SyncPairViewModel _pair;

    public TrashGroupViewModel(TrashViewModel page, SyncPairViewModel pair, IReadOnlyList<TrashEntry> entries)
    {
        _page = page;
        _pair = pair;
        Title = pair.Title;
        foreach (var entry in entries.Take(MaxShown)) Items.Add(new TrashItemViewModel(this, entry));
        Count = entries.Count;
        Bytes = entries.Sum(e => e.Size);
        Subtitle = Count == 0 ? "leer" : $"{Format.Count(Count, "Datei", "Dateien")} · {Format.Bytes(Bytes)}";
        MoreText = Count > MaxShown ? $"… und {Format.Number(Count - MaxShown)} weitere – alle siehst du über „Ordner öffnen“." : "";
    }

    public string Title { get; }
    public string Subtitle { get; private set; }
    public string MoreText { get; }
    public int Count { get; private set; }
    public long Bytes { get; private set; }
    public ObservableCollection<TrashItemViewModel> Items { get; } = [];
    public bool HasItems => Items.Count > 0;
    public bool IsVisible => HasItems || HasCloudLeftover;
    internal SyncPairViewModel Pair => _pair;
    internal MainViewModel Main => _page.Main;

    [ObservableProperty] public partial bool HasCloudLeftover { get; set; }
    [ObservableProperty] public partial string CloudLeftoverText { get; set; } = "";

    partial void OnHasCloudLeftoverChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVisible));
        _page.UpdateEmpty();
    }

    /// <summary>Looks for the recycle bin folder that version 0.1.0 kept in the cloud folder.</summary>
    public async Task CheckCloudAsync()
    {
        if (await Main.Host.Sync.GetCloudTrashAsync(_pair.Id) is not { } leftover) return;
        CloudLeftoverText = $"Auf dem Server liegt noch der Ordner „.clouddrive-papierkorb“ aus der Vorversion: {Format.Count(leftover.Files, "Datei", "Dateien")} · {Format.Bytes(leftover.Bytes)}. Er ist dort für jeden sichtbar, der den Ordner sieht.";
        HasCloudLeftover = true;
    }

    internal void Removed(TrashItemViewModel item)
    {
        Items.Remove(item);
        Count--;
        Bytes -= item.Entry.Size;
        Subtitle = Count <= 0 ? "leer" : $"{Format.Count(Count, "Datei", "Dateien")} · {Format.Bytes(Bytes)}";
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsVisible));
        _page.UpdateEmpty();
    }

    [RelayCommand]
    private void OpenFolder() => Shell.OpenFolder(SyncTrash.FolderOf(_pair.LocalPath));

    [RelayCommand]
    private void Empty()
    {
        var answer = Main.Dialogs.Ask("Papierkorb leeren?",
            $"{Format.Count(Count, "Datei", "Dateien")} im Papierkorb von „{Title}“ werden endgültig gelöscht.", "Leeren", danger: true);
        if (answer != DialogChoice.Primary) return;
        try
        {
            SyncTrash.Empty(_pair.LocalPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Main.Dialogs.Ask("Nicht alles gelöscht", $"Eine Datei ist noch geöffnet oder geschützt: {e.Message}", "OK", close: "");
        }
        _page.Refresh();
    }

    [RelayCommand]
    private async Task RemoveCloudLeftoverAsync()
    {
        var answer = Main.Dialogs.Ask("Papierkorb-Ordner auf dem Server löschen?",
            $"{CloudLeftoverText.Split(" Er ist dort")[0]} Der Ordner wird mit seinem ganzen Inhalt auf dem Server gelöscht.", "Auf dem Server löschen", danger: true);
        if (answer != DialogChoice.Primary) return;
        await Main.RunGuardedAsync(() => Main.Host.Sync.RemoveCloudTrashAsync(_pair.Id));
        HasCloudLeftover = await Main.Host.Sync.GetCloudTrashAsync(_pair.Id) is not null;
    }
}

/// <summary>One file in the recycle bin.</summary>
public sealed partial class TrashItemViewModel
{
    private readonly TrashGroupViewModel _group;

    public TrashItemViewModel(TrashGroupViewModel group, TrashEntry entry)
    {
        _group = group;
        Entry = entry;
        Name = Path.GetFileName(entry.RelativePath);
        var folder = Path.GetDirectoryName(entry.RelativePath);
        Details = $"{(string.IsNullOrEmpty(folder) ? "im Hauptordner" : "in " + folder.Replace("\\", " › "))} · entfernt {Format.Ago(entry.Removed)} · {Format.Bytes(entry.Size)}";
    }

    public TrashEntry Entry { get; }
    public string Name { get; }
    public string Details { get; }

    /// <summary>Back to where it was; the next synchronisation brings it to the cloud again.</summary>
    [RelayCommand]
    private void Restore()
    {
        try
        {
            SyncTrash.Restore(_group.Pair.LocalPath, Entry);
            _group.Removed(this);
            _group.Main.Host.Sync.RunNow(_group.Pair.Id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _group.Main.Dialogs.Ask("Wiederherstellen nicht möglich", e.Message, "OK", close: "");
        }
    }

    [RelayCommand]
    private void ShowInFolder() => Shell.ShowInFolder(Entry.FullPath);

    [RelayCommand]
    private void Delete()
    {
        var answer = _group.Main.Dialogs.Ask("Endgültig löschen?", $"„{Name}“ wird endgültig gelöscht.", "Löschen", danger: true);
        if (answer != DialogChoice.Primary) return;
        try
        {
            SyncTrash.Delete(_group.Pair.LocalPath, Entry);
            _group.Removed(this);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _group.Main.Dialogs.Ask("Löschen nicht möglich", e.Message, "OK", close: "");
        }
    }
}
