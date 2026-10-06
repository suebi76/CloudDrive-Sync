using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.System.Ioctl;
using System.Runtime.Versioning;

namespace CloudDriveSync.Core.CloudFiles;

/// <summary>Whether a file of files on demand must stay on this PC, may give its space back, or neither.</summary>
public enum PinState
{
    Unspecified,
    /// <summary>"Always keep on this device".</summary>
    Pinned,
    /// <summary>"Free up space".</summary>
    Unpinned,
    Excluded,
    /// <summary>Only when setting it: take over the state of the folder above.</summary>
    Inherit,
}

/// <summary>What Windows knows about a placeholder: size, how much of it is on the PC, pin state, in sync, identity.</summary>
public sealed record PlaceholderInfo(long Size, long OnDiskSize, PinState Pin, bool InSync, byte[] Identity)
{
    /// <summary>All the file's data is on the PC (always true for folders).</summary>
    public bool IsFullyOnDisk => OnDiskSize >= Size;
}

/// <summary>A placeholder to create: a file or folder of the cloud that is not on the PC yet.</summary>
/// <param name="Identity">Comes back in every request Windows makes for the file (at most 4 KB).</param>
public sealed record NewPlaceholder(string Name, bool IsDirectory, long Size, DateTime ModifiedUtc, byte[] Identity);

/// <summary>Size, write time (UTC ticks) and update sequence number of a file on the PC.</summary>
public sealed record FileVersion(long Size, long LastWriteTicks, long Usn);

/// <summary>A failure of the Cloud Files API; <see cref="Exception.HResult"/> holds Windows' code.</summary>
public sealed class CloudFileException(string message, int hresult) : IOException(message, hresult)
{
    /// <summary>The file is not a placeholder (ERROR_NOT_A_CLOUD_FILE).</summary>
    public bool NotAPlaceholder => HResult == unchecked((int)0x80070178);

    /// <summary>The file was changed on the PC and the change is not uploaded yet (ERROR_CLOUD_FILE_NOT_IN_SYNC).</summary>
    public bool NotInSync => HResult == unchecked((int)0x80070179);
}

