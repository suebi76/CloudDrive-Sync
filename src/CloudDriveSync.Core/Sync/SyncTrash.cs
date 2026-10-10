using System.Globalization;

namespace CloudDriveSync.Core.Sync;

/// <summary>A file in the recycle bin of a synchronisation.</summary>
/// <param name="RelativePath">Where the file was, relative to the synchronised folder ("\" separated).</param>
public sealed record TrashEntry(string PairId, string RelativePath, string FullPath, DateTimeOffset Removed, long Size);

/// <summary>
/// The recycle bin of a synchronisation: what the synchronisation deleted or replaced on the PC (because it was deleted
/// or changed in the cloud) waits in the hidden folder ".clouddrive-papierkorb\&lt;date and time&gt;\" of the synchronised
/// folder. Files the user deletes on the PC are in the Windows recycle bin instead.
/// </summary>
public static class SyncTrash
{
    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    public static string FolderOf(string localPath) => Path.Combine(localPath, SyncFilters.TrashFolder);

    /// <summary>The name of a recycle bin folder for a run started now.</summary>
    public static string NewStamp(DateTimeOffset? now = null) => (now ?? DateTimeOffset.Now).ToString(StampFormat, CultureInfo.InvariantCulture);

    /// <summary>All files in the recycle bin, newest first.</summary>
    public static IReadOnlyList<TrashEntry> List(string pairId, string localPath)
    {
        var root = Path.Combine(localPath, SyncFilters.TrashFolder);
        if (!Directory.Exists(root)) return [];
        var entries = new List<TrashEntry>();
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(folder), StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var removed)) continue;
            try
            {
                foreach (var file in FolderWalk.Files(folder, FileAttributes.ReparsePoint))
                    entries.Add(new TrashEntry(pairId, Path.GetRelativePath(folder, file.FullName), file.FullName, new DateTimeOffset(removed), file.Length));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not shown until Windows lets it be read.
            }
        }
        return entries.OrderByDescending(e => e.Removed).ThenBy(e => e.RelativePath, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Puts a file back where it was - with "(wiederhergestellt)" in its name when a file of that name is there. The
    /// next synchronisation brings it to the cloud again. Returns where it is now.
    /// </summary>
    public static string Restore(string localPath, TrashEntry entry)
    {
        var target = Path.Combine(localPath, entry.RelativePath);
        if (File.Exists(target) || Directory.Exists(target)) target = FreeName(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(entry.FullPath, target);
        RemoveEmptyFolders(localPath, Path.GetDirectoryName(entry.FullPath)!);
        return target;
    }

    public static void Delete(string localPath, TrashEntry entry)
    {
        if (File.Exists(entry.FullPath))
        {
            File.SetAttributes(entry.FullPath, FileAttributes.Normal);
            File.Delete(entry.FullPath);
        }
        RemoveEmptyFolders(localPath, Path.GetDirectoryName(entry.FullPath)!);
    }

    /// <summary>Empties the recycle bin of a synchronisation.</summary>
    public static void Empty(string localPath)
    {
        var root = Path.Combine(localPath, SyncFilters.TrashFolder);
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    /// <summary>Removes recycle bin folders older than <paramref name="keep"/>.</summary>
    public static void CleanUp(string localPath, TimeSpan keep, DateTimeOffset? now = null)
    {
        var root = Path.Combine(localPath, SyncFilters.TrashFolder);
        if (!Directory.Exists(root)) return;
        var limit = (now ?? DateTimeOffset.Now) - keep;
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(folder), StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var removed)) continue;
            if (new DateTimeOffset(removed) >= limit) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A file is open; it goes next time.
            }
        }
    }

    private static string FreeName(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var number = 1; ; number++)
        {
            var suffix = number == 1 ? " (wiederhergestellt)" : $" (wiederhergestellt {number})";
            var candidate = Path.Combine(folder, stem + suffix + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Removes empty folders from <paramref name="folder"/> up to the recycle bin itself.</summary>
    private static void RemoveEmptyFolders(string localPath, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.Combine(localPath, SyncFilters.TrashFolder));
        var current = folder;
        while (current.Length > root.Length && current.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
        {
            Directory.Delete(current);
            current = Path.GetDirectoryName(current)!;
        }
    }
}
