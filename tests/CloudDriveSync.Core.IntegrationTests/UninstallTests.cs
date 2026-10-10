using CloudDriveSync.Core.Security;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>Uninstalling leaves nothing of CloudDrive-Sync on the PC - but every synchronised file (<see cref="Uninstall"/>).</summary>
public class UninstallTests
{
    [Fact]
    public void Uninstalling_removes_settings_sign_ins_logs_and_rclone_and_keeps_every_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudDriveSyncTests", "uninstall-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(Path.Combine(root, "home"));
        var folder = Path.Combine(root, "Eigene Dateien");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Brief.docx"), "Inhalt");
            File.WriteAllText(Path.Combine(folder, SyncFilters.SentinelFile), "Wächter");
            new SettingsStore(paths.SettingsFile).Update(s => s.Syncs.Add(new SyncPairSettings { Id = "test-eigene", AccountId = "test", LocalPath = folder }));
            Directory.CreateDirectory(paths.LogDir);
            File.WriteAllText(Path.Combine(paths.LogDir, "clouddrive-sync-2026-10-10.log"), "Zeile");
            File.WriteAllText(Path.Combine(paths.Home, "rclone.conf"), "RCLONE_ENCRYPT_V0:");
            var secrets = new SecretStore(paths.SecretPrefix);
            secrets.Set("config", "test-schlüssel");

            Assert.Empty(Uninstall.RemoveData(paths));

            Assert.False(Directory.Exists(paths.Home));
            Assert.Null(secrets.Get("config"));
            Assert.False(File.Exists(Path.Combine(folder, SyncFilters.SentinelFile)));
            Assert.Equal("Inhalt", File.ReadAllText(Path.Combine(folder, "Brief.docx")));
        }
        finally
        {
            new SecretStore(paths.SecretPrefix).Delete("config");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_note_of_a_held_folder_stays_for_a_new_installation()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudDriveSyncTests", "uninstall-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(Path.Combine(root, "home"));
        try
        {
            new SettingsStore(paths.SettingsFile).Update(_ => { });
            File.WriteAllText(Path.Combine(paths.Home, "cleanup.json"), "[]");
            Assert.Empty(Uninstall.RemoveData(paths));
            Assert.Equal(["cleanup.json"], Directory.GetFileSystemEntries(paths.Home).Select(Path.GetFileName));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_folder_that_is_not_CloudDrive_Syncs_own_is_never_deleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudDriveSyncTests", "uninstall-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "fremd.txt"), "bleibt");
            Assert.Single(Uninstall.RemoveData(new AppPaths(root)));
            Assert.True(File.Exists(Path.Combine(root, "fremd.txt")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
