using System.Reflection;
using System.Runtime.Versioning;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.OnDemand;

/// <summary>
/// The live parts of one synchronisation with files on demand: its state (<see cref="ItemStore"/>), its registration
/// with Windows and the connection that answers requests for the data of online-only files. Connected as soon as
/// CloudDrive-Sync starts, so files open right after signing in to Windows.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed class OnDemandPair : IDisposable
{
    private const string MergeKey = "merge-next-run";
    private readonly AppPaths _paths;
    private readonly Func<SyncPairSettings?> _pair;
    private readonly Func<AccountSettings?> _account;
    private readonly CloudFetcher _fetcher;
    private readonly Lock _gate = new();
    private SyncRootConnection? _connection;
    // Windows refused a status report: the connection may be gone although Windows still answers questions about it.
    private bool _connectionDoubtful;
    private PinWatcher? _pins;

    public OnDemandPair(AppPaths paths, string pairId, FileServer files, Func<SyncPairSettings?> pair, Func<AccountSettings?> account, Action changedInCloud)
    {
        _paths = paths;
        _pair = pair;
        _account = account;
        Id = pairId;
        Store = ItemStore.Open(Path.Combine(paths.SyncPairDir(pairId), "items.db"));
        _fetcher = new CloudFetcher(files, Store, pair, account()?.Kind == WebDavKind.Nextcloud, changedInCloud);
    }

    public string Id { get; }

    public ItemStore Store { get; }

    /// <summary>The ID of the registration with Windows.</summary>
    public string SyncRootId => _pair() is { } pair ? SyncRoots.IdFor(_paths, pair) : SyncRoots.IdFor(_paths, Id);

    /// <summary>
    /// The registration was lost after the first run: Windows removed the online-only files from the PC then. The next
    /// run merges both sides instead of taking them as deleted on the PC. Kept in the state, so it survives a crash.
    /// </summary>
    public bool MergeNeeded => Store.GetMeta(MergeKey) == "1";

    public void Merged() => Store.SetMeta(MergeKey, "0");

    /// <summary>
    /// Makes sure the folder is registered with Windows (again, should the registration be lost) and connected. Does
    /// nothing when both are in place.
    /// </summary>
    public void EnsureConnected()
    {
        var pair = _pair() ?? throw new CdException("CD-9000", $"unknown synchronisation '{Id}'");
        lock (_gate)
        {
            var registered = SyncRoots.ContextOf(pair.LocalPath) == Id && SyncRoots.IsRegistered(SyncRootId);
            if (registered && _connection is not null)
            {
                if (!_connectionDoubtful && _connection.IsAlive) return;
                // Seen: Windows refuses the connection's status reports and then every online-only file ("Der
                // Clouddateianbieter wird nicht ausgeführt") until the folder is connected again.
                Log.Warn("OnDemand", $"'{Id}': Windows no longer takes the connection to the folder; it is connected again.");
            }
            _connectionDoubtful = false;
            _pins?.Dispose();
            _pins = null;
            _connection?.Dispose();
            _connection = null;
            if (!registered)
            {
                // A folder that is not there (a drive not ready yet) cannot be registered anyway; its registration is not lost.
                if (Directory.Exists(pair.LocalPath) && !Store.IsEmpty)
                {
                    Store.SetMeta(MergeKey, "1");
                    Log.Warn("OnDemand", $"'{Id}': the registration with Windows was lost; the next run merges both sides and deletes nothing.");
                }
                try
                {
                    SyncRoots.Register(Spec(pair));
                    Log.Info("OnDemand", $"'{Id}': folder registered with Windows.");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    throw new CdException("CD-4602", e.Message, e);
                }
            }
            try
            {
                _connection = SyncRootConnection.Connect(pair.LocalPath, _fetcher);
            }
            catch (IOException e)
            {
                throw new CdException("CD-4603", e.Message, e);
            }
            try
            {
                _pins = new PinWatcher(pair.LocalPath, Id);
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // Keeping and freeing still work, only later: each run carries out what was chosen meanwhile.
                Log.Warn("OnDemand", $"'{Id}': pin states not watched: {e.Message}");
            }
        }
    }

    /// <summary>What Explorer shows at the folder itself; nothing while it is not connected.</summary>
    /// <returns>Whether Windows took it; if not, the next <see cref="EnsureConnected"/> connects the folder again.</returns>
    public bool Report(ProviderStatus status)
    {
        lock (_gate)
        {
            if (_connection is null) return false;
            if (_connection.Report(status)) return true;
            _connectionDoubtful = true;
            return false;
        }
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            _pins?.Dispose();
            _pins = null;
            _connection?.Dispose();
            _connection = null;
        }
    }

    /// <summary>
    /// Ends the registration once nothing of it is left (<see cref="Leftovers.EndWhenClear"/>): files whose data is on
    /// the PC become normal files, online-only ones leave the PC (their data stays in the cloud). While a program holds a
    /// placeholder, the registration stays and false is returned - the folder is cleared later, never left with
    /// placeholders nothing can read.
    /// </summary>
    public bool Unregister(TimeSpan wait)
    {
        Disconnect();
        var id = SyncRootId;
        var folder = _pair()?.LocalPath ?? SyncRoots.FolderOf(id);
        if (!SyncRoots.IsRegistered(id)) return folder is null || Leftovers.Count(folder) == 0;
        if (folder is null)
        {
            SyncRoots.Unregister(id);
            return true;
        }
        var ended = Leftovers.EndWhenClear(id, folder, CleanUpSpec(id, folder, Id), wait, out var result);
        Log.Info("OnDemand", ended
            ? $"'{Id}': registration with Windows ended ({result.Removed} online-only file(s) removed, {result.Kept} kept as normal files)."
            : $"'{Id}': {result.Failed.Count} placeholder(s) held by a program; the registration stays until the folder is cleared.");
        return ended;
    }

    /// <summary>How a folder stays registered (or comes back) while what is left of a synchronisation is cleared.</summary>
    public static SyncRootSpec CleanUpSpec(string id, string folder, string context) =>
        new(id, folder, "CloudDrive-Sync – wird aufgeräumt", $"{Environment.ProcessPath},0", "0", context);

    /// <summary>
    /// Gives the folder's entry in Explorer the name from the settings - Windows takes a registration with the same ID as
    /// an update. A folder not registered gets the name with its registration.
    /// </summary>
    public void Rename()
    {
        var pair = _pair() ?? throw new CdException("CD-9000", $"unknown synchronisation '{Id}'");
        lock (_gate)
        {
            if (SyncRoots.IsRegistered(SyncRootId)) SyncRoots.Register(Spec(pair));
        }
        Log.Info("OnDemand", $"'{Id}': name in Explorer changed.");
    }

    private SyncRootSpec Spec(SyncPairSettings pair)
    {
        var name = SyncService.ExplorerNameOf(pair, _account());
        var version = typeof(OnDemandPair).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
        return new SyncRootSpec(SyncRootId, pair.LocalPath, name, $"{Environment.ProcessPath},0", version, Id);
    }

    public void Dispose()
    {
        Disconnect();
        Store.Dispose();
    }
}
