using System.IO;
using Microsoft.Win32;

namespace CloudDriveSync.App.Infrastructure;

/// <summary>
/// The entries the installed CloudDrive-Sync keeps in Windows for the signed-in user: its address under "App Paths" -
/// so CloudDrives and Windows' "Ausführen" find it wherever it is installed - and the start entry.
/// </summary>
internal static class AppRegistration
{
    private const string AppPathKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\CloudDrive-Sync.exe";

    /// <summary>Records where the program is.</summary>
    public static void Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppPathKey);
            if (key.GetValue(null) as string != exe) key.SetValue(null, exe);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Core.Diagnostics.Log.Warn("App", $"Program address not recorded: {e.Message}");
        }
    }

    /// <summary>Before uninstalling: removes the program's address and its start entry. Settings and files stay.</summary>
    public static void Remove()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(AppPathKey, throwOnMissingSubKey: false);
            Autostart.Apply(false, ViewModels.SettingsViewModel.AutostartName);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Uninstalling goes on; a leftover entry only points to a program that is gone.
        }
    }

    /// <summary>Before uninstalling, last: settings, sign-ins, logs and rclone (<see cref="Core.Uninstall.RemoveData"/>).</summary>
    public static void RemoveData()
    {
        try
        {
            var paths = Core.AppPaths.FromEnvironment();
            var stayed = Core.Uninstall.RemoveData(paths);
            // The log went with the data folder; what stayed is written only when something did.
            if (stayed.Count > 0 && Directory.Exists(paths.LogDir)) Core.Diagnostics.Log.Warn("App", $"Uninstalling: {stayed.Count} thing(s) stayed: {string.Join("; ", stayed)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Uninstalling goes on.
        }
    }

    /// <summary>
    /// Before uninstalling: ends the registrations of synchronisations with files on demand, each only once its folder
    /// is clear: fetched files stay as normal files, online-only ones leave the PC (their data stays in the cloud). A
    /// folder a program holds stays registered - nothing in it becomes unreadable - and is cleared should CloudDrive-Sync
    /// come back. Velopack allows 30 seconds.
    /// </summary>
    public static void EndFilesOnDemand()
    {
        try
        {
            var paths = Core.AppPaths.FromEnvironment();
            // The log stays with the settings; it tells later what happened here.
            if (Directory.Exists(paths.LogDir)) Core.Diagnostics.Log.Initialize(paths.LogDir);
            // On a thread of its own: the registration is a Windows Runtime call, the uninstall step runs on the UI thread.
            var ending = Task.Run(() => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? Core.Sync.SyncService.EndAllForUninstall(paths, TimeSpan.FromSeconds(20)) : []);
            if (ending.Wait(TimeSpan.FromSeconds(25))) Core.Diagnostics.Log.Info("App", $"Uninstalling: {ending.Result.Count} folder(s) with files on demand unregistered.");
            // The entries of classic synchronisations in Explorer's navigation pane go, too.
            Core.Diagnostics.Log.Info("App", $"Uninstalling: {Core.CloudFiles.ExplorerEntries.RemoveAll(paths)} Explorer entry(s) removed.");
        }
        catch (Exception e) when (e is AggregateException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Uninstalling goes on; a leftover entry only points to a program that is gone.
            Core.Diagnostics.Log.Warn("App", $"Uninstalling: folders with files on demand not unregistered: {(e as AggregateException)?.InnerException?.Message ?? e.Message}");
        }
    }
}
