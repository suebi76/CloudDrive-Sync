using System.Text.Json;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

public enum SyncNoticeKind
{
    Conflicts,
    NeedsAttention,
    Error,
}

/// <summary>Something the user should hear about (shown as a Windows notification).</summary>
public sealed record SyncNotice(string PairId, SyncNoticeKind Kind, string Title, string Text);

/// <summary>
/// The synchronisations: adding and removing them, and keeping each one in step - on an interval, shortly after
/// local changes, on request - with at most two runs at the same time. A synchronisation that needs a decision
/// (too many deletions, rebuild, sign-in, missing folder) waits for the user and does nothing on its own.
/// </summary>
public sealed class SyncService : IAsyncDisposable
{
    private const int MaxParallelRuns = 2;
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly AccountService _accounts;
    private readonly RcloneEngine _engine;
    private readonly SyncRunner _runner;
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
        Log.Info("Sync", $"Synchronisation '{pair.Id}' added: {BisyncCommand.CloudPath(pair)} <-> {local}.");
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

    public IReadOnlyList<SyncRunRecord> History(string id, int count = 50)
    {
        var file = Path.Combine(_paths.SyncPairDir(id), "runs.jsonl");
        if (!File.Exists(file)) return [];
        var records = new List<SyncRunRecord>();
        foreach (var line in File.ReadLines(file).Reverse().Take(count))
        {
            try
            {
                if (JsonSerializer.Deserialize<SyncRunRecord>(line, SettingsStore.JsonOptions) is { } record) records.Add(record);
            }
            catch (JsonException)
            {
            }
        }
        return records;
    }

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
        _shutdown.Dispose();
        _slots.Dispose();
    }

    /// <summary>Keeps one synchronisation in step: interval timer, folder watcher, one run at a time.</summary>
    private sealed class PairWorker
    {
        private static readonly TimeSpan LocalChangeDelay = TimeSpan.FromSeconds(5);

        private readonly SyncService _service;
        private readonly string _id;
        private readonly object _lock = new();
        private readonly CancellationTokenSource _stop;
        private readonly PersistedSyncState _saved;
        private FileSystemWatcher? _watcher;
        private Timer? _interval;
        private Timer? _debounce;
        private Timer? _retry;
        // Held during a run - and while the synchronisation is being checked, so both never overlap.
        private readonly SemaphoreSlim _busy = new(1, 1);
        private int _failures;
        private (BisyncMode Mode, bool AnswersDecision)? _pending;
        private CancellationTokenSource? _skipDelay;
        private bool _running;
        private bool _stopped;
        private Task _loop = Task.CompletedTask;
        private DateTime _lastTrashCleanUp = DateTime.MinValue;

        public PairWorker(SyncService service, string id)
        {
            _service = service;
            _id = id;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(service._shutdown.Token);
            _saved = PersistedSyncState.Load(StateFile);
            var pair = service.FindPair(id);
            State = SyncPairState.Initial(id, _saved, pair?.Paused == true) with
            {
                Conflicts = pair is null ? [] : SyncRunner.FindConflicts(pair.LocalPath),
            };
        }

        public SyncPairState State { get; private set; }

        private string StateFile => Path.Combine(_service._paths.SyncPairDir(_id), "state.json");

        public void Start()
        {
            SettingsChanged();
            // The first synchronisation starts at once, later ones shortly after CloudDrive-Sync started.
            Request(BisyncMode.Normal, delay: _saved.FirstSyncDone ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
        }

        /// <summary>Applies changed settings: interval, folder watcher, pause.</summary>
        public void SettingsChanged()
        {
            var pair = _service.FindPair(_id);
            if (pair is null) return;
            lock (_lock)
            {
                if (_stopped) return;
                var period = TimeSpan.FromMinutes(Math.Clamp(pair.IntervalMinutes, 1, 1440));
                _interval?.Dispose();
                _interval = new Timer(_ => Request(BisyncMode.Normal), null, period, period);

                _watcher?.Dispose();
                _watcher = null;
                if (pair.OnLocalChange && Directory.Exists(pair.LocalPath))
                {
                    try
                    {
                        var watcher = new FileSystemWatcher(pair.LocalPath)
                        {
                            IncludeSubdirectories = true,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                            InternalBufferSize = 64 * 1024,
                        };
                        watcher.Created += (_, e) => LocalChange(pair.LocalPath, e.FullPath);
                        watcher.Changed += (_, e) => LocalChange(pair.LocalPath, e.FullPath);
                        watcher.Deleted += (_, e) => LocalChange(pair.LocalPath, e.FullPath);
                        watcher.Renamed += (_, e) => LocalChange(pair.LocalPath, e.FullPath);
                        // Too many changes at once: simply synchronise everything.
                        watcher.Error += (_, _) => Request(BisyncMode.Normal);
                        watcher.EnableRaisingEvents = true;
                        _watcher = watcher;
                    }
                    catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
                    {
                        Log.Warn("Sync", $"Folder watch of '{_id}' not possible, the interval stays: {e.Message}");
                    }
                }
            }
            if (pair.Paused) Publish(State with { Status = SyncStatus.Paused, Activity = "" });
            else if (State.Status == SyncStatus.Paused) Publish(State with { Status = _saved.Decision != SyncDecision.None ? SyncStatus.NeedsAttention : SyncStatus.Idle });
        }

        private void LocalChange(string root, string path)
        {
            var relative = Path.GetRelativePath(root, path);
            var name = Path.GetFileName(path);
            if (relative.StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase) || name.StartsWith("~$", StringComparison.Ordinal) ||
                name.StartsWith(".~lock", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) return;
            lock (_lock)
            {
                if (_stopped) return;
                // Waits until changes have calmed down for a moment (e.g. Office saving a document in several steps).
                _debounce ??= new Timer(_ => Request(BisyncMode.Normal));
                _debounce.Change(LocalChangeDelay, Timeout.InfiniteTimeSpan);
            }
            // Shown at once, so nobody wonders whether the change was noticed.
            if (State.Status == SyncStatus.Idle) Publish(State with { Status = SyncStatus.Waiting, Activity = "Änderung am PC erkannt – wird gleich übertragen …" });
        }

        /// <summary>Runs something that must not overlap a run of this synchronisation (checking it).</summary>
        public async Task<T> ExclusiveAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        {
            await _busy.WaitAsync(cancellationToken);
            try
            {
                return await action();
            }
            finally
            {
                _busy.Release();
            }
        }

        /// <summary>After a failure that passes by itself (a file in use, the network): tries again soon.</summary>
        private void ScheduleRetry(TimeSpan delay)
        {
            lock (_lock)
            {
                if (_stopped) return;
                _retry ??= new Timer(_ => Request(BisyncMode.Normal));
                _retry.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        public void Request(BisyncMode mode, bool answersDecision = false, TimeSpan? delay = null)
        {
            lock (_lock)
            {
                if (_stopped) return;
                var pair = _service.FindPair(_id);
                if (pair is null) return;
                // Automatic runs wait while paused or while a decision is open.
                if (!answersDecision && (pair.Paused || _saved.Decision != SyncDecision.None)) return;
                if (_pending is null || Rank(mode) > Rank(_pending.Value.Mode) || answersDecision) _pending = (mode, answersDecision);
                if (_running)
                {
                    // A request without delay ("now") ends the pause after the start of CloudDrive-Sync.
                    if (delay is null) _skipDelay?.Cancel();
                    return;
                }
                _running = true;
                var wait = delay ?? TimeSpan.Zero;
                _skipDelay?.Dispose();
                _skipDelay = wait > TimeSpan.Zero ? new CancellationTokenSource() : null;
                var skip = _skipDelay?.Token ?? CancellationToken.None;
                _loop = Task.Run(() => LoopAsync(wait, skip));
            }
        }

        private static int Rank(BisyncMode mode) => mode switch { BisyncMode.Resync => 2, BisyncMode.Force => 1, _ => 0 };

        private async Task LoopAsync(TimeSpan delay, CancellationToken skipDelay)
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, skipDelay);
                    try
                    {
                        await Task.Delay(delay, wait.Token);
                    }
                    catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                    {
                        // Asked to synchronise now.
                    }
                }
                while (true)
                {
                    (BisyncMode Mode, bool AnswersDecision) request;
                    lock (_lock)
                    {
                        if (_pending is null || _stopped)
                        {
                            _running = false;
                            return;
                        }
                        request = _pending.Value;
                        _pending = null;
                    }
                    Publish(State with { Status = SyncStatus.Waiting, Activity = "Wartet auf einen freien Platz …" });
                    await _service._slots.WaitAsync(_stop.Token);
                    try
                    {
                        await _busy.WaitAsync(_stop.Token);
                        try
                        {
                            await RunOnceAsync(request.Mode, request.AnswersDecision);
                        }
                        finally
                        {
                            _busy.Release();
                        }
                    }
                    finally
                    {
                        _service._slots.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lock (_lock) _running = false;
            }
            catch (Exception e)
            {
                Log.Error("Sync", $"Synchronisation '{_id}' failed unexpectedly: {e}");
                lock (_lock) _running = false;
                Publish(State with { Status = SyncStatus.Error, Activity = "", ErrorCode = CdException.CodeOf(e), ErrorDetail = e.Message });
            }
        }

        private async Task RunOnceAsync(BisyncMode mode, bool answersDecision)
        {
            var pair = _service.FindPair(_id);
            var account = pair is null ? null : _service._accounts.Find(pair.AccountId);
            if (pair is null || account is null) return;
            if (pair.Paused && !answersDecision)
            {
                Publish(State with { Status = SyncStatus.Paused, Activity = "" });
                return;
            }

            var kind = "Abgleich";
            if (!_saved.FirstSyncDone)
            {
                mode = BisyncMode.Resync;
                kind = "Erster Abgleich";
            }
            else if (mode == BisyncMode.Resync || _saved.ResyncPending)
            {
                // A rebuild that broke off is finished first; it never deletes anything.
                mode = BisyncMode.Resync;
                kind = "Neuaufbau";
            }
            else if (mode == BisyncMode.Force) kind = "Änderungen übernehmen";

            var started = DateTimeOffset.Now;
            var previousConflicts = State.Conflicts.Count;
            Publish(State with { Status = SyncStatus.Syncing, Activity = kind + " …", Progress = JobProgress.None });
            SyncRunOutcome outcome;
            try
            {
                var keepTrash = _service._settings.Current.Preferences.TrashDays > 0;
                outcome = await _service._runner.RunAsync(pair, account, mode, "newer", progress => Publish(State with { Progress = progress }), _stop.Token, keepTrash);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                outcome = new SyncRunOutcome(false, CdException.CodeOf(e), (e as CdException)?.Detail ?? e.Message, SyncDecision.None, JobProgress.None, 0, SyncRunner.FindConflicts(pair.LocalPath), true);
            }

            var finished = DateTimeOffset.Now;
            var previousError = _saved.ErrorCode;
            _saved.LastRun = finished;
            if (mode == BisyncMode.Resync && _saved.FirstSyncDone) _saved.ResyncPending = !outcome.Success;
            _failures = outcome.Success ? 0 : _failures + 1;
            if (outcome.Success)
            {
                _saved.FirstSyncDone = true;
                _saved.LastSuccess = finished;
                _saved.ErrorCode = null;
                _saved.ErrorDetail = null;
                _saved.Decision = SyncDecision.None;
            }
            else
            {
                _saved.ErrorCode = outcome.ErrorCode;
                _saved.ErrorDetail = outcome.ErrorDetail;
                if (outcome.Decision != SyncDecision.None) _saved.Decision = outcome.Decision;
            }
            SaveState();
            // A failure that only repeats itself (e.g. a file still open every minute) is recorded once.
            if (outcome.Success || outcome.ErrorCode != previousError)
            {
                var changes = outcome.Changes ?? [];
                Record(new SyncRunRecord(started, finished - started, outcome.Success, kind, outcome.Final.Transfers, outcome.Final.Bytes, outcome.Deletes,
                    outcome.Conflicts.Count, outcome.ErrorCode, outcome.ErrorDetail, changes.Take(RunChanges.Limit).ToList(), Math.Max(0, changes.Count - RunChanges.Limit)));
            }

            var status = outcome.Success ? (pair.Paused ? SyncStatus.Paused : SyncStatus.Idle)
                : _saved.Decision != SyncDecision.None ? SyncStatus.NeedsAttention : SyncStatus.Error;
            Publish(new SyncPairState(_id, status, "", outcome.Final, _saved.LastRun, _saved.LastSuccess, _saved.ErrorCode, _saved.ErrorDetail,
                _saved.Decision, outcome.Conflicts, _saved.FirstSyncDone));

            var name = Describe(pair, account);
            if (outcome.LocalOnlyAdded is { Count: > 0 } kept)
            {
                _service.KeepLocalOnly(_id, kept);
                _service.Notice?.Invoke(_service, new SyncNotice(_id, SyncNoticeKind.NeedsAttention, name,
                    kept.Count == 1 ? $"„{Path.GetFileName(kept[0])}“ bleibt nur auf diesem PC – der Server hat die Datei nicht angenommen."
                        : $"{kept.Count} Dateien bleiben nur auf diesem PC – der Server hat sie nicht angenommen."));
            }
            if (outcome.Conflicts.Count > previousConflicts)
                _service.Notice?.Invoke(_service, new SyncNotice(_id, SyncNoticeKind.Conflicts, name,
                    $"{outcome.Conflicts.Count - previousConflicts} Konflikt(e): Beide Fassungen sind erhalten. Bitte prüfen."));
            if (!outcome.Success && _saved.Decision != SyncDecision.None)
                _service.Notice?.Invoke(_service, new SyncNotice(_id, SyncNoticeKind.NeedsAttention, name, ErrorCatalog.Get(outcome.ErrorCode ?? "CD-9000").Title));
            else if (!outcome.Success && outcome.ErrorCode != previousError)
                _service.Notice?.Invoke(_service, new SyncNotice(_id, SyncNoticeKind.Error, name, ErrorCatalog.Get(outcome.ErrorCode ?? "CD-9000").Title));

            if (!outcome.Success && outcome.Retryable && _saved.Decision == SyncDecision.None)
            {
                // A file in use costs only a look at the PC, so it is checked every minute; trouble with the network or the
                // server gets more time with each try (1, 2, 4 … minutes, at most the interval).
                var minutes = outcome.ErrorCode == "CD-4510" ? 1 : Math.Min(1 << Math.Min(_failures - 1, 6), Math.Max(1, pair.IntervalMinutes));
                ScheduleRetry(TimeSpan.FromMinutes(minutes));
            }
            CleanTrash(pair);
        }

        private static string Describe(SyncPairSettings pair, AccountSettings account) =>
            pair.RemotePath.Length > 0 ? $"{account.Label} › {pair.RemotePath}" : account.Label;

        private void Publish(SyncPairState state)
        {
            State = state;
            _service.StateChanged?.Invoke(_service, state);
        }

        private void SaveState()
        {
            try
            {
                _saved.Save(StateFile);
            }
            catch (IOException e)
            {
                Log.Warn("Sync", $"State of '{_id}' not saved: {e.Message}");
            }
        }

        private void Record(SyncRunRecord record)
        {
            try
            {
                var file = Path.Combine(_service._paths.SyncPairDir(_id), "runs.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllText(file, JsonSerializer.Serialize(record, new JsonSerializerOptions(SettingsStore.JsonOptions) { WriteIndented = false }) + Environment.NewLine);
                var lines = File.ReadAllLines(file);
                if (lines.Length > 400) File.WriteAllLines(file, lines[^200..]);
            }
            catch (IOException e)
            {
                Log.Warn("Sync", $"Run of '{_id}' not recorded: {e.Message}");
            }
        }

        /// <summary>
        /// Removes recycle bin folders older than the configured days, at most once a day. Switched off, nothing new
        /// arrives there and what is there stays until the user empties it.
        /// </summary>
        private void CleanTrash(SyncPairSettings pair)
        {
            if (DateTime.Now - _lastTrashCleanUp < TimeSpan.FromDays(1)) return;
            _lastTrashCleanUp = DateTime.Now;
            var trash = SyncTrash.FolderOf(pair.LocalPath);
            if (!Directory.Exists(trash)) return;
            var days = _service._settings.Current.Preferences.TrashDays;
            if (days > 0) SyncTrash.CleanUp(pair.LocalPath, TimeSpan.FromDays(days));
            if (Directory.Exists(trash)) File.SetAttributes(trash, File.GetAttributes(trash) | FileAttributes.Hidden);
        }

        public async Task StopAsync()
        {
            Task loop;
            lock (_lock)
            {
                _stopped = true;
                _interval?.Dispose();
                _debounce?.Dispose();
                _retry?.Dispose();
                _watcher?.Dispose();
                loop = _loop;
            }
            await _stop.CancelAsync();
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
            lock (_lock) _skipDelay?.Dispose();
            _stop.Dispose();
        }
    }
}
