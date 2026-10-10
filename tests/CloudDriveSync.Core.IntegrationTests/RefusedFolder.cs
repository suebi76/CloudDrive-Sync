using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>
/// A folder on the PC that Windows does not let be opened, until disposed. A test cannot make what does that in real
/// life - a placeholder Windows calls broken (error 363) stays behind for good -, so it is stood in for two ways.
/// </summary>
internal static partial class RefusedFolder
{
    /// <summary>The folder's own rule denies listing it: missing rights.</summary>
    public static IDisposable Deny(string folder)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes, AccessControlType.Deny);
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        return new Release(() =>
        {
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        });
    }

    /// <summary>
    /// A handle that shares nothing holds the folder: opening it to list fails with a sharing violation - an error other
    /// than missing rights, like the broken placeholder's.
    /// </summary>
    public static IDisposable Hold(string folder)
    {
        var handle = CreateFile(folder, GenericRead, 0, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"cannot hold '{folder}'");
        return handle;
    }

    private sealed class Release(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private const uint GenericRead = 0x80000000, OpenExisting = 3, BackupSemantics = 0x02000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
