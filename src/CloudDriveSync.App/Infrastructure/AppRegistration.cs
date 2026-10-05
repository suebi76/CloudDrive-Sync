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
}