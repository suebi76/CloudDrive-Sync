using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using Microsoft.Win32;

namespace CloudDriveSync.Core.Sync;

/// <summary>A hint about a chosen local folder. Nothing is forbidden: the user reads the hint and decides.</summary>
public sealed record FolderWarning(string Kind, string Text);

/// <summary>
/// Looks at a local folder before it is used for a synchronisation. Only what cannot work at all is an error (no
/// valid path, drive not there); everything else - network drive, cloud drive, removable drive, system folder, a
/// folder of another sync program, an overlap with another synchronisation - becomes a warning.
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

        foreach (var pair in otherPairs)
        {
            if (string.IsNullOrWhiteSpace(pair.LocalPath)) continue;
            var other = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pair.LocalPath));
            if (IsSameOrInside(full, other) || IsSameOrInside(other, full))
                warnings.Add(new("overlap", $"Der Ordner überschneidet sich mit einer anderen Synchronisation ({other})."));
        }

        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any(e => !Path.GetFileName(e).StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase)))
            warnings.Add(new("not-empty", "Der Ordner enthält schon Dateien. Sie werden mit der Cloud zusammengeführt – gelöscht wird dabei nichts."));

        return warnings;
    }

    public static bool IsSameOrInside(string path, string folder)
    {
        var a = Path.TrimEndingDirectorySeparator(path);
        var b = Path.TrimEndingDirectorySeparator(folder);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
            foreach (var line in File.ReadLines(nextcloudConfig))
            {
                var index = line.IndexOf("localPath=", StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;
                var folder = line[(index + "localPath=".Length)..].Trim().Replace('/', '\\');
                if (folder.Length > 0) found.Add(("dem Nextcloud-Client", folder));
            }
        }
        return found;
    }
}
