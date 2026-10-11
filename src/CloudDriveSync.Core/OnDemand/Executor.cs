using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.OnDemand;

/// <summary>What carrying out a plan did, for the run's result and the activity list.</summary>
/// <param name="CloudChanged">Steps left alone because the cloud no longer had the version the plan was made with.</param>
/// <param name="ServerLocked">Files someone has open in the cloud ("423 Locked"): tried again later.</param>
internal sealed record ExecutionResult(
    IReadOnlyList<FileChange> Changes,
    long Transfers,
    long Bytes,
    int Deletes,
    IReadOnlyList<string> LocalOnlyAdded,
    IReadOnlyList<string> Locked,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Broken,
    int HeldBack,
    int CloudChanged,
    IReadOnlyList<string> ServerLocked);

/// <summary>
/// Carries out a <see cref="SyncPlan"/>, step by step, each step recorded in the <see cref="ItemStore"/> as soon as it
/// is done - a run that breaks off continues where it stopped. Before a step changes anything it looks again: an upload
/// or a deletion in the cloud happens only while the cloud still has the known version, a deletion on the PC only while
/// the file is unchanged there. A step whose side changed meanwhile is left for the next run, which then sees the
/// change. Order: moves, folders, uploads, conflicts, new placeholders, refreshed ones, deletions (files before their
/// folders, which go only when empty). What the server does not let be changed (e.g. a share to read only) is never lost
/// and never tried again in every run: what may not be deleted or moved comes back from the server, a change of one's own
/// stays on the PC beside the server's version, and what may not be added stays on the PC.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed partial class Executor
{
    private readonly RcClient _rc;
    private readonly FileServer _files;
    private readonly ItemStore _store;
    private readonly SyncPairSettings _pair;
    private readonly AccountSettings _account;
    private readonly bool _keepTrash;
    private readonly Action<JobProgress>? _progress;
    private readonly CancellationToken _cancel;
    private readonly string _cloudFs;
    private readonly string _remote;
    private readonly bool _withHashes;
    private readonly HashSet<string> _takenNames;
    private readonly List<FileChange> _changes = [];
    private readonly List<string> _localOnly = [];
    private readonly List<string> _locked = [];
    private readonly List<string> _failed = [];
    private readonly List<string> _toFetch = [];
    private readonly List<string> _broken = [];
    private readonly List<string> _serverLocked = [];
    // New folders the server did not let be made: nothing below them is tried in this run.
    private readonly List<string> _refusedFolders = [];
    // New places of moves the server did not allow: the plan's further steps there are left to the next run.
    private readonly List<string> _undone = [];
    private readonly Func<string, bool> _isBroken;
    private bool _placeholdersStopped;
    private int _heldBack;
    private int _cloudChanged;
    private string? _trashStamp;
    private long _transfers;
    private long _bytes;
    // How far the run is, for its card: steps done of all, the bytes of the uploads, what is being done right now.
    private int _total;
    private int _done;
    private long _totalBytes;
    private long _currentBytes;
    private string? _current;
    private long _lastReport;
    private readonly System.Diagnostics.Stopwatch _uploading = new();
    private int _deletes;

    /// <param name="placeholdersStopped">No new placeholders: Windows wrote some broken before (<see cref="PlaceholderAlarm"/>).</param>
    /// <param name="isBroken">Whether Windows wrote a new placeholder broken; tests stand in for Windows.</param>
    public Executor(RcClient rc, FileServer files, ItemStore store, SyncPairSettings pair, AccountSettings account, bool keepTrash,
        IEnumerable<string> cloudPaths, Action<JobProgress>? progress, CancellationToken cancel, bool placeholdersStopped = false, Func<string, bool>? isBroken = null)
    {
        _placeholdersStopped = placeholdersStopped;
        _isBroken = isBroken ?? Placeholders.IsBrokenAt;
        _rc = rc;
        _files = files;
        _store = store;
        _pair = pair;
        _account = account;
        _keepTrash = keepTrash;
        _progress = progress;
        _cancel = cancel;
        _cloudFs = BisyncCommand.CloudPath(pair);
        _remote = AccountService.RemoteName(pair.AccountId) + ":";
        _withHashes = account.Kind == WebDavKind.Nextcloud;
        _takenNames = new HashSet<string>(cloudPaths, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ExecutionResult> RunAsync(SyncPlan plan)
    {
        var actions = plan.Actions;
        _total = actions.Count;
        _totalBytes = actions.OfType<Upload>().Sum(u => u.Local.Size);
        foreach (var move in actions.OfType<MoveCloud>()) await StepAsync(move.NewPath, () => MoveCloudAsync(move), "Verschiebt in der Cloud");
        foreach (var folder in actions.OfType<CreateCloudFolder>().OrderBy(a => Depth(a.Path))) await StepAsync(folder.Path, () => CreateCloudFolderAsync(folder), "Legt in der Cloud an");
        foreach (var adopt in actions.OfType<Adopt>().OrderBy(a => a.Cloud.IsDirectory ? 0 : 1).ThenBy(a => Depth(a.Path))) await StepAsync(adopt.Path, () => AdoptAsync(adopt));
        foreach (var upload in actions.OfType<Upload>()) await StepAsync(upload.Path, () => UploadAsync(upload.Local, upload.Item), Uploading);
        foreach (var mark in actions.OfType<MarkInSync>()) await StepAsync(mark.Path, () => MarkAsync(mark));
        foreach (var conflict in actions.OfType<Conflict>()) await StepAsync(conflict.Path, () => ResolveAsync(conflict), "Behält beide Fassungen");
        await CreatePlaceholdersAsync(actions.OfType<CreatePlaceholder>().Select(a => a.Cloud).ToList());
        foreach (var refresh in actions.OfType<RefreshPlaceholder>()) await StepAsync(refresh.Path, () => RefreshAsync(refresh), "Aktualisiert");
        foreach (var remove in actions.OfType<RemoveLocal>().OrderBy(a => a.Item.IsDirectory ? 1 : 0).ThenByDescending(a => Depth(a.Path)))
            await StepAsync(remove.Path, () => RemoveLocalAsync(remove), "Entfernt vom PC");
        foreach (var remove in actions.OfType<RemoveCloud>().OrderBy(a => a.Item.IsDirectory ? 1 : 0).ThenByDescending(a => Depth(a.Path)))
            await StepAsync(remove.Path, () => RemoveCloudAsync(remove), "Löscht in der Cloud");
        foreach (var drop in actions.OfType<DropPlaceholder>().OrderByDescending(a => Depth(a.Path))) await StepAsync(drop.Path, () => DropAsync(drop));
        foreach (var forget in actions.OfType<Forget>().OrderBy(a => a.Item.IsDirectory ? 1 : 0).ThenByDescending(a => Depth(a.Item.Path)))
            await StepAsync(forget.Item.Path, () => ForgetAsync(forget));
        // Files that were on the PC before their new version arrived come again - after everything else.
        _total += _toFetch.Count;
        foreach (var path in _toFetch) await StepAsync(path, () => FetchAsync(path), Downloading);
        Report(force: true);
        return new ExecutionResult(_changes, _transfers, _bytes, _deletes, _localOnly, _locked, _failed, _broken, _heldBack, _cloudChanged, _serverLocked);
    }

    /// <summary>
    /// One step. What only concerns this file is noted and the run goes on; trouble with the connection or the sign-in
    /// ends the run (the next one picks up from the recorded state).
    /// </summary>
    /// <param name="doing">What the card shows meanwhile, with the file's name; null for quick steps.</param>
    private async Task StepAsync(string path, Func<Task> step, string? doing = null)
    {
        _cancel.ThrowIfCancellationRequested();
        if (Undone(path))
        {
            _done++;
            return;
        }
        if (doing is not null)
        {
            _current = $"{doing}: {Name(path)}";
            // A transfer can take a while: its name shows at once. Other steps (thousands of deletions) are summed up.
            Report(force: doing is Uploading or Downloading);
        }
        try
        {
            await step();
        }
        catch (CdException e) when (e.Code is "CD-5001" or "CD-5003" or "CD-3012" or "CD-3006")
        {
            throw;
        }
        catch (CdException e) when (e.Code == "CD-4511")
        {
            Log.Info("OnDemand", $"'{_pair.Id}': the server did not take '{path}'; it stays on the PC.");
            KeepOnPc(path);
        }
        catch (CdException e) when (e.Code == "CD-4513")
        {
            Log.Info("OnDemand", $"'{_pair.Id}': '{path}' is locked in the cloud (someone has it open); it follows later.");
            _serverLocked.Add(path);
        }
        catch (Exception e) when (IsInUse(e))
        {
            Log.Info("OnDemand", $"'{_pair.Id}': '{path}' is open in another program; it follows later.");
            _locked.Add(path);
        }
        catch (Exception e) when (e is CdException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("OnDemand", $"'{_pair.Id}': '{path}' left for the next run: {(e as CdException)?.Detail ?? e.Message}");
            _failed.Add(path);
        }
        finally
        {
            _done++;
            _current = null;
            _currentBytes = 0;
            Report(force: false);
        }
    }

    private const string Uploading = "Lädt hoch", Downloading = "Lädt herunter";

    /// <summary>Tells the card how far the run is - at most four times a second, unless a transfer begins.</summary>
    private void Report(bool force)
    {
        if (_progress is null) return;
        var now = Environment.TickCount64;
        if (!force && now - _lastReport < 250) return;
        _lastReport = now;
        var seconds = _uploading.Elapsed.TotalSeconds;
        var speed = seconds >= 1 ? (_bytes + _currentBytes) / seconds : 0;
        _progress(new JobProgress(_bytes + _currentBytes, _totalBytes, Math.Min(_done, _total), _total, speed, 0, 0, _failed.Count, Current: _current));
    }

    /// <summary>The server refused a change for good (e.g. a share to read only, a type it does not take) - no passing trouble.</summary>
    private static bool Refused(CdException e) => e.Code == "CD-4511" || (e.Code == "CD-9000" && RunSafety.IsRefusal(e.Detail ?? ""));

    /// <summary>
    /// At or below the new place of a move the server did not allow: the plan took the move as done, so its further steps
    /// there no longer fit - the next run plans them afresh.
    /// </summary>
    private bool Undone(string path) => _undone.Any(place => path == place || path.StartsWith(place + "/", StringComparison.Ordinal));

    /// <summary>Lies in a new folder the server did not let be made in this run (it stays on the PC with everything in it).</summary>
    private bool BelowRefused(string path) => _refusedFolders.Any(folder => path.StartsWith(folder + "/", StringComparison.Ordinal));

    /// <summary>Stays on the PC and out of the synchronisation from now on (see SyncPairSettings.LocalOnly).</summary>
    private void KeepOnPc(string path)
    {
        _localOnly.Add(path);
        _changes.Add(new FileChange(ChangeKind.KeptOnPc, path));
    }

    private async Task MoveCloudAsync(MoveCloud move)
    {
        // Look again: a place taken in the cloud since the listing is never overwritten (the next run keeps both). Only
        // the spelling changing is the same place - on a server that ignores case the stat finds the item itself.
        if (!string.Equals(move.Item.Path, move.NewPath, StringComparison.OrdinalIgnoreCase)
            && await _files.StatAsync(_remote, CloudFetcher.CloudPath(_pair, move.NewPath), withHash: false, _cancel) is not null)
        {
            Log.Info("OnDemand", $"'{_pair.Id}': '{move.NewPath}' was taken in the cloud meanwhile; '{move.Item.Path}' not moved.");
            _cloudChanged++;
            return;
        }
        try
        {
            if (move.Item.IsDirectory)
            {
                // A whole folder: one move on the server where it can (WebDAV MOVE), nothing is uploaded again.
                await _rc.CallAsync("sync/move", new JsonObject
                {
                    ["srcFs"] = $"{_cloudFs}/{move.Item.Path}",
                    ["dstFs"] = $"{_cloudFs}/{move.NewPath}",
                    ["createEmptySrcDirs"] = true,
                    ["deleteEmptySrcDirs"] = true,
                }, TimeSpan.FromMinutes(30), _cancel);
            }
            else
            {
                await _rc.CallAsync("operations/movefile", new JsonObject
                {
                    ["srcFs"] = _cloudFs,
                    ["srcRemote"] = move.Item.Path,
                    ["dstFs"] = _cloudFs,
                    ["dstRemote"] = move.NewPath,
                }, TimeSpan.FromMinutes(5), _cancel);
            }
        }
        catch (CdException e) when (Refused(e))
        {
            // Gone in the cloud meanwhile: the next run sees it (the moved entry is then a new one on the PC).
            if (await _files.StatAsync(_remote, CloudFetcher.CloudPath(_pair, move.Item.Path), withHash: false, _cancel) is null)
            {
                Log.Info("OnDemand", $"'{_pair.Id}': '{move.Item.Path}' is gone in the cloud meanwhile; not moved.");
                _cloudChanged++;
                return;
            }
            UndoMove(move, e.Detail);
            return;
        }
        _store.Move(move.Item.Path, move.NewPath);
        _takenNames.Add(move.NewPath);
        Log.Info("OnDemand", $"'{_pair.Id}': moved '{move.Item.Path}' to '{move.NewPath}' in the cloud.");
    }

    private async Task CreateCloudFolderAsync(CreateCloudFolder folder)
    {
        if (BelowRefused(folder.Path)) return;
        try
        {
            await _rc.CallAsync("operations/mkdir", new JsonObject { ["fs"] = _cloudFs, ["remote"] = folder.Path }, TimeSpan.FromMinutes(2), _cancel);
        }
        catch (CdException e) when (Refused(e))
        {
            // Nothing may be added here (e.g. a share to read only): the folder stays on the PC with everything in it, and
            // nothing below it is tried - not in this run, and no more from now on.
            _refusedFolders.Add(folder.Path);
            throw new CdException("CD-4511", e.Detail, e);
        }
        var stat = await _files.StatAsync(_remote, CloudFetcher.CloudPath(_pair, folder.Path), withHash: false, _cancel);
        var id = _store.Upsert(new SyncItem(0, folder.Path, true, 0, stat?.Ticks ?? DateTime.UtcNow.Ticks, null, null, null));
        MakePlaceholder(folder.Local, id);
        _takenNames.Add(folder.Path);
    }

    private Task AdoptAsync(Adopt adopt)
    {
        var full = LocalFull(adopt.Path);
        if (adopt.Cloud.IsDirectory)
        {
            var id = _store.Upsert(new SyncItem(0, adopt.Cloud.Path, true, 0, adopt.Cloud.Ticks, null, null, null));
            MakePlaceholder(adopt.Local, id);
            return Task.CompletedTask;
        }
        // Where the server has a checksum, the same size is not enough: different content is a conflict after all.
        if (adopt.Cloud.Hash is not null && adopt.Local.OnDisk && !LocalHashMatches(full, adopt.Cloud.Hash))
            return ResolveAsync(new Conflict(adopt.Local, adopt.Cloud, null));
        var version = Placeholders.ReadVersion(full);
        var itemId = _store.Upsert(new SyncItem(0, adopt.Cloud.Path, false, adopt.Cloud.Size, adopt.Cloud.Ticks, adopt.Cloud.Hash, version.Size, version.LastWriteTicks));
        if (adopt.Local.OnDisk) FinishUpload(adopt.Local, itemId, version);
        else MakePlaceholder(adopt.Local, itemId);
        return Task.CompletedTask;
    }

    /// <summary>Uploads a new or changed file and makes it a placeholder in step with the uploaded version.</summary>
    private async Task UploadAsync(LocalEntry local, SyncItem? known)
    {
        if (BelowRefused(local.Path)) return;
        var full = LocalFull(local.Path);
        var version = Placeholders.ReadVersion(full);
        // The cloud may have changed since the listing: then this is a conflict for the next run, nothing is overwritten.
        var before = await _files.StatAsync(_remote, CloudFetcher.CloudPath(_pair, local.Path), _withHashes, _cancel);
        if (before is not null && (known is null || before.Value.Size != known.CloudSize || before.Value.Ticks != known.CloudTicks))
        {
            Log.Info("OnDemand", $"'{_pair.Id}': '{local.Path}' changed in the cloud meanwhile; the next run handles it.");
            _cloudChanged++;
            return;
        }
        _uploading.Start();
        try
        {
            await CopyUpAsync(local);
        }
        catch (CdException e) when (Refused(e))
        {
            // The server has the file but does not let it be changed (e.g. a share to read only): the change stays beside it.
            if (known is not null && before is not null)
            {
                KeepBeside(local, known, e.Detail);
                return;
            }
            // A new file the server does not take (e.g. a type it blocks, answered with an error page; a folder to read
            // only): it stays on the PC and is not tried again in every run.
            throw new CdException("CD-4511", e.Detail, e);
        }
        finally
        {
            _uploading.Stop();
        }
        var after = await _files.StatAsync(_remote, CloudFetcher.CloudPath(_pair, local.Path), _withHashes, _cancel)
            ?? throw new CdException("CD-4605", $"{local.Path}: not in the cloud after the upload");
        var id = _store.Upsert(new SyncItem(known?.Id ?? 0, local.Path, false, after.Size, after.Ticks, after.Hash, version.Size, version.LastWriteTicks));
        FinishUpload(local, id, version);
        _takenNames.Add(local.Path);
        _transfers++;
        _bytes += version.Size;
        _changes.Add(new FileChange(ChangeKind.Uploaded, local.Path));
    }

    /// <summary>A large file goes up as a job of the engine, so the card shows how far it is; a small one in one call.</summary>
    private async Task CopyUpAsync(LocalEntry local)
    {
        var body = new JsonObject
        {
            ["srcFs"] = _pair.LocalPath,
            ["srcRemote"] = local.Path,
            ["dstFs"] = _cloudFs,
            ["dstRemote"] = local.Path,
            // The run knows the file changed. On servers without times of their own rclone compares sizes only and would
            // skip a change that keeps the size. One more try at most: rclone tries a whole upload up to ten times - with a
            // file someone has open ("423 Locked") that held the run up for minutes; the next run tries again anyway.
            ["_config"] = new JsonObject { ["IgnoreTimes"] = true, ["LowLevelRetries"] = 2 },
        };
        if (local.Size < LiveProgressFrom || _progress is null)
        {
            await _rc.CallAsync("operations/copyfile", body, TimeSpan.FromHours(6), _cancel);
            return;
        }
        // One group of statistics per synchronisation, started afresh for each file (the engine keeps every group).
        var group = $"upload/{_pair.Id}";
        try
        {
            await _rc.CallAsync("core/stats-reset", new JsonObject { ["group"] = group }, cancellationToken: _cancel);
        }
        catch (CdException)
        {
            // Without fresh statistics only the shown progress of this file is off.
        }
        var result = await _rc.RunJobAsync("operations/copyfile", body, group, progress =>
        {
            _currentBytes = progress.Bytes;
            Report(force: false);
        }, _cancel);
        if (!result.Success) throw new CdException(ErrorCatalog.Classify(result.Error), $"operations/copyfile: {result.Error}");
    }

    /// <summary>From this size on, an upload shows its progress while it runs.</summary>
    private const long LiveProgressFrom = 4L << 20;

    private Task MarkAsync(MarkInSync mark)
    {
        Placeholders.MarkInSyncIfUnchanged(LocalFull(mark.Path), new FileVersion(mark.Item.LocalSize ?? -1, mark.Item.LocalTicks ?? -1, -1));
        return Task.CompletedTask;
    }

    /// <summary>
    /// New placeholders, folder by folder (outer folders first), recorded in one transaction per folder. Each is opened
    /// once right after: should Windows have written one broken, no more are made (<see cref="PlaceholderAlarm"/>).
    /// </summary>
    private Task CreatePlaceholdersAsync(IReadOnlyList<CloudEntry> entries)
    {
        foreach (var group in entries.Where(e => !Undone(e.Path)).GroupBy(e => Parent(e.Path)).OrderBy(g => g.Key.Length == 0 ? -1 : Depth(g.Key)))
        {
            _cancel.ThrowIfCancellationRequested();
            _done += group.Count();
            if (_placeholdersStopped)
            {
                _heldBack += group.Count();
                continue;
            }
            _current = $"Legt an: {(group.Key.Length == 0 ? Path.GetFileName(_pair.LocalPath) : Name(group.Key))}";
            Report(force: false);
            var folder = group.Key.Length == 0 ? _pair.LocalPath : LocalFull(group.Key);
            if (!Directory.Exists(folder))
            {
                _failed.AddRange(group.Select(e => e.Path));
                continue;
            }
            var items = group.OrderBy(e => e.IsDirectory ? 0 : 1).ToList();
            var created = new List<CloudEntry>();
            try
            {
                CreateIn(folder, items, created);
            }
            catch (Exception e) when (e is CloudFileException or IOException or UnauthorizedAccessException)
            {
                // Windows refuses the whole folder (e.g. it is broken itself): its entries wait, the rest goes on.
                created.Clear();
                _failed.AddRange(items.Select(i => i.Path));
                Log.Warn("OnDemand", $"'{_pair.Id}': {items.Count} placeholder(s) in '{group.Key}' not created: {e.Message}");
                continue;
            }
            // Broken ones stay recorded: they are there, refused by Windows, and never taken as deleted.
            var broken = created.Where(entry => _isBroken(LocalFull(entry.Path))).ToList();
            if (broken.Count > 0)
            {
                _broken.AddRange(broken.Select(entry => entry.Path));
                created.RemoveAll(broken.Contains);
                _placeholdersStopped = true;
                Log.Error("OnDemand", $"'{_pair.Id}': Windows wrote {broken.Count} new placeholder(s) broken, e.g. '{broken[0].Path}'; no more are made.");
            }
            // In a folder kept on this device, what is new is kept, too - also in new folders below it (outer ones come first).
            var keep = PinnedFolder(folder);
            foreach (var entry in created)
            {
                _takenNames.Add(entry.Path);
                if (keep) Placeholders.SetPinState(LocalFull(entry.Path), PinState.Pinned, recurse: false);
                if (entry.IsDirectory) continue;
                _changes.Add(new FileChange(ChangeKind.Downloaded, entry.Path));
                if (keep) _toFetch.Add(entry.Path);
            }
        }
        _current = null;
        return Task.CompletedTask;
    }

    /// <summary>The placeholders of one folder, recorded in one transaction - all of them or, when Windows refuses the folder, none.</summary>
    private void CreateIn(string folder, List<CloudEntry> items, List<CloudEntry> created)
    {
        _store.Batch(() =>
        {
            var ids = items.Select(e => _store.Upsert(new SyncItem(0, e.Path, e.IsDirectory, e.Size, e.Ticks, e.Hash, e.IsDirectory ? null : e.Size, e.IsDirectory ? null : TimeOf(e.Ticks).Ticks))).ToList();
            var results = Placeholders.Create(folder, items.Select((e, i) =>
                new NewPlaceholder(NameEncoding.ToLocalName(Name(e.Path)), e.IsDirectory, e.Size, TimeOf(e.Ticks), ItemIdentity.Encode(ids[i], e.Path))).ToList());
            for (var i = 0; i < items.Count; i++)
            {
                if (results[i] is null)
                {
                    created.Add(items[i]);
                    continue;
                }
                // Not created (a file of that name appeared meanwhile): not recorded either, so it is never taken as deleted.
                _store.Remove(ids[i]);
                _failed.Add(items[i].Path);
                Log.Warn("OnDemand", $"'{_pair.Id}': placeholder '{items[i].Path}' not created: {results[i]}");
            }
        });
    }

    private Task RefreshAsync(RefreshPlaceholder refresh)
    {
        var full = LocalFull(refresh.Path);
        var cloud = refresh.Cloud;
        var wasOnDisk = refresh.Local.Placeholder is { OnDiskSize: > 0 } || refresh.Local.Placeholder?.Pin == PinState.Pinned;
        // Refused by Windows when the file was changed on the PC meanwhile: then the next run sees a conflict.
        Placeholders.UpdateToNewVersion(full, cloud.Size, TimeOf(cloud.Ticks), ItemIdentity.Encode(refresh.Item.Id, refresh.Path));
        _store.Update(refresh.Item with { CloudSize = cloud.Size, CloudTicks = cloud.Ticks, CloudHash = cloud.Hash, LocalSize = cloud.Size, LocalTicks = TimeOf(cloud.Ticks).Ticks });
        _changes.Add(new FileChange(ChangeKind.Downloaded, refresh.Path));
        if (wasOnDisk) _toFetch.Add(refresh.Path);
        return Task.CompletedTask;
    }

    /// <summary>
    /// An item gone from both listings. Its placeholder may still lie on the PC, outside the listing: the selection
    /// changed. Then it is no longer part of the synchronisation - a file whose data is on the PC stays as a normal file
    /// (like a folder that is no longer selected in classic synchronisations), an online-only one goes (its data stays in
    /// the cloud), and a folder goes when nothing stayed in it.
    /// </summary>
    private Task ForgetAsync(Forget forget)
    {
        var full = LocalFull(forget.Item.Path);
        if (!forget.Item.IsDirectory && File.Exists(full) && Placeholders.Read(full) is { } file)
        {
            if (file.IsFullyOnDisk || !file.InSync) Placeholders.Revert(full);
            else File.Delete(full);
            Log.Info("OnDemand", $"'{_pair.Id}': '{forget.Item.Path}' is no longer synchronised; {(file.IsFullyOnDisk || !file.InSync ? "it stays on the PC" : "it stays in the cloud")}.");
        }
        else if (forget.Item.IsDirectory && Directory.Exists(full) && Placeholders.Read(full) is not null)
        {
            if (Directory.EnumerateFileSystemEntries(full).Any()) Placeholders.Revert(full);
            else Directory.Delete(full);
        }
        _store.Remove(forget.Item.Id);
        return Task.CompletedTask;
    }

    private Task FetchAsync(string path)
    {
        // Fetched like any program's request; the data goes through this program's own fetcher.
        Placeholders.Hydrate(LocalFull(path));
        _transfers++;
        return Task.CompletedTask;
    }

    private Task RemoveLocalAsync(RemoveLocal remove)
    {
        var full = LocalFull(remove.Path);
        if (!Path.Exists(full))
        {
            _store.Remove(remove.Item.Id);
            return Task.CompletedTask;
        }
        if (remove.Item.IsDirectory)
        {
            // Only an empty folder goes; what is still in it stays (and goes up again with the next run).
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
            if (!Directory.Exists(full)) _store.Remove(remove.Item.Id);
            return Task.CompletedTask;
        }
        // Look again: a file changed since the listing is not deleted.
        var now = Placeholders.Read(full);
        if (now is null || !now.InSync || ItemIdentity.Decode(now.Identity)?.Id != remove.Item.Id)
        {
            Log.Info("OnDemand", $"'{_pair.Id}': '{remove.Path}' changed on the PC meanwhile; not deleted.");
            return Task.CompletedTask;
        }
        if (now.OnDiskSize > 0 && _keepTrash)
        {
            // Its data goes into the recycle bin as a normal file, so freeing space can never touch it.
            Placeholders.Revert(full);
            var target = Path.Combine(SyncTrash.FolderOf(_pair.LocalPath), _trashStamp ??= SyncTrash.NewStamp(), NameEncoding.ToLocalPath(remove.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(full, target, overwrite: true);
        }
        else
        {
            File.Delete(full);
        }
        _store.Remove(remove.Item.Id);
        _deletes++;
        _changes.Add(new FileChange(ChangeKind.DeletedOnPc, remove.Path));
        return Task.CompletedTask;
    }

    private async Task RemoveCloudAsync(RemoveCloud remove)
    {
        if (remove.Item.IsDirectory)
        {
            try
            {
                // rmdir takes only an empty folder: what the cloud got in it meanwhile stays.
                await _rc.CallAsync("operations/rmdir", new JsonObject { ["fs"] = _cloudFs, ["remote"] = remove.Path }, TimeSpan.FromMinutes(2), _cancel);
                _store.Remove(remove.Item.Id);
            }
            catch (CdException e) when (e.Code is not ("CD-5001" or "CD-5003" or "CD-3012"))
            {
                // Something new in it, or the server does not let it go: it comes back on the PC, so what is in it can come,
                // too. Before, it stayed away - and every run tried to delete it again.
                Log.Info("OnDemand", $"'{_pair.Id}': folder '{remove.Path}' stays in the cloud ({e.Detail}).");
                Restore(remove.Path);
            }
            return;
        }
        // Look again: a file changed in the cloud since the listing is not deleted (the next run brings it back).
        var now = await _files.StatAsync(_remote, CloudFetcher.CloudPath(_pair, remove.Path), _withHashes, _cancel);
        if (now is not null && (now.Value.Size != remove.Item.CloudSize || now.Value.Ticks != remove.Item.CloudTicks))
        {
            Log.Info("OnDemand", $"'{_pair.Id}': '{remove.Path}' changed in the cloud meanwhile; not deleted.");
            _cloudChanged++;
            return;
        }
        if (now is not null)
        {
            try
            {
                await _rc.CallAsync("operations/deletefile", new JsonObject { ["fs"] = _cloudFs, ["remote"] = remove.Path }, TimeSpan.FromMinutes(2), _cancel);
            }
            catch (CdException e) when (Refused(e))
            {
                // Gone all the same (e.g. "404 Not Found"): deleted.
                if (await _files.StatAsync(_remote, CloudFetcher.CloudPath(_pair, remove.Path), _withHashes, _cancel) is not null)
                {
                    // The server does not let it be deleted (e.g. a share to read only): it comes back. Before, it was kept
                    // out of the synchronisation for good - gone from the PC although the cloud still had it.
                    Log.Info("OnDemand", $"'{_pair.Id}': the server did not let '{remove.Path}' be deleted ({e.Detail}); it comes back.");
                    Restore(remove.Path);
                    return;
                }
            }
        }
        _store.Remove(remove.Item.Id);
        _deletes++;
        _changes.Add(new FileChange(ChangeKind.DeletedInCloud, remove.Path));
    }

    /// <summary>
    /// Brings back on the PC what the server did not let be changed: as the cloud has it - a placeholder without data, as
    /// the store knows it -, with the folders above it where they are missing. A place taken on the PC meanwhile stays as
    /// it is; the next run sees what is there.
    /// </summary>
    private void Restore(string path)
    {
        var missing = new List<SyncItem>();
        for (var place = path; place.Length > 0 && !Path.Exists(LocalFull(place)); place = Parent(place))
        {
            if (_store.FindByPath(place) is not { } item)
            {
                Log.Warn("OnDemand", $"'{_pair.Id}': '{path}' not brought back: '{place}' is not known.");
                return;
            }
            missing.Add(item);
        }
        if (missing.Count == 0) return;
        missing.Reverse();
        foreach (var item in missing)
        {
            if (_placeholdersStopped)
            {
                _heldBack++;
                return;
            }
            var folder = Parent(item.Path) is { Length: > 0 } parent ? LocalFull(parent) : _pair.LocalPath;
            var entry = new CloudEntry(item.Path, item.IsDirectory, item.CloudSize, item.CloudTicks, item.CloudHash);
            var created = new List<CloudEntry>();
            CreateIn(folder, [entry], created);
            if (created.Count == 0) return;
            if (_isBroken(LocalFull(item.Path)))
            {
                _broken.Add(item.Path);
                _placeholdersStopped = true;
                Log.Error("OnDemand", $"'{_pair.Id}': Windows wrote the placeholder '{item.Path}' broken; no more are made.");
                return;
            }
            _takenNames.Add(item.Path);
            // In a folder kept on this device, what comes back is kept, too.
            if (PinnedFolder(folder))
            {
                Placeholders.SetPinState(LocalFull(item.Path), PinState.Pinned, recurse: false);
                if (!item.IsDirectory) _toFetch.Add(item.Path);
            }
        }
        _changes.Add(new FileChange(ChangeKind.Restored, path));
        Log.Info("OnDemand", $"'{_pair.Id}': '{path}' is back on the PC as the server has it.");
    }

    /// <summary>
    /// A change the server does not take although it has the file (e.g. a share to read only): the PC's version goes
    /// beside it under a conflict name and stays on the PC, and the server's version comes back under the name - nothing
    /// is lost, and the file keeps coming from the server. The Nextcloud client does the same.
    /// </summary>
    private void KeepBeside(LocalEntry local, SyncItem known, string? reason)
    {
        var copy = MovePcVersionBeside(known.Path, local);
        Log.Info("OnDemand", $"'{_pair.Id}': the server did not take the change of '{known.Path}' ({reason}); it stays on the PC as '{copy}'.");
        KeepOnPc(copy);
        Restore(known.Path);
    }

    /// <summary>The PC's version goes beside the file under a conflict name, as a normal file; returns that name.</summary>
    private string MovePcVersionBeside(string path, LocalEntry local)
    {
        var full = LocalFull(local.Path);
        var name = ConflictName(path, "PC");
        if (local.Placeholder is not null) Placeholders.Revert(full);
        File.Move(full, LocalFull(name));
        return name;
    }

    /// <summary>
    /// The server does not let it be moved or renamed (e.g. a share to read only). A file whose data is on the PC stays
    /// where it was put, as a file of its own - the next run uploads it there, or keeps it on the PC when the server does
    /// not take it there either -, and the original comes back. Anything else goes back to its place: nothing of it lies
    /// only on the PC.
    /// </summary>
    private void UndoMove(MoveCloud move, string? reason)
    {
        Log.Info("OnDemand", $"'{_pair.Id}': the server did not let '{move.Item.Path}' be moved to '{move.NewPath}' ({reason}).");
        _undone.Add(move.NewPath);
        var now = LocalFull(move.NewPath);
        var before = LocalFull(move.Item.Path);
        if (!Path.Exists(now))
        {
            Restore(move.Item.Path);
            return;
        }
        var caseOnly = string.Equals(move.Item.Path, move.NewPath, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && !move.Item.IsDirectory && Placeholders.Read(now) is { IsFullyOnDisk: true })
        {
            Placeholders.Revert(now);
            Restore(move.Item.Path);
            return;
        }
        if (caseOnly || !Path.Exists(before))
        {
            if (Parent(move.Item.Path) is { Length: > 0 } parent) Restore(parent);
            if (Directory.Exists(Path.GetDirectoryName(before)))
            {
                MoveBack(now, before, move.Item.IsDirectory);
                // Windows takes a renamed placeholder as changed; back under its name and unchanged, it is in step again.
                if (!move.Item.IsDirectory) Placeholders.MarkInSyncIfUnchanged(before, new FileVersion(move.Item.LocalSize ?? -1, move.Item.LocalTicks ?? -1, -1));
                _changes.Add(new FileChange(ChangeKind.Restored, move.Item.Path));
                return;
            }
        }
        // Its place is taken on the PC: a file without its data goes (the server has it); a folder becomes one of its own -
        // what is in it keeps its identity, and the next run handles each entry the same way.
        if (move.Item.IsDirectory) Placeholders.Revert(now);
        else File.Delete(now);
        Restore(move.Item.Path);
    }

    /// <summary>Back to the old name on the PC - also when only upper and lower case differ (through a name in between).</summary>
    private static void MoveBack(string now, string before, bool isDirectory)
    {
        var from = now;
        if (string.Equals(now, before, StringComparison.OrdinalIgnoreCase))
        {
            from = now + ".clouddrive-move";
            if (isDirectory) Directory.Move(now, from);
            else File.Move(now, from);
        }
        if (isDirectory) Directory.Move(from, before);
        else File.Move(from, before);
    }

    private Task DropAsync(DropPlaceholder drop)
    {
        var full = LocalFull(drop.Path);
        var now = Path.Exists(full) ? Placeholders.Read(full) : null;
        // Only a placeholder whose data was never on the PC: there is nothing to lose.
        if (now is not null && now.OnDiskSize == 0)
        {
            if (drop.Local.IsDirectory)
            {
                if (!Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
            }
            else
            {
                File.Delete(full);
            }
        }
        if (drop.Local.ItemId is { } id && _store.Find(id) is { } item && item.Path == drop.Path) _store.Remove(id);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Catches up with "Always keep on this device" and "Free up space" chosen while CloudDrive-Sync was not running (the
    /// <see cref="PinWatcher"/> handles them at once otherwise): every placeholder is looked at again before anything
    /// happens to it. Explorer sets the state of a chosen folder alone; a file below it without a state of its own takes
    /// the state of the nearest such folder, while one the user chose for itself keeps it.
    /// </summary>
    public void ApplyPinStates(IReadOnlyCollection<LocalEntry> local)
    {
        var folders = local.Where(e => e.IsDirectory && e.Placeholder?.Pin is PinState.Pinned or PinState.Unpinned)
            .ToDictionary(e => e.Path, e => e.Placeholder!.Pin, StringComparer.OrdinalIgnoreCase);
        if (RootPinState() is { } root) folders[""] = root;
        foreach (var entry in local.Where(e => !e.IsDirectory && e.Placeholder is not null))
        {
            _cancel.ThrowIfCancellationRequested();
            var own = entry.Placeholder!.Pin is PinState.Pinned or PinState.Unpinned;
            var inherited = own ? null : FolderPinState(folders, entry.Path);
            if (!own && inherited is null) continue;
            var full = LocalFull(entry.Path);
            try
            {
                if (!File.Exists(full)) continue;
                if (inherited is { } state) Placeholders.SetPinState(full, state, recurse: false);
                if (Placeholders.Read(full) is { } now) PinWatcher.Apply(full, now, _pair.Id);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn("OnDemand", $"'{_pair.Id}': pin state of '{entry.Path}' not carried out: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Windows takes a folder out of sync whenever something in it is created, renamed or deleted - also by this run - and
    /// Explorer then shows no status for it. After a run every folder is in sync again, except those on the way to
    /// something left for the next run and the folders the server did not let be read (with everything in them).
    /// </summary>
    public void MarkFoldersInSync(IEnumerable<string> unfinished, IReadOnlyCollection<string> unreadable)
    {
        var waiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in unfinished.Concat(unreadable))
        {
            waiting.Add("");
            for (var folder = Parent(path); folder.Length > 0; folder = Parent(folder)) waiting.Add(folder);
        }
        var leftAlone = new HashSet<string>(unreadable, StringComparer.OrdinalIgnoreCase);
        var folders = new Stack<string>();
        folders.Push(_pair.LocalPath);
        while (folders.Count > 0)
        {
            _cancel.ThrowIfCancellationRequested();
            var folder = folders.Pop();
            try
            {
                var relative = folder.Length == _pair.LocalPath.Length ? "" : NameEncoding.ToStandardPath(Path.GetRelativePath(_pair.LocalPath, folder));
                if (leftAlone.Contains(relative)) continue;
                // A link is not part of the folder: what it points to is never marked (it may be another program's).
                foreach (var below in new DirectoryInfo(folder).EnumerateDirectories())
                    if ((relative.Length > 0 || !below.Name.StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase)) && !FolderWalk.IsLink(below)) folders.Push(below.FullName);
                if (!waiting.Contains(relative) && Placeholders.Read(folder) is { InSync: false }) Placeholders.MarkFolderInSync(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Only the status in Explorer is missing until the next run.
                Log.Debug("OnDemand", $"'{_pair.Id}': folder not marked in sync: {e.Message}");
            }
        }
    }

    /// <summary>
    /// "Speicherplatz automatisch freigeben": gives back the space of files not used for <paramref name="days"/> days
    /// (0 = never, the default). Only files in sync and without a pin state of their own - "Immer auf diesem Gerät
    /// beibehalten" keeps a file, and a change not uploaded yet is never lost. "Used" is the latest of: last opened (the
    /// last access time NTFS keeps), last changed, and when CloudDrive-Sync first saw the data on the PC. The last one
    /// makes a file fetched just now count as used and stands in where Windows keeps no last access time. Returns the
    /// bytes given back.
    /// </summary>
    public long FreeUpSpace(IReadOnlyCollection<LocalEntry> local, int days, DateTime nowUtc)
    {
        var known = _store.OnDiskSince();
        var arrived = new Dictionary<long, long>();
        var onDisk = new HashSet<long>();
        var freed = 0L;
        var latest = nowUtc.AddDays(-days);
        foreach (var entry in local)
        {
            if (entry.IsDirectory || entry.ItemId is not { } id || entry.Placeholder is not { OnDiskSize: > 0 }) continue;
            if (!known.TryGetValue(id, out var since))
            {
                since = nowUtc.Ticks;
                arrived[id] = since;
            }
            onDisk.Add(id);
            if (days <= 0) continue;
            _cancel.ThrowIfCancellationRequested();
            var full = LocalFull(entry.Path);
            try
            {
                if (Placeholders.Read(full) is not { } now) continue;
                var file = new FileInfo(full);
                if (!Planner.ShouldFree(now, file.LastAccessTimeUtc, file.LastWriteTimeUtc, since, latest)) continue;
                Placeholders.Dehydrate(full);
                freed += now.OnDiskSize;
                onDisk.Remove(id);
                Log.Info("OnDemand", $"'{_pair.Id}': space of '{entry.Path}' freed - not used for {days} days.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Open in a program, for example: the next run tries again.
                Log.Debug("OnDemand", $"'{_pair.Id}': space of '{entry.Path}' not freed: {e.Message}");
            }
        }
        _store.UpdateOnDisk(arrived, known.Keys.Where(id => !onDisk.Contains(id)).ToList());
        return freed;
    }

    /// <summary>What the folder takes on the PC (data of placeholders, normal files) and what the cloud folder holds.</summary>
    public static SpaceUse Measure(IReadOnlyCollection<LocalEntry> local, IEnumerable<CloudEntry> cloud, long freed) => new(
        Math.Max(0, local.Where(e => !e.IsDirectory).Sum(e => e.Placeholder?.OnDiskSize ?? e.Size) - freed),
        cloud.Where(e => !e.IsDirectory).Sum(e => e.Size));

    private PinState? RootPinState()
    {
        try
        {
            var pin = Placeholders.Read(_pair.LocalPath)?.Pin;
            return pin is PinState.Pinned or PinState.Unpinned ? pin : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static PinState? FolderPinState(Dictionary<string, PinState> folders, string path)
    {
        for (var folder = Parent(path); ; folder = Parent(folder))
        {
            if (folders.TryGetValue(folder, out var state)) return state;
            if (folder.Length == 0) return null;
        }
    }

    /// <summary>After an upload (or when a file is taken as in step): placeholder of the item, in sync if unchanged.</summary>
    private void FinishUpload(LocalEntry local, long id, FileVersion version)
    {
        var convert = local.Placeholder is null;
        Placeholders.FinishUpload(LocalFull(local.Path), ItemIdentity.Encode(id, local.Path), convert, setIdentity: !convert && local.ItemId != id, version);
    }

    /// <summary>Turns a normal file or folder into a placeholder of the item, or gives a placeholder the item's identity.</summary>
    private void MakePlaceholder(LocalEntry local, long id)
    {
        var full = LocalFull(local.Path);
        var identity = ItemIdentity.Encode(id, local.Path);
        if (local.Placeholder is null) Placeholders.Convert(full, identity, markInSync: local.IsDirectory);
        else if (local.ItemId != id) Placeholders.SetIdentity(full, identity);
    }

    private static bool PinnedFolder(string folder) => ((uint)File.GetAttributes(folder) & 0x00080000) != 0;

    private string LocalFull(string path) => Path.Combine(_pair.LocalPath, NameEncoding.ToLocalPath(path));

    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static int Depth(string path) => path.Count(c => c == '/');

    /// <summary>A time for Windows: the server's, or now when the server gave none (FILETIME starts in 1601).</summary>
    private static DateTime TimeOf(long ticks) => ticks > new DateTime(1601, 1, 2).Ticks ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.UtcNow;

    private static bool IsInUse(Exception e) =>
        (e is IOException io && io.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)) ||
        (e is CdException cd && cd.Code == "CD-4510");
}
