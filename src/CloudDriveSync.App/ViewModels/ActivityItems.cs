using System.IO;
using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Sync;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

/// <summary>One finished run in the activity list.</summary>
public sealed class ActivityItem
{
    public ActivityItem(string pairTitle, SyncRunRecord run)
    {
        Started = run.Started;
        PairTitle = pairTitle;
        Time = Format.Time(run.Started);
        Tone = run.Success ? Tone.Ok : run.ErrorCode is "CD-4502" or "CD-4503" or "CD-4504" or "CD-4509" or "CD-3012" ? Tone.Warning : Tone.Error;
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
    }

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
