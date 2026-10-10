using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using Microsoft.Win32;

namespace CloudDriveSync.Core.Sync;

/// <summary>A hint about a chosen local folder. The user reads the hint and decides.</summary>
public sealed record FolderWarning(string Kind, string Text);

/// <summary>
/// Looks at a local folder before it is used for a synchronisation. Only what cannot work at all is an error (no
/// valid path, drive not there, overlap with another CloudDrive-Sync pair); network drives, cloud drives, removable
/// drives, system folders, and folders of other sync programs become warnings.
/// </summary>
public static class LocalFolderCheck
{
    public static IReadOnlyList<FolderWarning> Check(string path, IEnumerable<SyncPairSettings> otherPairs)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new CdException("CD-4501", $"not a complete path: '{path}'");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full) ?? "";
        var warnings = new List<FolderWarning>();
        EnsureNoPairOverlap(full, otherPairs);
        ProbeFolder(full);

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            warnings.Add(new("network", "Der Ordner liegt im Netzwerk. Die Synchronisation ist dort langsamer und pausiert, wenn das Netzwerk fehlt."));
        }
        else
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady) throw new CdException("CD-4501", $"drive {root} is not ready");
            if (drive.DriveType == DriveType.Network)
                warnings.Add(new("network", $"{root.TrimEnd('\\')} ist ein Netzlaufwerk. Die Synchronisation ist dort langsamer und pausiert, wenn das Netzwerk fehlt."));
            if (drive.DriveType == DriveType.Removable)
                warnings.Add(new("removable", $"{root.TrimEnd('\\')} ist ein Wechseldatenträger. Ist er nicht angeschlossen, pausiert die Synchronisation – es wird dann nichts gelöscht."));
            var format = SafeFormat(drive);
            if (format.Contains("FUSE", StringComparison.OrdinalIgnoreCase) || format.Contains("rclone", StringComparison.OrdinalIgnoreCase))
                warnings.Add(new("cloud-drive", $"{root.TrimEnd('\\')} ist ein Cloud-Laufwerk. Liegt dort dasselbe Konto, gleicht sich die Cloud mit sich selbst ab."));
        }

        if (full.Length <= root.TrimEnd('\\').Length)
            warnings.Add(new("drive-root", "Es wird ein ganzes Laufwerk synchronisiert – mit allen Ordnern darauf."));

        foreach (var system in SystemFolders())
            if (IsSameOrInside(full, system))
                warnings.Add(new("system", $"Der Ordner liegt in einem Systemordner ({system}). Programme und Windows können dort Dateien sperren oder ändern."));

        foreach (var (client, folder) in OtherSyncClientFolders())
            if (IsSameOrInside(full, folder) || IsSameOrInside(folder, full))
                warnings.Add(new("other-client", $"Der Ordner überschneidet sich mit {client} ({folder}). Dann synchronisieren zwei Programme dieselben Dateien."));

        if (Directory.Exists(full))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(full).Any(e => !Path.GetFileName(e).StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase)))
                    warnings.Add(new("not-empty", "Der Ordner enthält schon Dateien. Sie werden mit der Cloud zusammengeführt – gelöscht wird dabei nichts."));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new CdException("CD-4513", full, e);
            }
        }

        return warnings;
    }

    /// <summary>Two pairs must not write to the same local tree, even when their accounts differ.</summary>
    public static void EnsureNoPairOverlap(string path, IEnumerable<SyncPairSettings> otherPairs)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (var pair in otherPairs)
        {
            if (string.IsNullOrWhiteSpace(pair.LocalPath)) continue;
            var other = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pair.LocalPath));
            if (IsSameOrInside(full, other) || IsSameOrInside(other, full))
                throw new CdException("CD-4506", $"{full} overlaps {other}");
        }
    }

    /// <summary>
    /// Before setup writes anything, checks an existing local tree for unreadable entries and orphaned cloud placeholders.
    /// Reads metadata only, never file content and never follows links. Call off the UI thread for a large tree.
    /// </summary>
    public static void EnsureExistingTreeSafe(string folder, CancellationToken cancellationToken = default)
    {
        FileAttributes rootAttributes;
        try { rootAttributes = File.GetAttributes(folder); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CdException("CD-4513", folder, e);
        }
        if ((rootAttributes & FileAttributes.Directory) == 0) throw new CdException("CD-4501", $"not a folder: {folder}");
        if (HasCloudMarks(rootAttributes) || (rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new CdException("CD-4513", $"cloud placeholder or link at folder root: {folder}");

        var pending = new Stack<string>([folder]);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(entry);
                    if (HasCloudMarks(attributes)) throw new CdException("CD-4513", $"cloud placeholder: {entry}");
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // A link is not part of this tree. Other reparse points may be orphaned cloud placeholders.
                        var link = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(entry).LinkTarget : new FileInfo(entry).LinkTarget;
                        if (link is null) throw new CdException("CD-4513", $"unregistered reparse point: {entry}");
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new CdException("CD-4513", current, e);
            }
        }
    }

    private static bool HasCloudMarks(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000)) != 0;

    /// <summary>A quick, nonrecursive check for feedback while the user edits the path.</summary>
    private static void ProbeFolder(string folder)
    {
        try
        {
            if ((File.GetAttributes(folder) & FileAttributes.Directory) == 0)
                throw new CdException("CD-4501", $"not a folder: {folder}");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CdException("CD-4513", folder, e);
        }
    }

    /// <summary>Suggests a new sibling folder for an account or one of its cloud folders.</summary>
    public static string? SuggestDefaultPath(string userProfile, string accountLabel, string? cloudFolderName, IEnumerable<SyncPairSettings> otherPairs)
    {
        var name = SafeFolderName(accountLabel);
        if (!string.IsNullOrWhiteSpace(cloudFolderName)) name += " - " + SafeFolderName(cloudFolderName);
        var preferred = Path.Combine(userProfile, "CloudDrive-Sync", name);
        var pairs = otherPairs.Where(p => !string.IsNullOrWhiteSpace(p.LocalPath))
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.LocalPath))).ToArray();
        for (var number = 1; number <= 1000; number++)
        {
            var candidate = number == 1 ? preferred : $"{preferred} ({number})";
            // A numbered sibling cannot escape a pair that already contains the parent.
            if (pairs.Any(pair => IsSameOrInside(Path.GetDirectoryName(candidate)!, pair))) return null;
            if (Path.Exists(candidate) || pairs.Any(pair => IsSameOrInside(candidate, pair) || IsSameOrInside(pair, candidate))) continue;
            return candidate;
        }
        return null;
    }

    private static string SafeFolderName(string name)
    {
        var cleaned = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (cleaned.Length > 80) cleaned = cleaned[..80].TrimEnd(' ', '.');
        if (cleaned.Length == 0) return "Cloud";
        var stem = cleaned.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
            cleaned = "_" + cleaned;
        return cleaned;
    }

    public static bool IsSameOrInside(string path, string folder)
    {
        var a = Path.TrimEndingDirectorySeparator(path);
        var b = Path.TrimEndingDirectorySeparator(folder);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(Path.EndsInDirectorySeparator(b) ? b : b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeFormat(DriveInfo drive)
    {
        try { return drive.DriveFormat; }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    private static IEnumerable<string> SystemFolders()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)) yield return path;
        }
    }

    /// <summary>Folders the OneDrive client and the Nextcloud client synchronise.</summary>
    private static IEnumerable<(string Client, string Folder)> OtherSyncClientFolders()
    {
        var found = new List<(string, string)>();
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            foreach (var name in accounts?.GetSubKeyNames() ?? [])
            {
                using var account = accounts!.OpenSubKey(name);
                if (account?.GetValue("UserFolder") is string folder && folder.Length > 0) found.Add(("dem OneDrive-Client", folder));
            }
        }
        catch (System.Security.SecurityException)
        {
        }

        var nextcloudConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nextcloud", "nextcloud.cfg");
        if (File.Exists(nextcloudConfig))
        {
            try
            {
                foreach (var line in File.ReadLines(nextcloudConfig))
                {
                    var index = line.IndexOf("localPath=", StringComparison.OrdinalIgnoreCase);
                    if (index < 0) continue;
                    var folder = line[(index + "localPath=".Length)..].Trim().Replace('/', '\\');
                    if (folder.Length > 0) found.Add(("dem Nextcloud-Client", folder));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }
}
