using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

public sealed partial class SyncService
{
    /// <summary>
    /// Keeps one synchronisation in step: the interval timer, the folder watcher and retries request runs; the worker
    /// runs them one at a time (within the service's limit of parallel runs), keeps the state and tells the user.
    /// </summary>
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
            // CloudDrive-Sync's own files and the temporary files of Office, LibreOffice and downloads do not count.
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

        /// <summary>
        /// Asks for a run. Requests that arrive while one is waiting or running are merged; the strongest mode wins
        /// (rebuild over "apply the changes" over a normal run). <paramref name="answersDecision"/>: the user answered
        /// an open question, so the run goes ahead although the synchronisation waits for a decision or is paused.
        /// </summary>
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
                outcome = pair.Mode == SyncMode.OnDemand
                    ? await _service.RunOnDemandAsync(pair, account, mode, progress => Publish(State with { Progress = progress }), keepTrash, _stop.Token,
                        activity: text => Publish(State with { Activity = text }))
                    : await _service._runner.RunAsync(pair, account, mode, "newer", progress => Publish(State with { Progress = progress }), _stop.Token, keepTrash);
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
            if (outcome.Space is not null) _saved.Space = outcome.Space;
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
                _saved.Decision, outcome.Conflicts, _saved.FirstSyncDone, _saved.Space));

            Announce(pair, account, outcome, previousConflicts, previousError);
            if (!outcome.Success && outcome.Retryable && _saved.Decision == SyncDecision.None) ScheduleRetry(RetryDelay(outcome, pair));
            CleanTrash(pair);
        }

        /// <summary>Tells the user what needs them: files kept on the PC, new conflicts, a decision, a new error.</summary>
        private void Announce(SyncPairSettings pair, AccountSettings account, SyncRunOutcome outcome, int previousConflicts, string? previousError)
        {
            var name = pair.RemotePath.Length > 0 ? $"{account.Label} › {pair.RemotePath}" : account.Label;
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
        }

        /// <summary>
        /// When to try again after a failure that passes by itself: a file in use costs only a look at the PC, so every
        /// minute; trouble with the network or the server gets more time with each try (1, 2, 4 … minutes, at most the
        /// interval).
        /// </summary>
        private TimeSpan RetryDelay(SyncRunOutcome outcome, SyncPairSettings pair)
        {
            var minutes = outcome.ErrorCode == "CD-4510" ? 1 : Math.Min(1 << Math.Min(_failures - 1, 6), Math.Max(1, pair.IntervalMinutes));
            return TimeSpan.FromMinutes(minutes);
        }

        private void Publish(SyncPairState state)
        {
            State = state;
            _service.StateChanged?.Invoke(_service, state);
        }

        /// <summary>The next run merges both sides - also after a restart (the state remembers it).</summary>
        public void RebuildNext()
        {
            _saved.ResyncPending = true;
            SaveState();
            Request(BisyncMode.Resync, answersDecision: true);
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
                RunHistory.Append(_service._paths.SyncPairDir(_id), record);
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
