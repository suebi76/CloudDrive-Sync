using System.Runtime.InteropServices;
using CloudDriveSync.Core.Diagnostics;

namespace CloudDriveSync.App.Infrastructure;

/// <summary>
/// Asks Windows to start CloudDrive-Sync again after a crash or a hang - files that are only online open only while it
/// runs. It comes back in the background, as at the start with Windows; not after a restart of Windows itself, where
/// the start entry does it. Windows does so for programs that ran at least a minute.
/// </summary>
internal static partial class CrashRestart
{
    private const int NotAfterReboot = 8;

    public static void Register()
    {
        var result = RegisterApplicationRestart("--background", NotAfterReboot);
        if (result != 0) Log.Warn("App", $"Restart after a crash not registered: 0x{result:X8}");
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterApplicationRestart(string? commandLine, int flags);
}
