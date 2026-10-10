using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Security;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core;

/// <summary>
/// What uninstalling removes besides the program, once the registrations with Windows have ended: nothing of
/// CloudDrive-Sync is to stay on the PC. The synchronised folders and every file in them stay; in the cloud nothing
/// changes.
/// </summary>
public static class Uninstall
{
    /// <summary>
    /// The sentinel files in the synchronised folders on this PC (never the one in the cloud - other PCs may use it),
    /// the key of the encrypted sign-ins in Windows' credential manager, and the data folder: settings, sign-ins, state,
    /// logs and rclone. Only the note of a folder a program still held stays (see <see cref="SyncService.EndAllForUninstall"/>),
    /// so a new installation can clear it. Returns what stayed, for the log.
    /// </summary>
    public static IReadOnlyList<string> RemoveData(AppPaths paths)
    {
        var stayed = new List<string>();
        if (!IsOwnDataFolder(paths))
        {
            stayed.Add($"data folder {paths.Home} (not CloudDrive-Sync's own)");
            return stayed;
        }
        if (File.Exists(paths.SettingsFile))
        {
            foreach (var pair in new SettingsStore(paths.SettingsFile).Current.Syncs)
            {
                var sentinel = Path.Combine(pair.LocalPath, SyncFilters.SentinelFile);
                try
                {
                    if (File.Exists(sentinel))
                    {
                        File.SetAttributes(sentinel, FileAttributes.Normal);
                        File.Delete(sentinel);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    stayed.Add($"{sentinel}: {e.Message}");
                }
            }
        }
        try
        {
            new SecretStore(paths.SecretPrefix).Delete("config");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            stayed.Add($"credential: {e.Message}");
        }
        var keep = Path.Combine(paths.Home, "cleanup.json");
        foreach (var entry in Directory.Exists(paths.Home) ? Directory.EnumerateFileSystemEntries(paths.Home).ToList() : [])
        {
            if (string.Equals(entry, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                stayed.Add($"{entry}: {e.Message}");
            }
        }
        try
        {
            if (Directory.Exists(paths.Home) && !Directory.EnumerateFileSystemEntries(paths.Home).Any()) Directory.Delete(paths.Home);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stayed.Add($"{paths.Home}: {e.Message}");
        }
        return stayed;
    }

    /// <summary>
    /// Only CloudDrive-Sync's own data folder is ever deleted: the default one, or one with its settings in it - never a
    /// drive, the user folder or the folder all programs keep their data in.
    /// </summary>
    private static bool IsOwnDataFolder(AppPaths paths)
    {
        var home = Path.TrimEndingDirectorySeparator(paths.Home);
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(home) ?? "");
        if (home.Length <= root.Length) return false;
        var forbidden = new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
        }.Select(f => Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(f))).Where(f => f.Length > 0);
        if (forbidden.Any(f => string.Equals(f, home, StringComparison.OrdinalIgnoreCase))) return false;
        return paths.IsDefaultHome || File.Exists(paths.SettingsFile);
    }
}
