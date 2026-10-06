using System.Reflection;
using System.Runtime.Versioning;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;

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
    public string SyncRootId => SyncRoots.IdFor(_paths, Id);

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
            if (registered && _connection is not null) return;
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
    /// Ends the registration: Windows keeps the files whose data is on the PC as normal files and removes the
    /// online-only ones from the PC (their data stays in the cloud).
    /// </summary>
    public void Unregister()
    {
        Disconnect();
        if (SyncRoots.IsRegistered(SyncRootId)) SyncRoots.Unregister(SyncRootId);
        Log.Info("OnDemand", $"'{Id}': registration with Windows ended.");
    }

    private SyncRootSpec Spec(SyncPairSettings pair)
    {
        var account = _account();
        var name = !string.IsNullOrWhiteSpace(pair.ExplorerName) ? pair.ExplorerName
            : account is null ? "CloudDrive-Sync"
            : pair.RemotePath.Trim('/').Length == 0 ? account.Label
            : $"{account.Label} – {CloudFolderNames.ShowPath(account.Kind, pair.RemotePath)}";
        var version = typeof(OnDemandPair).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
        return new SyncRootSpec(SyncRootId, pair.LocalPath, name, $"{Environment.ProcessPath},0", version, Id);
    }

    public void Dispose()
    {
        Disconnect();
        Store.Dispose();
    }
}
