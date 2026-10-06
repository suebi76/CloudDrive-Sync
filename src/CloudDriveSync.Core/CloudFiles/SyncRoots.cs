using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using System.Runtime.Versioning;

namespace CloudDriveSync.Core.CloudFiles;

/// <summary>How a folder is registered with Windows for files on demand.</summary>
/// <param name="Context">Kept by Windows with the registration; CloudDrive-Sync stores the synchronisation's ID in it.</param>
public sealed record SyncRootSpec(string Id, string Path, string DisplayName, string IconResource, string Version, string Context);

/// <summary>
/// Registers folders with Windows as sync roots (StorageProviderSyncRootManager of the Windows Runtime, called through
/// <see cref="WinRtSyncRootManager"/>). A registered folder gets its entry in Explorer's navigation pane, the status
/// icons and the commands "Always keep on this device" and "Free up space" - all from Windows. IDs follow Windows'
/// pattern "provider!user SID!account"; the account part is the ID of the synchronisation.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public static class SyncRoots
{
    private const string ManagerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

    /// <summary>The signed-in user's SID, part of every ID.</summary>
    public static string UserSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("no user SID");

    public static string IdFor(AppPaths paths, string pairId) => $"{paths.SyncRootProvider}!{UserSid}!{pairId}";

    /// <summary>
    /// Registers (or updates) a sync root. Full hydration (a file comes completely when opened), all entries created by
    /// CloudDrive-Sync itself, no automatic freeing of space by Windows - CloudDrive-Sync's own setting decides that.
    /// </summary>
    public static void Register(SyncRootSpec spec) => WinRtSyncRootManager.Register(spec);

    /// <summary>
    /// Ends a registration. Windows turns placeholders whose data is on the PC into normal files and removes those that
    /// were online only (their data stays in the cloud). The folder must not be connected any more.
    /// </summary>
    public static void Unregister(string id) => WinRtSyncRootManager.Unregister(id);

    /// <summary>
    /// Whether Windows has a registration with this ID. Read from the registry: Windows' own queries leave out sync roots
    /// in the temp folder, where the tests keep theirs.
    /// </summary>
    public static bool IsRegistered(string id)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ManagerKey}\{id}");
        return key is not null;
    }

    /// <summary>The entry Windows made in Explorer's navigation pane for a registration (a CLSID), or null.</summary>
    public static string? NavigationPaneEntry(string id)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ManagerKey}\{id}");
        return key?.GetValue("NamespaceCLSID") as string;
    }

    /// <summary>The folders of all of this user's sync roots, of any program (Windows keeps them under "UserSyncRoots").</summary>
    public static IReadOnlyList<string> RegisteredFolders()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ManagerKey);
        var sid = UserSid;
        var folders = new List<string>();
        foreach (var id in key?.GetSubKeyNames() ?? [])
        {
            try
            {
                using var roots = key!.OpenSubKey($@"{id}\UserSyncRoots");
                if (roots?.GetValue(sid) is string folder && folder.Length > 0) folders.Add(folder);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // Some programs protect their registration; Windows itself still refuses a folder nested in theirs.
            }
        }
        return folders;
    }

    /// <summary>The registrations of this user under a provider (see <see cref="AppPaths.SyncRootProvider"/>).</summary>
    public static IReadOnlyList<string> RegisteredIds(string provider)
    {
        using var key = Registry.LocalMachine.OpenSubKey(ManagerKey);
        var prefix = $"{provider}!{UserSid}!";
        return (key?.GetSubKeyNames() ?? []).Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    /// <summary>
    /// Before CloudDrive-Sync is uninstalled: ends this user's registrations of the installed program (never those of the
    /// tests), so Explorer keeps no entries of a program that is gone. What happens to the files is described at
    /// <see cref="Unregister"/>; should CloudDrive-Sync come back, the first run of each synchronisation merges both sides
    /// and deletes nothing. Returns the ended IDs.
    /// </summary>
    public static IReadOnlyList<string> UnregisterAll(AppPaths paths)
    {
        var ended = new List<string>();
        foreach (var id in RegisteredIds(paths.SyncRootProvider))
        {
            Unregister(id);
            ended.Add(id);
        }
        return ended;
    }

    /// <summary>
    /// Removes what tests left behind: registrations of other homes ("CloudDriveSyncTest-…") of this user. The
    /// installation's own registrations ("CloudDriveSync!…") are never touched. Returns the removed IDs.
    /// </summary>
    public static IReadOnlyList<string> RemoveTestRegistrations()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ManagerKey);
        var removed = new List<string>();
        foreach (var id in key?.GetSubKeyNames() ?? [])
        {
            var parts = id.Split('!');
            if (parts.Length < 3 || !parts[0].StartsWith("CloudDriveSyncTest-", StringComparison.Ordinal) || parts[1] != UserSid) continue;
            Unregister(id);
            removed.Add(id);
        }
        return removed;
    }

    /// <summary>
    /// The context of the sync root a path lies in (for CloudDrive-Sync's own: the synchronisation's ID), "" for another
    /// program's sync root without one, or null when the path lies in no sync root at all.
    /// </summary>
    public static unsafe string? ContextOf(string path)
    {
        var buffer = new byte[sizeof(CF_SYNC_ROOT_STANDARD_INFO) + 64 * 1024];
        fixed (byte* pointer = buffer)
        fixed (char* pathPointer = Path.GetFullPath(path))
        {
            uint returned;
            var result = PInvoke.CfGetSyncRootInfoByPath(new PCWSTR(pathPointer), CF_SYNC_ROOT_INFO_CLASS.CF_SYNC_ROOT_INFO_STANDARD, pointer, (uint)buffer.Length, &returned);
            if (result.Failed) return null;
            var info = (CF_SYNC_ROOT_STANDARD_INFO*)pointer;
            return Encoding.UTF8.GetString((byte*)&info->SyncRootIdentity, (int)info->SyncRootIdentityLength);
        }
    }
}
