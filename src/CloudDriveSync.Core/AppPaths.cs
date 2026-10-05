using System.Security.Cryptography;
using System.Text;

namespace CloudDriveSync.Core;

/// <summary>
/// Folders and files of CloudDrive-Sync. Everything lives below one home folder, by default
/// %LOCALAPPDATA%\CloudDrive-Sync - separate from CloudDrives, so both versions can run side by side.
/// Tests (and a portable copy) choose another home with CLOUDDRIVE_SYNC_HOME.
/// </summary>
public sealed class AppPaths
{
    public const string HomeVariable = "CLOUDDRIVE_SYNC_HOME";

    public AppPaths(string home, bool isDefaultHome = false)
    {
        Home = Path.GetFullPath(home);
        IsDefaultHome = isDefaultHome;
    }

    public static AppPaths FromEnvironment()
    {
        var custom = Environment.GetEnvironmentVariable(HomeVariable);
        if (!string.IsNullOrWhiteSpace(custom)) return new AppPaths(custom);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, "CloudDrive-Sync"), isDefaultHome: true);
    }

    public string Home { get; }
    public bool IsDefaultHome { get; }

    public string SettingsFile => Path.Combine(Home, "settings.json");
    public string RcloneConfig => Path.Combine(Home, "rclone.conf");
    public string LogDir => Path.Combine(Home, "logs");
    public string RcloneLog => Path.Combine(LogDir, "rclone.log");
    public string CacheDir => Path.Combine(Home, "cache");
    public string DepsDir => Path.Combine(Home, "deps");
    public string SyncDir => Path.Combine(Home, "sync");
    public string BackupDir => Path.Combine(Home, "backup");

    public string SyncPairDir(string pairId) => Path.Combine(SyncDir, pairId);

    /// <summary>
    /// Prefix of the entries in the Windows Credential Manager. Another home gets its own entries, so tests never
    /// touch the keys of the real installation.
    /// </summary>
    public string SecretPrefix
    {
        get
        {
            if (IsDefaultHome) return "CloudDrive-Sync";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Home.ToUpperInvariant()));
            return "CloudDrive-Sync-" + Convert.ToHexString(hash)[..12];
        }
    }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Home, LogDir, CacheDir, DepsDir, SyncDir, BackupDir })
            Directory.CreateDirectory(dir);
    }
}
