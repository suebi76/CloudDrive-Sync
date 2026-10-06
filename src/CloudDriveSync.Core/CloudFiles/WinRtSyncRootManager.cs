using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.Versioning;

namespace CloudDriveSync.Core.CloudFiles;

/// <summary>
/// The two calls of Windows.Storage.Provider.StorageProviderSyncRootManager CloudDrive-Sync needs (Register, Unregister),
/// made directly through the COM interfaces of the Windows Runtime. The usual way, Microsoft's projection assembly of
/// the Windows SDK (Microsoft.Windows.SDK.NET.dll, 24 MB), is not used: it is not under an open-source licence, and
/// CloudDrive-Sync ships only open-source parts. Interface IDs and the order of methods come from the Windows SDK
/// (windows.storage.provider.idl, windows.storage.idl, windows.security.cryptography.idl, windows.foundation.idl);
/// every interface starts with the three methods of IUnknown and the three of IInspectable, so its own methods begin
/// at slot 6.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal static unsafe partial class WinRtSyncRootManager
{
    private static readonly Guid SyncRootManagerStatics = new("3E99FBBF-8FE3-4B40-ABC7-F6FC3D74C98E");
    private static readonly Guid SyncRootInfo = new("7C1305C4-99F9-41AC-8904-AB055D654926");
    private static readonly Guid StorageFolderStatics = new("08F327FF-85D5-48B9-AEE9-28511E339F9F");
    private static readonly Guid CryptographicBufferStatics = new("320B7E22-3CB0-4CDF-8663-1D28910065EB");
    private static readonly Guid AsyncInfo = new("00000036-0000-0000-C000-000000000046");

    // Slots of IStorageProviderSyncRootInfo: getter and setter of each property in the order of the IDL.
    private const int PutId = 7, PutContext = 9, PutPath = 11, PutDisplayNameResource = 13, PutIconResource = 15, PutHydrationPolicy = 17,
        PutHydrationPolicyModifier = 19, PutPopulationPolicy = 21, PutInSyncPolicy = 23, PutHardlinkPolicy = 25, PutShowSiblingsAsGroup = 27,
        PutVersion = 29, PutProtectionMode = 31, PutAllowPinning = 33;

    // Values of the enumerations in Windows.Storage.Provider.
    private const int HydrationFull = 2, PopulationAlwaysFull = 2, ProtectionUnknown = 0;
    private const uint ModifierNone = 0, InSyncDefault = 0, HardlinkNone = 0;
    private const int MultiThreaded = 1, ChangedMode = unchecked((int)0x80010106);

    /// <summary>Registers (or updates) a sync root, see <see cref="SyncRoots.Register"/>.</summary>
    public static void Register(SyncRootSpec spec) => InWindowsRuntime(() =>
    {
        nint statics = 0, info = 0, folder = 0, context = 0;
        try
        {
            statics = Factory("Windows.Storage.Provider.StorageProviderSyncRootManager", SyncRootManagerStatics);
            info = Activate("Windows.Storage.Provider.StorageProviderSyncRootInfo", SyncRootInfo);
            folder = FolderFromPath(spec.Path);
            context = Buffer(Encoding.UTF8.GetBytes(spec.Context));
            SetString(info, PutId, spec.Id);
            Check(Call(info, PutContext, context), "Context");
            Check(Call(info, PutPath, folder), "Path");
            SetString(info, PutDisplayNameResource, spec.DisplayName);
            SetString(info, PutIconResource, spec.IconResource);
            Check(Call(info, PutHydrationPolicy, HydrationFull), "HydrationPolicy");
            Check(Call(info, PutHydrationPolicyModifier, ModifierNone), "HydrationPolicyModifier");
            Check(Call(info, PutPopulationPolicy, PopulationAlwaysFull), "PopulationPolicy");
            Check(Call(info, PutInSyncPolicy, InSyncDefault), "InSyncPolicy");
            Check(Call(info, PutHardlinkPolicy, HardlinkNone), "HardlinkPolicy");
            Check(Call(info, PutShowSiblingsAsGroup, (byte)0), "ShowSiblingsAsGroup");
            SetString(info, PutVersion, spec.Version);
            Check(Call(info, PutProtectionMode, ProtectionUnknown), "ProtectionMode");
            Check(Call(info, PutAllowPinning, (byte)1), "AllowPinning");
            // IStorageProviderSyncRootManagerStatics.Register(StorageProviderSyncRootInfo)
            Check(Call(statics, 6, info), "Register");
        }
        finally
        {
            foreach (var pointer in new[] { context, folder, info, statics })
                if (pointer != 0) Marshal.Release(pointer);
        }
    });

    /// <summary>Ends a registration, see <see cref="SyncRoots.Unregister"/>.</summary>
    public static void Unregister(string id) => InWindowsRuntime(() =>
    {
        var statics = Factory("Windows.Storage.Provider.StorageProviderSyncRootManager", SyncRootManagerStatics);
        try
        {
            using var text = new HString(id);
            // IStorageProviderSyncRootManagerStatics.Unregister(HSTRING)
            Check(Call(statics, 7, text.Handle), "Unregister");
        }
        finally
        {
            Marshal.Release(statics);
        }
    });

    /// <summary>StorageFolder.GetFolderFromPathAsync, waited for: the IStorageFolder of a path.</summary>
    private static nint FolderFromPath(string path)
    {
        var statics = Factory("Windows.Storage.StorageFolder", StorageFolderStatics);
        nint operation = 0, info = 0;
        try
        {
            using var text = new HString(path);
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(statics, 6))(statics, text.Handle, &operation), "GetFolderFromPathAsync");
            Check(Marshal.QueryInterface(operation, AsyncInfo, out info), "IAsyncInfo");
            int status;
            var deadline = Environment.TickCount64 + 30_000;
            // IAsyncInfo.Status: 0 started, 1 completed, 2 cancelled, 3 error. The operation finishes on a thread of
            // Windows; asking for its status needs no message loop (unlike a completion handler on the UI thread).
            while (true)
            {
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 7))(info, &status), "Status");
                if (status != 0) break;
                if (Environment.TickCount64 > deadline) throw new TimeoutException($"{path}: no answer from Windows");
                Thread.Sleep(5);
            }
            if (status != 1)
            {
                int error;
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 8))(info, &error), "ErrorCode");
                Check(status == 2 ? unchecked((int)0x800704C7) : error, $"folder {path}");
            }
            nint folder;
            // IAsyncOperation<StorageFolder>.GetResults (after put_Completed and get_Completed)
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(operation, 8))(operation, &folder), "GetResults");
            return folder;
        }
        finally
        {
            if (info != 0)
            {
                // IAsyncInfo.Close
                ((delegate* unmanaged[Stdcall]<nint, int>)Slot(info, 10))(info);
                Marshal.Release(info);
            }
            if (operation != 0) Marshal.Release(operation);
            Marshal.Release(statics);
        }
    }

    /// <summary>CryptographicBuffer.CreateFromByteArray: an IBuffer holding the bytes.</summary>
    private static nint Buffer(byte[] bytes)
    {
        var statics = Factory("Windows.Security.Cryptography.CryptographicBuffer", CryptographicBufferStatics);
        try
        {
            nint buffer;
            fixed (byte* pointer = bytes)
                Check(((delegate* unmanaged[Stdcall]<nint, uint, byte*, nint*, int>)Slot(statics, 9))(statics, (uint)bytes.Length, pointer, &buffer), "CreateFromByteArray");
            return buffer;
        }
        finally
        {
            Marshal.Release(statics);
        }
    }

    private static void SetString(nint self, int slot, string value)
    {
        using var text = new HString(value);
        Check(Call(self, slot, text.Handle), $"slot {slot}");
    }

    private static nint Factory(string className, Guid iid)
    {
        using var name = new HString(className);
        Check(RoGetActivationFactory(name.Handle, iid, out var factory), className);
        return factory;
    }

    private static nint Activate(string className, Guid iid)
    {
        using var name = new HString(className);
        Check(RoActivateInstance(name.Handle, out var instance), className);
        try
        {
            Check(Marshal.QueryInterface(instance, iid, out var wanted), className);
            return wanted;
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    /// <summary>Runs the calls with the Windows Runtime initialised on this thread, as COM requires.</summary>
    private static void InWindowsRuntime(Action calls)
    {
        var result = RoInitialize(MultiThreaded);
        // Already initialised as a single-threaded apartment (the UI thread): that is fine, too.
        if (result < 0 && result != ChangedMode) Marshal.ThrowExceptionForHR(result);
        try
        {
            calls();
        }
        finally
        {
            if (result >= 0) RoUninitialize();
        }
    }

    private static nint Slot(nint self, int slot) => (*(nint**)self)[slot];

    private static int Call(nint self, int slot, nint argument) => ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(self, slot))(self, argument);

    private static int Call(nint self, int slot, int argument) => ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(self, slot))(self, argument);

    private static int Call(nint self, int slot, uint argument) => ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(self, slot))(self, argument);

    private static int Call(nint self, int slot, byte argument) => ((delegate* unmanaged[Stdcall]<nint, byte, int>)Slot(self, slot))(self, argument);

    private static void Check(int result, string what)
    {
        if (result < 0) throw new CloudFileException($"Windows Runtime ({what}): 0x{result:X8} {Marshal.GetExceptionForHR(result)?.Message}", result);
    }

    /// <summary>A Windows Runtime string (HSTRING), deleted at the end.</summary>
    private readonly struct HString : IDisposable
    {
        public HString(string value)
        {
            nint handle;
            fixed (char* pointer = value)
                Check(WindowsCreateString(pointer, (uint)value.Length, &handle), "WindowsCreateString");
            Handle = handle;
        }

        public nint Handle { get; }

        public void Dispose() => WindowsDeleteString(Handle);
    }

    [LibraryImport("combase.dll")]
    private static partial int WindowsCreateString(char* source, uint length, nint* result);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint text);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [LibraryImport("combase.dll")]
    private static partial int RoActivateInstance(nint activatableClassId, out nint instance);

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int initType);

    [LibraryImport("combase.dll")]
    private static partial void RoUninitialize();
}
