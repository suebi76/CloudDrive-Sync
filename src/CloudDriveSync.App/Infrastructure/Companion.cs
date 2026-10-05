using System.Diagnostics;
using System.IO;
using CloudDriveSync.Core.Diagnostics;

namespace CloudDriveSync.App.Infrastructure;

/// <summary>
/// CloudDrives - the drives program (cloud drives like M: without a local copy) - is a separate program. When it is
/// installed, CloudDrive-Sync opens it from its menu; CloudDrives in turn opens CloudDrive-Sync from its own.
/// </summary>
internal static class Companion
{
    /// <summary>The start file of CloudDrives, or null when it is not installed for this user.</summary>
    public static string? DrivesProgram
    {
        get
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CloudDrives", "CloudDrives.bat");
            return File.Exists(file) ? file : null;
        }
    }

    public static void OpenDrivesProgram()
    {
        if (DrivesProgram is not { } file) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(file)! });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("App", $"CloudDrives could not be opened: {e.Message}");
        }
    }
}
