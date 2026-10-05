using System.IO;
using CloudDriveSync.Core;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Security;

namespace CloudDriveSync.App.Infrastructure;

/// <summary>
/// The first test build was called "CloudDrives 2". Its data folder, the key of its encrypted configuration and its
/// start entry move over to CloudDrive-Sync once - nothing has to be set up again.
/// </summary>
internal static class TestBuildMigration
{
    private const string OldName = "CloudDrives2";

    public static void Run(AppPaths paths)
    {
        if (!paths.IsDefaultHome) return;
        var oldHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), OldName);
        var moved = 0;
        try
        {
            if (Directory.Exists(oldHome))
            {
                Directory.CreateDirectory(paths.Home);
                foreach (var entry in Directory.EnumerateFileSystemEntries(oldHome))
                {
                    var target = Path.Combine(paths.Home, Path.GetFileName(entry));
                    if (File.Exists(target) || Directory.Exists(target)) continue;
                    if (Directory.Exists(entry)) Directory.Move(entry, target);
                    else File.Move(entry, target);
                    moved++;
                }
                if (!Directory.EnumerateFileSystemEntries(oldHome).Any()) Directory.Delete(oldHome);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file is still open: what is left stays where it is; CloudDrive-Sync starts with what it has.
        }

        var oldSecrets = new SecretStore(OldName);
        if (oldSecrets.Get("config") is { Length: > 0 } key)
        {
            var secrets = new SecretStore(paths.SecretPrefix);
            if (string.IsNullOrEmpty(secrets.Get("config"))) secrets.Set("config", key);
            oldSecrets.Delete("config");
            moved++;
        }
        Autostart.Apply(false, OldName);
        if (moved > 0) Log.Info("App", $"Data of the test build \"CloudDrives 2\" taken over ({moved} item(s)).");
    }
}
