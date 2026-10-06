using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace CloudDriveSync.Core.CloudFiles;

/// <summary>
/// The entry of a classic synchronisation in Explorer's navigation pane - the same kind of entry Windows makes by itself
/// for folders with files on demand (Microsoft's "Integrate a cloud storage provider", per user in HKCU). Its CLSID
/// follows from the synchronisation and the data folder, so it is found again; a marker value tells CloudDrive-Sync's
/// entries from all others, and only those are ever removed.
/// </summary>
public static partial class ExplorerEntries
{
    private const string NameSpaceKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace";
    private const string HideKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    private const string Marker = "CloudDriveSync.Entry";

    /// <summary>The CLSID of a synchronisation's entry.</summary>
    public static Guid ClsidFor(AppPaths paths, string pairId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"CloudDriveSync|{paths.SyncRootProvider}|{pairId}"))[..16]);

    /// <summary>Adds the entry, or brings name, folder and symbol up to date.</summary>
    public static void Add(AppPaths paths, string pairId, string name, string folder, string icon)
    {
        var clsid = ClsidFor(paths, pairId).ToString("B");
        using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsid}"))
        {
            key.SetValue(null, name);
            key.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
            key.SetValue("SortOrderIndex", 0x42, RegistryValueKind.DWord);
            key.SetValue(Marker, $"{paths.SyncRootProvider}|{pairId}");
            using (var defaultIcon = key.CreateSubKey("DefaultIcon")) defaultIcon.SetValue(null, icon);
            using (var server = key.CreateSubKey("InProcServer32")) server.SetValue(null, @"%SystemRoot%\system32\shell32.dll", RegistryValueKind.ExpandString);
            using (var instance = key.CreateSubKey("Instance"))
            {
                // A folder of the file system, shown under the entry.
                instance.SetValue("CLSID", "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}");
                using var bag = instance.CreateSubKey("InitPropertyBag");
                bag.SetValue("Attributes", 0x11, RegistryValueKind.DWord);
                bag.SetValue("TargetFolderPath", folder, RegistryValueKind.ExpandString);
            }
            using var shellFolder = key.CreateSubKey("ShellFolder");
            shellFolder.SetValue("FolderValueFlags", 0x28, RegistryValueKind.DWord);
            shellFolder.SetValue("Attributes", unchecked((int)0xF080004D), RegistryValueKind.DWord);
        }
        using (var nameSpace = Registry.CurrentUser.CreateSubKey($@"{NameSpaceKey}\{clsid}")) nameSpace.SetValue(null, name);
        // Only in the navigation pane, not on the desktop.
        using (var hide = Registry.CurrentUser.CreateSubKey(HideKey)) hide.SetValue(clsid, 1, RegistryValueKind.DWord);
        Refresh();
    }

    /// <summary>Removes a synchronisation's entry (nothing happens when there is none).</summary>
    public static void Remove(AppPaths paths, string pairId)
    {
        if (RemoveQuietly(ClsidFor(paths, pairId).ToString("B"))) Refresh();
    }

    /// <summary>The synchronisations of this data folder with an entry.</summary>
    public static IReadOnlyList<string> PairIds(AppPaths paths)
    {
        var prefix = paths.SyncRootProvider + "|";
        var ids = new List<string>();
        using var nameSpace = Registry.CurrentUser.OpenSubKey(NameSpaceKey);
        foreach (var clsid in nameSpace?.GetSubKeyNames() ?? [])
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{clsid}");
            if (key?.GetValue(Marker) is string marker && marker.StartsWith(prefix, StringComparison.Ordinal)) ids.Add(marker[prefix.Length..]);
        }
        return ids;
    }

    /// <summary>Removes every entry of this data folder - before uninstalling, and what tests leave behind.</summary>
    public static int RemoveAll(AppPaths paths)
    {
        var ids = PairIds(paths);
        foreach (var id in ids) RemoveQuietly(ClsidFor(paths, id).ToString("B"));
        if (ids.Count > 0) Refresh();
        return ids.Count;
    }

    private static bool RemoveQuietly(string clsid)
    {
        using (var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{clsid}"))
        {
            // Only CloudDrive-Sync's own entries.
            if (key?.GetValue(Marker) is null) return false;
        }
        Registry.CurrentUser.DeleteSubKeyTree($@"{NameSpaceKey}\{clsid}", throwOnMissingSubKey: false);
        using (var hide = Registry.CurrentUser.OpenSubKey(HideKey, writable: true)) hide?.DeleteValue(clsid, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{clsid}", throwOnMissingSubKey: false);
        return true;
    }

    /// <summary>Explorer shows the change without a restart.</summary>
    private static void Refresh() => SHChangeNotify(0x08000000, 0, 0, 0);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
