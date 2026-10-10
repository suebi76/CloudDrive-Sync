using System.Globalization;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

/// <summary>Size and modification time of a file on the PC.</summary>
internal sealed record LocalFileState(long Size, long Ticks);

/// <summary>
/// What CloudDrive-Sync remembers about the PC side after each successful run: every synchronised file with size and
/// modification time. It serves CloudDrive-Sync's own deletion guard, counted in files as the user reads it ("more
/// than half of the files" - rclone's guard counts folders as well, so in a small tree many deleted files can stay
/// below its limit), and finding the files changed since the last run. Single deletions never stop a run.
/// </summary>
internal static class DeleteGuard
{
    public const int MinimumDeletions = 3;

    private static string ListFile(string pairFolder) => Path.Combine(pairFolder, "local-files.txt");

    /// <summary>Files gone from the PC since the last run when they are more than allowed, otherwise null.</summary>
    public static (int Missing, int Known)? TooManyMissing(string pairFolder, SyncPairSettings pair)
    {
        if (pair.MaxDeletePercent >= 100) return null;
        var known = Load(pairFolder);
        if (known is null || known.Count == 0) return null;
        var refused = new List<string>();
        var current = LocalFiles(pair, refused);
        // What is in a folder Windows refuses is unknown, not missing.
        var missing = known.Keys.Count(path => !current.ContainsKey(path) && !IsBelow(path, refused));
        if (missing < MinimumDeletions || missing * 100L <= (long)pair.MaxDeletePercent * known.Count) return null;
        return (missing, known.Count);
    }

    /// <summary>Files on the PC that are new or changed since the last successful run (none when nothing is remembered).</summary>
    public static IReadOnlyList<string> ChangedSinceLastRun(string pairFolder, SyncPairSettings pair)
    {
        var known = Load(pairFolder);
        if (known is null) return [];
        return LocalFiles(pair)
            .Where(file => !known.TryGetValue(file.Key, out var before) || before.Size != file.Value.Size || (before.Ticks >= 0 && before.Ticks != file.Value.Ticks))
            .Select(file => file.Key)
            .ToList();
    }

    /// <summary>After a successful run: remembers the synchronised files on the PC.</summary>
    public static void Remember(string pairFolder, SyncPairSettings pair)
    {
        var file = ListFile(pairFolder);
        var temporary = file + ".tmp";
        var refused = new List<string>();
        var files = LocalFiles(pair, refused);
        // A folder Windows refuses keeps what was known of it.
        if (refused.Count > 0 && Load(pairFolder) is { } known)
            foreach (var (path, state) in known)
                if (IsBelow(path, refused)) files.TryAdd(path, state);
        File.WriteAllLines(temporary, files
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Value.Ticks}\t{f.Value.Size}\t{f.Key}")));
        File.Move(temporary, file, overwrite: true);
    }

    internal static Dictionary<string, LocalFileState>? Load(string pairFolder)
    {
        var file = ListFile(pairFolder);
        if (!File.Exists(file)) return null;
        var known = new Dictionary<string, LocalFileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(file))
        {
            if (line.Length == 0) continue;
            var parts = line.Split('\t', 3);
            if (parts.Length == 3 && long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks) && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var size))
                known[parts[2]] = new LocalFileState(size, ticks);
            else
                known[line] = new LocalFileState(-1, -1); // remembered by an earlier version: only the name
        }
        return known;
    }

    /// <summary>The synchronised files on the PC (relative, "/" separated): selection applied, own and temporary files left out.</summary>
    /// <param name="refused">Collects the folders Windows refuses to open (relative, "/" separated): what is in them is unknown.</param>
    internal static Dictionary<string, LocalFileState> LocalFiles(SyncPairSettings pair, List<string>? refused = null)
    {
        var files = new Dictionary<string, LocalFileState>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(pair.LocalPath)) return files;
        var selected = pair.Selection.Mode == SelectionMode.Selected ? SyncFilters.Normalise(pair.Selection.Include) : null;
        var refusedFull = new List<string>();
        foreach (var info in FolderWalk.Files(pair.LocalPath, FileAttributes.ReparsePoint, refusedFull))
        {
            var relative = Path.GetRelativePath(pair.LocalPath, info.FullName).Replace('\\', '/');
            if (relative.StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase) || IsTemporary(info.Name)) continue;
            if (selected is not null && !selected.Any(s => s.EndsWith('/')
                    ? relative.StartsWith(s, StringComparison.OrdinalIgnoreCase)
                    : relative.Equals(s, StringComparison.OrdinalIgnoreCase)))
                continue;
            files[relative] = new LocalFileState(info.Length, info.LastWriteTimeUtc.Ticks);
        }
        refused?.AddRange(refusedFull.Select(f => Path.GetRelativePath(pair.LocalPath, f).Replace('\\', '/')));
        return files;
    }

    private static bool IsBelow(string path, List<string> folders) =>
        folders.Any(folder => path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The same temporary and system files the synchronisation leaves out (see <see cref="SyncFilters.StandardExcludes"/>).</summary>
    private static bool IsTemporary(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith(".~lock.", StringComparison.Ordinal) ||
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
}