/// <summary>
/// The operations on single placeholders that the sync core needs, as thin wrappers around the Cloud Files API
/// (cfapi.h). Handles are opened with as little access as each call allows: an attribute or WRITE_DAC handle does not
/// collide with programs that have the file open, and never loads the file's data.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal static unsafe partial class Placeholders
{
    private const uint FileReadData = 0x1, FileReadAttributes = 0x80, FileWriteAttributes = 0x100, WriteDac = 0x40000, Synchronize = 0x100000;
    private const uint ShareRead = 1, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000;
    private const int SharingViolation = 32;
    private const uint AttributeDirectory = 0x10, AttributeNormal = 0x80;
    public const int MaxIdentityLength = 4096;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    /// <summary>What Windows knows about a placeholder, or null when the file or folder is a normal one.</summary>
    public static PlaceholderInfo? Read(string path)
    {
        using var handle = Open(path, FileReadAttributes);
        var buffer = new byte[sizeof(CF_PLACEHOLDER_STANDARD_INFO) + MaxIdentityLength];
        fixed (byte* pointer = buffer)
        {
            uint returned;
            var result = PInvoke.CfGetPlaceholderInfo(H(handle), CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_STANDARD, pointer, (uint)buffer.Length, &returned);
            if (result.Value == unchecked((int)0x80070178)) return null;
            Check(result, "CfGetPlaceholderInfo", path);
            var info = (CF_PLACEHOLDER_STANDARD_INFO*)pointer;
            var identity = new ReadOnlySpan<byte>((byte*)&info->FileIdentity, (int)info->FileIdentityLength).ToArray();
            var size = Directory.Exists(path) ? 0 : new FileInfo(path).Length;
            return new PlaceholderInfo(size, info->OnDiskDataSize, (PinState)info->PinState, info->InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC, identity);
        }
    }

    /// <summary>
    /// Marks a folder in sync. Windows takes that from a folder whenever something in it is created, renamed or deleted -
    /// also by CloudDrive-Sync itself - and Explorer shows no status for such a folder. False for a folder that is no
    /// placeholder.
    /// </summary>
    public static bool MarkFolderInSync(string path)
    {
        using var handle = Open(path, WriteDac | FileReadAttributes);
        var result = PInvoke.CfSetInSyncState(H(handle), CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC, CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE, null);
        if (result.Value == unchecked((int)0x80070178)) return false;
        Check(result, "CfSetInSyncState", path);
        return true;
    }

    /// <summary>Creates placeholders in a folder; per item null when it was created, otherwise why not.</summary>
    public static IReadOnlyList<string?> Create(string folder, IReadOnlyList<NewPlaceholder> items)
    {
        if (items.Count == 0) return [];
        var infos = new CF_PLACEHOLDER_CREATE_INFO[items.Count];
        var allocations = new List<nint>(items.Count * 2);
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Identity.Length is 0 or > MaxIdentityLength) throw new ArgumentException($"identity of '{item.Name}' must be 1 to {MaxIdentityLength} bytes");
                var name = Marshal.StringToHGlobalUni(item.Name);
                allocations.Add(name);
                var identity = Marshal.AllocHGlobal(item.Identity.Length);
                allocations.Add(identity);
                Marshal.Copy(item.Identity, 0, identity, item.Identity.Length);
                infos[i].RelativeFileName = new PCWSTR((char*)name);
                infos[i].FsMetadata = Metadata(item.IsDirectory, item.Size, item.ModifiedUtc);
                infos[i].FileIdentity = (void*)identity;
                infos[i].FileIdentityLength = (uint)item.Identity.Length;
                // In sync from the start; a folder holds all its entries itself, so Windows never asks for them.
                infos[i].Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC |
                    (item.IsDirectory ? CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION : 0);
            }
            var results = new string?[items.Count];
            fixed (char* folderPointer = Path.GetFullPath(folder))
            fixed (CF_PLACEHOLDER_CREATE_INFO* array = infos)
            {
                uint processed;
                var result = PInvoke.CfCreatePlaceholders(new PCWSTR(folderPointer), array, (uint)infos.Length, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, &processed);
                for (var i = 0; i < infos.Length; i++)
                    results[i] = infos[i].Result.Failed ? $"0x{infos[i].Result.Value:X8}" : null;
                // A failure of the call itself (not of single entries) leaves the entries without a result.
                if (result.Failed && results.All(r => r is null)) Check(result, "CfCreatePlaceholders", folder);
            }
            return results;
        }
        finally
        {
            foreach (var pointer in allocations) Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary>
    /// Turns a normal file or folder into a placeholder that keeps its data (switching a classic synchronisation over);
    /// returns the file's USN afterwards.
    /// </summary>
    public static long Convert(string path, ReadOnlySpan<byte> identity, bool markInSync)
    {
        using var handle = Open(path, WriteDac | FileReadAttributes);
        var flags = markInSync ? CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC : CF_CONVERT_FLAGS.CF_CONVERT_FLAG_NONE;
        long usn;
        fixed (byte* pointer = identity)
            Check(PInvoke.CfConvertToPlaceholder(H(handle), pointer, (uint)identity.Length, flags, &usn, null), "CfConvertToPlaceholder", path);
        return usn;
    }

    /// <summary>
    /// Gives a placeholder the size and time of a new version in the cloud and drops the data of the old one, so the
    /// next opening fetches the new version. Windows refuses (<see cref="CloudFileException.NotInSync"/>) when the file
    /// was changed on the PC and that change is not uploaded yet: a change on the PC is never dropped.
    /// </summary>
    public static void UpdateToNewVersion(string path, long size, DateTime modifiedUtc, ReadOnlySpan<byte> identity)
    {
        using var handle = Open(path, WriteDac | FileReadAttributes);
        var metadata = Metadata(isDirectory: false, size, modifiedUtc);
        fixed (byte* pointer = identity)
            Check(PInvoke.CfUpdatePlaceholder(H(handle), &metadata, pointer, (uint)identity.Length, null, 0,
                CF_UPDATE_FLAGS.CF_UPDATE_FLAG_VERIFY_IN_SYNC | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC,
                null, null), "CfUpdatePlaceholder", path);
    }

    /// <summary>Gives a placeholder another identity, nothing else (its data and state stay).</summary>
    public static void SetIdentity(string path, ReadOnlySpan<byte> identity)
    {
        using var handle = Open(path, WriteDac | FileReadAttributes);
        fixed (byte* pointer = identity)
            Check(PInvoke.CfUpdatePlaceholder(H(handle), null, pointer, (uint)identity.Length, null, 0, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_NONE, null, null),
                "CfUpdatePlaceholder(identity)", path);
    }

    /// <summary>Makes a placeholder a normal file again; its data must be on the PC (otherwise Windows fetches it first).</summary>
    public static void Revert(string path)
    {
        using var handle = Open(path, WriteDac | FileReadAttributes);
        Check(PInvoke.CfRevertPlaceholder(H(handle), CF_REVERT_FLAGS.CF_REVERT_FLAG_NONE, null), "CfRevertPlaceholder", path);
    }

    /// <summary>Fetches all of a file's data on purpose (pinned files); the provider's own requests are served.</summary>
    public static void Hydrate(string path)
    {
        using var handle = Open(path, FileReadData | FileReadAttributes | Synchronize);
        Check(PInvoke.CfHydratePlaceholder(H(handle), 0, -1, CF_HYDRATE_FLAGS.CF_HYDRATE_FLAG_NONE, null), "CfHydratePlaceholder", path);
    }

    /// <summary>Gives a file's space back; it stays visible and comes back from the cloud when opened.</summary>
    public static void Dehydrate(string path)
    {
        using var handle = Open(path, WriteDac | FileReadAttributes);
        Check(PInvoke.CfDehydratePlaceholder(H(handle), 0, -1, CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE, null), "CfDehydratePlaceholder", path);
    }

    public static void SetPinState(string path, PinState state, bool recurse)
    {
        using var handle = Open(path, FileWriteAttributes | FileReadAttributes);
        var flags = recurse ? CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_RECURSE : CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_NONE;
        Check(PInvoke.CfSetPinState(H(handle), (CF_PIN_STATE)state, flags, null), "CfSetPinState", path);
    }

    /// <summary>
    /// The version of a file on the PC: size, write time and update sequence number (USN, it changes with every change
    /// of the file). Taken before an upload, it tells afterwards whether the uploaded data is still what the file holds.
    /// </summary>
    public static FileVersion ReadVersion(string path)
    {
        using var handle = Open(path, FileReadAttributes);
        return VersionOf(handle, path);
    }

    /// <summary>
    /// Marks a placeholder as in sync - only when it is still exactly the version that was uploaded. While it checks,
    /// it holds the file so that no program can write to it in between. False (nothing changed) when the file changed
    /// since, or a program has it open for writing right now: then the next run uploads it again. A version with
    /// USN -1 is compared by size and write time only (all that is remembered between runs).
    /// </summary>
    public static bool MarkInSyncIfUnchanged(string path, FileVersion uploaded)
    {
        // Only reading is shared: no program can open the file for writing while the handle exists, and the open fails
        // when one has it open for writing already. Windows checks sharing only for opens that ask for data access,
        // hence FileReadData (opening does not fetch anything; the data of an uploaded file is on the PC anyway).
        var handle = CreateFile(@"\\?\" + Path.GetFullPath(path), FileReadData | WriteDac | FileReadAttributes, ShareRead, 0, OpenExisting, BackupSemantics, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error == SharingViolation) return false;
            throw new CloudFileException($"open {path}: {Marshal.GetPInvokeErrorMessage(error)}", Marshal.GetHRForLastWin32Error());
        }
        using (handle)
        {
            var now = VersionOf(handle, path);
            if (uploaded.Usn >= 0 ? now != uploaded : now.Size != uploaded.Size || now.LastWriteTicks != uploaded.LastWriteTicks) return false;
            // The USN check of CfSetInSyncState itself does not compare with the file's journal USN (seen in the tests),
            // so the version is compared above, under the protection of this handle.
            Check(PInvoke.CfSetInSyncState(H(handle), CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC, CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE, null), "CfSetInSyncState", path);
            return true;
        }
    }

    /// <summary>
    /// After an upload: makes the file a placeholder of its item (a normal file is converted, a placeholder gets the
    /// item's identity) and marks it in sync when it is still the uploaded version. All of it happens while the file is
    /// held so that nothing can write to it - converting changes the file's USN itself, so the version is compared
    /// before and the in-sync mark set after, under the same protection. False when it could not be marked (changed
    /// since the upload, or open for writing in another program): the next run takes care of it.
    /// </summary>
    public static bool FinishUpload(string path, ReadOnlySpan<byte> identity, bool convert, bool setIdentity, FileVersion uploaded)
    {
        var guard = CreateFile(@"\\?\" + Path.GetFullPath(path), FileReadData | WriteDac | FileReadAttributes, ShareRead, 0, OpenExisting, BackupSemantics, 0);
        if (guard.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            guard.Dispose();
            if (error != SharingViolation) throw new CloudFileException($"open {path}: {Marshal.GetPInvokeErrorMessage(error)}", Marshal.GetHRForLastWin32Error());
            // Open for writing elsewhere: it becomes a placeholder now, in sync only after its next upload.
            if (convert) Convert(path, identity, markInSync: false);
            else if (setIdentity) SetIdentity(path, identity);
            return false;
        }
        using (guard)
        {
            var unchanged = VersionOf(guard, path) == uploaded;
            if (convert) Convert(path, identity, markInSync: false);
            else if (setIdentity) SetIdentity(path, identity);
            if (!unchanged) return false;
            // A handle opened before the conversion still sees a normal file: the mark goes through a new one, while the
            // guard keeps every writer out.
            using var marker = Open(path, WriteDac | FileReadAttributes);
            Check(PInvoke.CfSetInSyncState(H(marker), CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC, CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE, null), "CfSetInSyncState", path);
            return true;
        }
    }

    private static FileVersion VersionOf(SafeFileHandle handle, string path)
    {
        var buffer = new byte[1024];
        fixed (byte* pointer = buffer)
        {
            uint returned;
            if (!PInvoke.DeviceIoControl(H(handle), PInvoke.FSCTL_READ_FILE_USN_DATA, null, 0, pointer, (uint)buffer.Length, &returned, null))
                throw new CloudFileException($"FSCTL_READ_FILE_USN_DATA {path}: {Marshal.GetLastPInvokeErrorMessage()}", Marshal.GetHRForLastWin32Error());
            return new FileVersion(RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle).Ticks, ((USN_RECORD_V2*)pointer)->Usn);
        }
    }

    internal static SafeFileHandle Open(string path, uint access)
    {
        var handle = CreateFile(@"\\?\" + Path.GetFullPath(path), access, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw error is 2 or 3 ? new FileNotFoundException($"not found: {path}", path)
                : new CloudFileException($"open {path}: {Marshal.GetPInvokeErrorMessage(error)}", Marshal.GetHRForLastWin32Error());
        }
        return handle;
    }

    internal static HANDLE H(SafeHandle handle) => (HANDLE)handle.DangerousGetHandle();

    internal static void Check(HRESULT result, string call, string path)
    {
        if (result.Failed) throw new CloudFileException($"{call} {path}: 0x{result.Value:X8} {Marshal.GetExceptionForHR(result.Value)?.Message}", result.Value);
    }

    private static CF_FS_METADATA Metadata(bool isDirectory, long size, DateTime modifiedUtc)
    {
        var time = modifiedUtc.ToFileTimeUtc();
        var metadata = new CF_FS_METADATA { FileSize = isDirectory ? 0 : size };
        metadata.BasicInfo.CreationTime = time;
        metadata.BasicInfo.LastWriteTime = time;
        metadata.BasicInfo.LastAccessTime = time;
        metadata.BasicInfo.ChangeTime = time;
        metadata.BasicInfo.FileAttributes = isDirectory ? AttributeDirectory : AttributeNormal;
        return metadata;
    }
}
