using CloudDriveSync.Core.Diagnostics;
using Microsoft.Win32;

namespace CloudDriveSync.App.Infrastructure;

/// <summary>Start with Windows: an entry for the signed-in user (no administrator rights), started in the background.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled, string name)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            var command = $"\"{exe}\" --background";
            if (!enabled) key.DeleteValue(name, throwOnMissingValue: false);
            else if (key.GetValue(name) as string != command) key.SetValue(name, command);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            Log.Warn("App", $"Start with Windows could not be changed: {e.Message}");
        }
    }
}
