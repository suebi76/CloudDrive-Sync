using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CloudDriveSync.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using System.Runtime.Versioning;

namespace CloudDriveSync.Core.CloudFiles;

/// <summary>Answers Windows' requests for the data of online-only files of one sync root.</summary>
public interface IFileFetcher
{
    /// <summary>
    /// Called on a thread of Windows' pool; returns at once and answers through the request (from any thread) - the
    /// program that opened the file waits for it.
    /// </summary>
    void Fetch(FetchRequest request);

    /// <summary>The request with this key is no longer needed (the program gave up waiting).</summary>
    void Cancel(long transferKey);
}

/// <summary>
/// CloudDrive-Sync's connection to a registered sync root: while it exists, Windows passes requests for online-only
/// files to the <see cref="IFileFetcher"/>. Without a connection, opening such a file fails with "the cloud file
/// provider is not running"; files whose data is on the PC keep working. CloudDrive-Sync's own process can never load
/// an online-only file by accident (CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION): only <see cref="Placeholders.Hydrate"/>
/// fetches data on purpose.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed unsafe class SyncRootConnection : IDisposable
{
    private static readonly CF_CALLBACK_REGISTRATION* Callbacks = CreateCallbackTable();
    private readonly IFileFetcher _fetcher;
    private GCHandle _self;
    private CF_CONNECTION_KEY _key;
    private bool _connected;

    private SyncRootConnection(string path, IFileFetcher fetcher)
    {
        Path = path;
        _fetcher = fetcher;
    }

    public string Path { get; }

    public static SyncRootConnection Connect(string path, IFileFetcher fetcher)
    {
        var connection = new SyncRootConnection(System.IO.Path.GetFullPath(path), fetcher);
        connection._self = GCHandle.Alloc(connection);
        try
        {
            CF_CONNECTION_KEY key;
            var flags = CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH |
                CF_CONNECT_FLAGS.CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION;
            fixed (char* pointer = connection.Path)
                Placeholders.Check(PInvoke.CfConnectSyncRoot(new PCWSTR(pointer), Callbacks, (void*)GCHandle.ToIntPtr(connection._self), flags, &key), "CfConnectSyncRoot", connection.Path);
            connection._key = key;
            connection._connected = true;
            return connection;
        }
        catch
        {
            connection._self.Free();
            throw;
        }
    }

    public void Dispose()
    {
        if (_connected)
        {
            _connected = false;
            var result = PInvoke.CfDisconnectSyncRoot(_key);
            if (result.Failed) Log.Warn("CloudFiles", $"Disconnecting {Path} failed: 0x{result.Value:X8}");
        }
        // Windows sends no more requests after the disconnection, so the handle can go.
        if (_self.IsAllocated) _self.Free();
    }

    private static CF_CALLBACK_REGISTRATION* CreateCallbackTable()
    {
        // Lives as long as the process: Windows keeps using the table while any sync root is connected.
        var table = (CF_CALLBACK_REGISTRATION*)NativeMemory.AllocZeroed((nuint)(3 * sizeof(CF_CALLBACK_REGISTRATION)));
        table[0].Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA;
        table[0].Callback = &OnFetchData;
        table[1].Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA;
        table[1].Callback = &OnCancelFetchData;
        table[2].Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE;
        return table;
    }

    // Windows calls these on threads of its own pool; no exception may ever leave them.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        FetchRequest? request = null;
        try
        {
            var connection = (SyncRootConnection)GCHandle.FromIntPtr((nint)info->CallbackContext).Target!;
            var identity = new ReadOnlySpan<byte>(info->FileIdentity, (int)info->FileIdentityLength).ToArray();
            var path = info->VolumeDosName.ToString() + info->NormalizedPath.ToString();
            var process = info->ProcessInfo is null ? null : System.IO.Path.GetFileName(info->ProcessInfo->ImagePath.ToString());
            request = new FetchRequest(info->ConnectionKey, info->TransferKey, info->RequestKey, path, identity, info->FileSize,
                parameters->FetchData.RequiredFileOffset, parameters->FetchData.RequiredLength, process);
            connection._fetcher.Fetch(request);
        }
        catch (Exception e)
        {
            Log.Error("CloudFiles", $"Request for data failed: {e}");
            try
            {
                request?.Fail(FetchFailure.Unsuccessful, request.Offset);
            }
            catch (Exception answer)
            {
                Log.Error("CloudFiles", $"The failure could not be reported to Windows: {answer.Message}");
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnCancelFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        try
        {
            var connection = (SyncRootConnection)GCHandle.FromIntPtr((nint)info->CallbackContext).Target!;
            connection._fetcher.Cancel(info->TransferKey);
        }
        catch (Exception e)
        {
            Log.Error("CloudFiles", $"Cancelling a request failed: {e}");
        }
    }
}
