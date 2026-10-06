using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.OnDemand;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

/// <summary>
/// The synchronisations: adding and removing them, and keeping each one in step - on an interval, shortly after
/// local changes, on request - with at most two runs at the same time. A synchronisation that needs a decision
/// (too many deletions, rebuild, sign-in, missing folder) waits for the user and does nothing on its own.
/// Each synchronisation has a <see cref="PairWorker"/> (SyncService.PairWorker.cs) that runs it - classic ones with
/// rclone's bisync (<see cref="SyncRunner"/>), those with files on demand with CloudDrive-Sync's own core
/// (<see cref="OnDemandRunner"/>, SyncService.OnDemand.cs).
/// </summary>
public sealed partial class SyncService : IAsyncDisposable
{
    private const int MaxParallelRuns = 2;
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly AccountService _accounts;
    private readonly RcloneEngine _engine;
    private readonly SyncRunner _runner;
    private readonly FileServer _files;
    private readonly SemaphoreSlim _slots = new(MaxParallelRuns, MaxParallelRuns);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, PairWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _started;

    public SyncService(AppPaths paths, SettingsStore settings, AccountService accounts, RcloneEngine engine)
    {
        _paths = paths;
        _settings = settings;
        _accounts = accounts;
        _engine = engine;
        _runner = new SyncRunner(paths, engine);
        _files = new FileServer(engine);
    }

    /// <summary>Raised on any thread whenever the state of a synchronisation changes.</summary>
    public event EventHandler<SyncPairState>? StateChanged;

    public event EventHandler<SyncNotice>? Notice;

    public IReadOnlyList<SyncPairSettings> Pairs => _settings.Current.Syncs;

    public SyncPairSettings? FindPair(string id) => _settings.Current.Syncs.FirstOrDefault(p => p.Id == id);

    public SyncPairState? GetState(string id)
    {
        lock (_gate) return _workers.TryGetValue(id, out var worker) ? worker.State : null;
    }

    public void Start()
    {
        lock (_gate)
        {
            _started = true;
            foreach (var pair in _settings.Current.Syncs) WorkerFor(pair.Id).Start();
        }
        // Files on demand: connected at once, so online-only files open before the first run.
        if (OnDemandSupported)
            foreach (var pair in Pairs.Where(p => p.Mode == SyncMode.OnDemand)) ConnectEarly(pair.Id);
    }

    public IReadOnlyList<FolderWarning> CheckFolder(string localPath, string? exceptPairId = null) =>
        LocalFolderCheck.Check(localPath, Pairs.Where(p => p.Id != exceptPairId));

    public Task<SyncPreviewResult> PreviewAsync(SyncPairSettings draft, CancellationToken cancellationToken = default) =>
        SyncPreview.CalculateAsync(_accounts, draft, cancellationToken);

    /// <summary>
    /// Adds a synchronisation: creates the local folder, places the sentinel file on both sides and starts the
    /// first synchronisation (which merges both sides and deletes nothing).
    /// </summary>
    public async Task<SyncPairSettings> AddAsync(SyncPairSettings draft, CancellationToken cancellationToken = default)
    {
        var account = _accounts.Find(draft.AccountId) ?? throw new CdException("CD-9000", $"unknown account '{draft.AccountId}'");
        var local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(draft.LocalPath));
        if (draft.Mode == SyncMode.OnDemand && OnDemandProblem(local) is { } problem) throw new CdException("CD-4601", problem);
        var remotePath = draft.RemotePath.Trim('/');
        var createdFrom = FirstMissingFolder(local);
        var sentinel = Path.Combine(local, SyncFilters.SentinelFile);
        var newSentinel = !File.Exists(sentinel);
        Directory.CreateDirectory(local);
        bool cloudCheckFile;
        try
        {
            cloudCheckFile = await PlaceSentinelsAsync(account, remotePath, local, tryCloud: true, cancellationToken);
        }
        catch (CdException)
        {
            // A setup that did not come about leaves nothing behind on the PC.
            if (createdFrom is not null) RemoveCreatedFolders(local, createdFrom);
            else if (newSentinel) TryDelete(sentinel);
            throw;
        }

