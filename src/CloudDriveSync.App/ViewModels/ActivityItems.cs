using System.IO;
using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Sync;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

/// <summary>One file of a run in the activity list: what happened to it, and where it is.</summary>
public sealed partial class ChangeRow
{
    public ChangeRow(FileChange change, string localPath, bool trashKept)
    {
        Shown = change.Path.Replace("/", " › ");
        FullPath = System.IO.Path.Combine(localPath, change.Path.Replace('/', '\\'));
        (Glyph, What) = change.Kind switch
        {
            ChangeKind.Uploaded => (Glyphs.Upload, "hochgeladen"),
            ChangeKind.Downloaded => (Glyphs.Download, "auf den PC geholt"),
            ChangeKind.DeletedInCloud => (Glyphs.Trash, "in der Cloud gelöscht"),
            ChangeKind.DeletedOnPc => (Glyphs.Trash, trashKept ? "am PC gelöscht – im Papierkorb" : "am PC gelöscht"),
            _ => (Glyphs.Warning, "bleibt nur auf diesem PC"),
        };
        CanShow = change.Kind is not (ChangeKind.DeletedInCloud or ChangeKind.DeletedOnPc);
    }

    public string Shown { get; }
    public string What { get; }
    public string Glyph { get; }
    public string FullPath { get; }
    public bool CanShow { get; }

    [RelayCommand]
    private void Show() => Shell.ShowInFolder(FullPath);
}

/// <summary>One finished run in the activity list.</summary>
public sealed class ActivityItem
{
    public ActivityItem(string pairTitle, string localPath, SyncRunRecord run, bool trashKept)
    {
        Started = run.Started;
        PairTitle = pairTitle;
        Time = Format.Time(run.Started);
        Tone = run.Success ? Tone.Ok : run.ErrorCode is "CD-4502" or "CD-4503" or "CD-4504" or "CD-4509" or "CD-4510" or "CD-4511" or "CD-4512" or "CD-3012" ? Tone.Warning : Tone.Error;
        Glyph = run.Success ? Glyphs.Done : Tone == Tone.Warning ? Glyphs.Warning : Glyphs.Error;
        if (run.Success)
        {
            var parts = new List<string>();
            if (run.Transfers > 0) parts.Add($"{Format.Count(run.Transfers, "Datei", "Dateien")} übertragen ({Format.Bytes(run.Bytes)})");
            if (run.Deletes > 0) parts.Add($"{Format.Number(run.Deletes)} gelöscht");
            if (run.Conflicts > 0) parts.Add(Format.Count(run.Conflicts, "Konflikt", "Konflikte"));
            Summary = parts.Count == 0 ? "Keine Änderungen" : string.Join(" · ", parts);
        }
        else
        {
            Summary = ErrorCatalog.Get(run.ErrorCode ?? "CD-9000").Title;
        }
        Title = $"{run.Kind} · {Format.Duration(run.Duration)}";
        Changes = (run.Changes ?? []).Select(change => new ChangeRow(change, localPath, trashKept)).ToList();
        var count = Changes.Count + run.MoreChanges;
        ChangesHeader = run.MoreChanges > 0
            ? $"{Format.Count(count, "Datei", "Dateien")} – die ersten {Format.Number(Changes.Count)} anzeigen"
            : $"{Format.Count(count, "Datei", "Dateien")} anzeigen";
    }

    public IReadOnlyList<ChangeRow> Changes { get; }
    public bool HasChanges => Changes.Count > 0;
    public string ChangesHeader { get; }

    public DateTimeOffset Started { get; }
    public string PairTitle { get; }
    public string Title { get; }
    public string Summary { get; }
    public string Time { get; }
    public string Glyph { get; }
    public Tone Tone { get; }
}

/// <summary>A conflict copy: both versions of a file are kept, the user decides.</summary>
public sealed partial class ConflictItem
{
    public ConflictItem(string pairTitle, string folder, string relativePath)
    {
        PairTitle = pairTitle;
        RelativePath = relativePath;
        FullPath = Path.Combine(folder, relativePath);
        Name = Path.GetFileName(relativePath);
        var parent = Path.GetDirectoryName(relativePath);
        Location = string.IsNullOrEmpty(parent) ? pairTitle : $"{pairTitle} › {parent.Replace("\\", " › ")}";
    }

    public string PairTitle { get; }
    public string RelativePath { get; }
    public string FullPath { get; }
    public string Name { get; }
    public string Location { get; }

    [RelayCommand]
    private void Open() => Shell.OpenFile(FullPath);

    [RelayCommand]
    private void ShowInFolder() => Shell.ShowInFolder(FullPath);
}
