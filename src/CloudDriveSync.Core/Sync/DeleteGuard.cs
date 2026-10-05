using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

/// <summary>
/// CloudDrive-Sync's own deletion guard, counted in files as the user reads it ("more than half of the files"). rclone's
/// guard counts folders as well, so in a small tree with several folders many deleted files can stay below its
/// limit. After each successful run the synchronised files on the PC are remembered; before a normal run, files that
/// disappeared from the PC since then are counted against them. Single deletions never stop a synchronisation.
/// </summary>
internal static class DeleteGuard
{
    public const int MinimumDeletions = 3;

    private static string ListFile(string pairFolder) => Path.Combine(pairFolder, "local-files.txt");

    /// <summary>Files gone from the PC since the last run when they are more than allowed, otherwise null.</summary>
    public static (int Missing, int Known)? TooManyMissing(string pairFolder, SyncPairSettings pair)
    {
        var file = ListFile(pairFolder);
        if (pair.MaxDeletePercent >= 100 || !File.Exists(file)) return null;
        var known = File.ReadAllLines(file).Where(line => line.Length > 0).ToList();
        if (known.Count == 0) return null;
        var current = LocalFiles(pair);
        var missing = known.Count(path => !current.Contains(path));
        if (missing < MinimumDeletions || missing * 100L <= (long)pair.MaxDeletePercent * known.Count) return null;
        return (missing, known.Count);
    }

    /// <summary>After a successful run: remembers the synchronised files on the PC.</summary>
    public static void Remember(string pairFolder, SyncPairSettings pair)
    {
        var file = ListFile(pairFolder);
        var temporary = file + ".tmp";
        File.WriteAllLines(temporary, LocalFiles(pair).Order(StringComparer.Ordinal));
        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>The synchronised files on the PC (relative, "/" separated): selection applied, own and temporary files left out.</summary>
    internal static HashSet<string> LocalFiles(SyncPairSettings pair)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(pair.LocalPath)) return files;
        var selected = pair.Selection.Mode == SelectionMode.Selected ? SyncFilters.Normalise(pair.Selection.Include) : null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var path in Directory.EnumerateFiles(pair.LocalPath, "*", options))
        {
            var relative = Path.GetRelativePath(pair.LocalPath, path).Replace('\\', '/');
            if (relative.StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase) || IsTemporary(Path.GetFileName(relative))) continue;
            if (selected is not null && !selected.Any(s => s.EndsWith('/')
                    ? relative.StartsWith(s, StringComparison.OrdinalIgnoreCase)
                    : relative.Equals(s, StringComparison.OrdinalIgnoreCase)))
                continue;
            files.Add(relative);
        }
        return files;
    }

    /// <summary>The same temporary and system files the synchronisation leaves out (see <see cref="SyncFilters.StandardExcludes"/>).</summary>
    private static bool IsTemporary(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith(".~lock.", StringComparison.Ordinal) ||
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
}