        var pair = new SyncPairSettings
        {
            Id = AccountService.UniqueId($"{account.Id}-{(remotePath.Length > 0 ? Path.GetFileName(remotePath) : "alles")}",
                Pairs.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)),
            AccountId = account.Id,
            RemotePath = remotePath,
            LocalPath = local,
            Mode = draft.Mode,
            ExplorerName = draft.ExplorerName,
            Selection = draft.Selection,
            Conflicts = draft.Conflicts,
            IntervalMinutes = Math.Clamp(draft.IntervalMinutes, 1, 1440),
            OnLocalChange = draft.OnLocalChange,
            MaxDeletePercent = Math.Clamp(draft.MaxDeletePercent, 1, 100),
            Created = DateTimeOffset.Now,
            CloudCheckFile = cloudCheckFile,
        };
        var folder = _paths.SyncPairDir(pair.Id);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        _settings.Update(s => s.Syncs.Add(pair));
        if (pair.Mode == SyncMode.OnDemand && OnDemandSupported)
        {
            try
            {
                ConnectNew(pair.Id);
            }
            catch (CdException)
            {
                // Windows did not take the folder: the synchronisation does not come about.
                await RemoveAsync(pair.Id, cancellationToken);
                throw;
            }
        }
        Log.Info("Sync", $"Synchronisation '{pair.Id}' added ({pair.Mode}): {BisyncCommand.CloudPath(pair)} <-> {local}.");
        lock (_gate)
        {
            if (_started) WorkerFor(pair.Id).Start();
        }
        return pair;
    }

    /// <summary>Ends a synchronisation. Files stay on both sides; only CloudDrive-Sync's own files are removed.</summary>
    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        PairWorker? worker;
        lock (_gate)
        {
            _workers.Remove(id, out worker);
        }
        if (worker is not null) await worker.StopAsync();
        var pair = FindPair(id);
        if (pair is null) return;
        if (pair.Mode == SyncMode.OnDemand && OnDemandSupported) EndOnDemand(id);
        TryDelete(Path.Combine(pair.LocalPath, SyncFilters.SentinelFile));
        try
        {
            var rc = await _engine.EnsureRunningAsync(cancellationToken);
            await rc.CallAsync("operations/deletefile", new JsonObject
            {
                ["fs"] = AccountService.RemoteName(pair.AccountId) + ":",
                ["remote"] = Join(pair.RemotePath, SyncFilters.SentinelFile),
            }, cancellationToken: cancellationToken);
        }
        catch (CdException e)
        {
            Log.Warn("Sync", $"Sentinel file of '{id}' in the cloud stays: {e.Detail}");
        }
        _settings.Update(s => s.Syncs.RemoveAll(p => p.Id == id));
        try
        {
            var folder = _paths.SyncPairDir(id);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (IOException e)
        {
            Log.Warn("Sync", $"State of '{id}' could not be removed: {e.Message}");
        }
        Log.Info("Sync", $"Synchronisation '{id}' removed (files kept on both sides).");
    }

    public void SetPaused(string id, bool paused)
    {
        _settings.Update(s =>
        {
            var pair = s.Syncs.FirstOrDefault(p => p.Id == id);
            if (pair is not null) pair.Paused = paused;
        });
        Worker(id)?.SettingsChanged();
    }

    /// <summary>Changes settings of a synchronisation. A changed selection needs a rebuild (bisync's rule), which follows.</summary>
    public void Update(string id, Action<SyncPairSettings> change)
    {
        string? before = null, after = null;
        _settings.Update(s =>
        {
            var pair = s.Syncs.FirstOrDefault(p => p.Id == id);
            if (pair is null) return;
            before = SyncFilters.Build(pair.Selection, pair.CloudCheckFile);
            change(pair);
            after = SyncFilters.Build(pair.Selection, pair.CloudCheckFile);
        });
        var worker = Worker(id);
        worker?.SettingsChanged();
        if (before != after) worker?.Request(BisyncMode.Resync, answersDecision: true);
    }

    public void RunNow(string id) => Worker(id)?.Request(BisyncMode.Normal);

    public void RunAll()
    {
        List<PairWorker> workers;
        lock (_gate) workers = _workers.Values.ToList();
        foreach (var worker in workers) worker.Request(BisyncMode.Normal);
    }

    /// <summary>Answer to "too many deletions" or "unusually many changes": apply them, or restore by a rebuild.</summary>
    public void ResolveDeletions(string id, bool apply) =>
        Worker(id)?.Request(apply ? BisyncMode.Force : BisyncMode.Resync, answersDecision: true);

    /// <summary>Rebuilds the synchronisation: both sides are merged, nothing is deleted.</summary>
    public void Rebuild(string id) => Worker(id)?.Request(BisyncMode.Resync, answersDecision: true);

    /// <summary>After the account was signed in again.</summary>
    public void Retry(string id) => Worker(id)?.Request(BisyncMode.Normal, answersDecision: true);

    /// <summary>
    /// "Abgleich überprüfen": compares PC and cloud file by file without changing anything; a running synchronisation of
    /// the pair is finished first.
    /// </summary>
    public async Task<VerifyResult> VerifyAsync(string id, bool compareContent, Action<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var pair = FindPair(id) ?? throw new CdException("CD-9000", $"unknown synchronisation '{id}'");
        // rclone would read every file and so fetch every online-only one.
        if (pair.Mode == SyncMode.OnDemand) throw new CdException("CD-9000", "checking a synchronisation with files on demand is not available yet");
        if (!Directory.Exists(pair.LocalPath)) throw new CdException("CD-4501", pair.LocalPath);
        var rc = await _engine.EnsureRunningAsync(cancellationToken);
        Task<VerifyResult> Check() => SyncVerifier.VerifyAsync(rc, _paths, pair, compareContent, progress, cancellationToken);
        return Worker(id) is { } worker ? await worker.ExclusiveAsync(Check, cancellationToken) : await Check();
    }

    /// <summary>Files the server did not take stay on the PC and out of the synchronisation - without a rebuild.</summary>
    public void KeepLocalOnly(string id, IEnumerable<string> paths) => _settings.Update(s =>
    {
        if (s.Syncs.FirstOrDefault(p => p.Id == id) is not { } pair) return;
        foreach (var path in paths)
            if (!pair.LocalOnly.Contains(path, StringComparer.OrdinalIgnoreCase)) pair.LocalOnly.Add(path);
    });

    /// <summary>The files that stayed on the PC take part again; those the server still does not take stay again.</summary>
    public void RetryLocalOnly(string id)
    {
        _settings.Update(s => s.Syncs.FirstOrDefault(p => p.Id == id)?.LocalOnly.Clear());
        Worker(id)?.Request(BisyncMode.Normal, answersDecision: true);
    }

    /// <summary>Puts the sentinel files back (the folder was moved or the file deleted) and rebuilds.</summary>
    public async Task RepairAsync(string id, CancellationToken cancellationToken = default)
    {
        var pair = FindPair(id) ?? throw new CdException("CD-9000", $"unknown synchronisation '{id}'");
        var account = _accounts.Find(pair.AccountId) ?? throw new CdException("CD-9000", $"unknown account '{pair.AccountId}'");
        if (!Directory.Exists(pair.LocalPath)) throw new CdException("CD-4501", pair.LocalPath);
        await PlaceSentinelsAsync(account, pair.RemotePath, pair.LocalPath, tryCloud: pair.CloudCheckFile, cancellationToken);
        Worker(id)?.Request(BisyncMode.Resync, answersDecision: true);
    }

    /// <summary>What the synchronisation deleted or replaced on the PC, newest first.</summary>
    public IReadOnlyList<TrashEntry> Trash(string id) => FindPair(id) is { } pair ? SyncTrash.List(pair.Id, pair.LocalPath) : [];

    /// <summary>
    /// Version 0.1.0 kept a recycle bin folder in the cloud folder as well (IServ, WebDAV). Files and bytes still
    /// there, or null when there is none.
    /// </summary>
    public async Task<(long Files, long Bytes)?> GetCloudTrashAsync(string id, CancellationToken cancellationToken = default)
    {
        if (FindPair(id) is not { } pair) return null;
        try
        {
            var (bytes, count) = await _accounts.GetSizeAsync(pair.AccountId, Join(pair.RemotePath, SyncFilters.TrashFolder), cancellationToken);
            return count > 0 ? (count, bytes) : null;
        }
        catch (CdException)
        {
            // Not there (or the server cannot be reached right now).
            return null;
        }
    }

    /// <summary>Deletes the recycle bin folder of version 0.1.0 in the cloud folder.</summary>
    public async Task RemoveCloudTrashAsync(string id, CancellationToken cancellationToken = default)
    {
        var pair = FindPair(id) ?? throw new CdException("CD-9000", $"unknown synchronisation '{id}'");
        var rc = await _engine.EnsureRunningAsync(cancellationToken);
        await rc.CallAsync("operations/purge", new JsonObject
        {
            ["fs"] = AccountService.RemoteName(pair.AccountId) + ":",
            ["remote"] = Join(pair.RemotePath, SyncFilters.TrashFolder),
        }, TimeSpan.FromMinutes(5), cancellationToken);
        Log.Info("Sync", $"Recycle bin folder of '{id}' in the cloud removed.");
    }

    /// <summary>The finished runs of a synchronisation, newest first.</summary>
    public IReadOnlyList<SyncRunRecord> History(string id, int count = 50) => RunHistory.Read(_paths.SyncPairDir(id), count);

    /// <summary>
    /// Puts the protection file into the folder on the PC and - with <paramref name="tryCloud"/> - into the cloud folder.
    /// False when the server takes no file there (IServ: "Groups" itself, the whole account; folders to read only):
    /// the synchronisation then checks the cloud folder itself (<see cref="CloudFolderCheck"/>).
    /// </summary>
    private async Task<bool> PlaceSentinelsAsync(AccountSettings account, string remotePath, string local, bool tryCloud, CancellationToken cancellationToken)
    {
        var sentinel = Path.Combine(local, SyncFilters.SentinelFile);
        if (!File.Exists(sentinel))
        {
            await File.WriteAllTextAsync(sentinel,
                "CloudDrive-Sync: Diese Datei schützt die Synchronisation. Fehlt sie auf einer Seite, hält CloudDrive-Sync an, statt Dateien zu löschen. Bitte nicht löschen.\r\n",
                cancellationToken);
        }
        File.SetAttributes(sentinel, File.GetAttributes(sentinel) | FileAttributes.Hidden);
        if (!tryCloud) return false;
        var rc = await _engine.EnsureRunningAsync(cancellationToken);
        var cloud = AccountService.RemoteName(account.Id) + ":";
        if (remotePath.Length > 0)
            await rc.CallAsync("operations/mkdir", new JsonObject { ["fs"] = cloud, ["remote"] = remotePath }, cancellationToken: cancellationToken);
        try
        {
            await rc.CallAsync("operations/copyfile", new JsonObject
            {
                ["srcFs"] = local,
                ["srcRemote"] = SyncFilters.SentinelFile,
                ["dstFs"] = cloud,
                ["dstRemote"] = Join(remotePath, SyncFilters.SentinelFile),
            }, TimeSpan.FromMinutes(2), cancellationToken);
        }
        catch (CdException e) when (e.Code is "CD-9000" or "CD-4511")
        {
            // The folder is there, but the server takes no new file in it - servers answer that in many ways (IServ:
            // 500, others 403 or 404). No connection or sign-in still end the setup.
            Log.Info("Sync", $"No protection file in the cloud folder '{remotePath}' ({e.Detail}); it is checked before each run instead.");
            return false;
        }
        return true;
    }

    private PairWorker WorkerFor(string id)
    {
        if (!_workers.TryGetValue(id, out var worker))
        {
            worker = new PairWorker(this, id);
            _workers[id] = worker;
        }
        return worker;
    }

    private PairWorker? Worker(string id)
    {
        lock (_gate) return _workers.TryGetValue(id, out var worker) ? worker : null;
    }

    private static string Join(params string[] parts) => string.Join('/', parts.Select(p => p.Trim('/')).Where(p => p.Length > 0));

    /// <summary>The topmost folder on the way to <paramref name="folder"/> that does not exist yet; null when it exists.</summary>
    private static string? FirstMissingFolder(string folder)
    {
        string? missing = null;
        for (var current = folder; current is not null && !Directory.Exists(current); current = Path.GetDirectoryName(current))
            missing = current;
        return missing;
    }

    /// <summary>Removes the folders a setup created itself, as long as nothing but the sentinel file is in them.</summary>
    private static void RemoveCreatedFolders(string folder, string topmost)
    {
        TryDelete(Path.Combine(folder, SyncFilters.SentinelFile));
        for (var current = folder; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                Directory.Delete(current, recursive: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
            if (string.Equals(current, topmost, StringComparison.OrdinalIgnoreCase)) return;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        List<PairWorker> workers;
        lock (_gate) workers = _workers.Values.ToList();
        foreach (var worker in workers) await worker.StopAsync();
        if (OnDemandSupported) DisconnectAll();
        _files.Dispose();
        _shutdown.Dispose();
        _slots.Dispose();
    }
}
