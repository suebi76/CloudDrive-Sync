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
internal sealed record ExecutionResult(
    IReadOnlyList<FileChange> Changes,
    long Transfers,
    long Bytes,
    int Deletes,
    IReadOnlyList<string> LocalOnlyAdded,
    IReadOnlyList<string> Locked,
    IReadOnlyList<string> Failed);

/// <summary>
/// Carries out a <see cref="SyncPlan"/>, step by step, each step recorded in the <see cref="ItemStore"/> as soon as it
/// is done - a run that breaks off continues where it stopped. Before a step changes anything it looks again: an upload
/// or a deletion in the cloud happens only while the cloud still has the known version, a deletion on the PC only while
/// the file is unchanged there. A step whose side changed meanwhile is left for the next run, which then sees the
/// change. Order: moves, folders, uploads, conflicts, new placeholders, refreshed ones, deletions (files before their
/// folders, which go only when empty).
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
    private string? _trashStamp;
    private long _transfers;
    private long _bytes;
    private int _deletes;

    public Executor(RcClient rc, FileServer files, ItemStore store, SyncPairSettings pair, AccountSettings account, bool keepTrash,
        IEnumerable<string> cloudPaths, Action<JobProgress>? progress, CancellationToken cancel)
    {
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
        foreach (var move in actions.OfType<MoveCloud>()) await StepAsync(move.NewPath, () => MoveCloudAsync(move));
        foreach (var folder in actions.OfType<CreateCloudFolder>().OrderBy(a => Depth(a.Path))) await StepAsync(folder.Path, () => CreateCloudFolderAsync(folder));
        foreach (var adopt in actions.OfType<Adopt>().OrderBy(a => a.Cloud.IsDirectory ? 0 : 1).ThenBy(a => Depth(a.Path))) await StepAsync(adopt.Path, () => AdoptAsync(adopt));
        foreach (var upload in actions.OfType<Upload>()) await StepAsync(upload.Path, () => UploadAsync(upload.Local, upload.Item));
        foreach (var mark in actions.OfType<MarkInSync>()) await StepAsync(mark.Path, () => MarkAsync(mark));
        foreach (var conflict in actions.OfType<Conflict>()) await StepAsync(conflict.Path, () => ResolveAsync(conflict));
        await CreatePlaceholdersAsync(actions.OfType<CreatePlaceholder>().Select(a => a.Cloud).ToList());
        foreach (var refresh in actions.OfType<RefreshPlaceholder>()) await StepAsync(refresh.Path, () => RefreshAsync(refresh));
        foreach (var remove in actions.OfType<RemoveLocal>().OrderBy(a => a.Item.IsDirectory ? 1 : 0).ThenByDescending(a => Depth(a.Path))) await StepAsync(remove.Path, () => RemoveLocalAsync(remove));
        foreach (var remove in actions.OfType<RemoveCloud>().OrderBy(a => a.Item.IsDirectory ? 1 : 0).ThenByDescending(a => Depth(a.Path))) await StepAsync(remove.Path, () => RemoveCloudAsync(remove));
        foreach (var drop in actions.OfType<DropPlaceholder>().OrderByDescending(a => Depth(a.Path))) await StepAsync(drop.Path, () => DropAsync(drop));
        foreach (var forget in actions.OfType<Forget>().OrderBy(a => a.Item.IsDirectory ? 1 : 0).ThenByDescending(a => Depth(a.Item.Path)))
            await StepAsync(forget.Item.Path, () => ForgetAsync(forget));
        // Files that were on the PC before their new version arrived come again - after everything else.
        foreach (var path in _toFetch) await StepAsync(path, () => FetchAsync(path));
        return new ExecutionResult(_changes, _transfers, _bytes, _deletes, _localOnly, _locked, _failed);
    }

    /// <summary>
    /// One step. What only concerns this file is noted and the run goes on; trouble with the connection or the sign-in
    /// ends the run (the next one picks up from the recorded state).
    /// </summary>
    private async Task StepAsync(string path, Func<Task> step)
    {
        _cancel.ThrowIfCancellationRequested();
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
            _localOnly.Add(path);
            _changes.Add(new FileChange(ChangeKind.KeptOnPc, path));
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
            _progress?.Invoke(new JobProgress(_bytes, 0, _transfers, 0, 0, 0, 0, _failed.Count));
        }
    }

    private async Task MoveCloudAsync(MoveCloud move)
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
        _store.Move(move.Item.Path, move.NewPath);
        _takenNames.Add(move.NewPath);
        Log.Info("OnDemand", $"'{_pair.Id}': moved '{move.Item.Path}' to '{move.NewPath}' in the cloud.");
    }

    private async Task CreateCloudFolderAsync(CreateCloudFolder folder)
    {
        await _rc.CallAsync("operations/mkdir", new JsonObject { ["fs"] = _cloudFs, ["remote"] = folder.Path }, TimeSpan.FromMinutes(2), _cancel);
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
        var full = LocalFull(local.Path);
        var version = Placeholders.ReadVersion(full);
        // The cloud may have changed since the listing: then this is a conflict for the next run, nothing is overwritten.
        var before = await _files.StatAsync(_remote, CloudFetcher.CloudPath(_pair, local.Path), _withHashes, _cancel);
        if (before is not null && (known is null || before.Value.Size != known.CloudSize || before.Value.Ticks != known.CloudTicks))
        {
            Log.Info("OnDemand", $"'{_pair.Id}': '{local.Path}' changed in the cloud meanwhile; the next run handles it.");
            return;
        }
        await _rc.CallAsync("operations/copyfile", new JsonObject
        {
            ["srcFs"] = _pair.LocalPath,
            ["srcRemote"] = local.Path,
            ["dstFs"] = _cloudFs,
            ["dstRemote"] = local.Path,
            // The run knows the file changed. On servers without times of their own rclone compares sizes only and would
            // skip a change that keeps the size.
            ["_config"] = new JsonObject { ["IgnoreTimes"] = true },
        }, TimeSpan.FromHours(6), _cancel);
        var after = await _files.StatAsync(_remote, CloudFetcher.CloudPath(_pair, local.Path), _withHashes, _cancel)
            ?? throw new CdException("CD-4605", $"{local.Path}: not in the cloud after the upload");
        var id = _store.Upsert(new SyncItem(known?.Id ?? 0, local.Path, false, after.Size, after.Ticks, after.Hash, version.Size, version.LastWriteTicks));
        FinishUpload(local, id, version);
        _takenNames.Add(local.Path);
        _transfers++;
        _bytes += version.Size;
        _changes.Add(new FileChange(ChangeKind.Uploaded, local.Path));
    }

    private Task MarkAsync(MarkInSync mark)
    {
        Placeholders.MarkInSyncIfUnchanged(LocalFull(mark.Path), new FileVersion(mark.Item.LocalSize ?? -1, mark.Item.LocalTicks ?? -1, -1));
        return Task.CompletedTask;
    }

    /// <summary>New placeholders, folder by folder (outer folders first), recorded in one transaction per folder.</summary>
    private Task CreatePlaceholdersAsync(IReadOnlyList<CloudEntry> entries)
    {
        foreach (var group in entries.GroupBy(e => Parent(e.Path)).OrderBy(g => g.Key.Length == 0 ? -1 : Depth(g.Key)))
        {
            _cancel.ThrowIfCancellationRequested();
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
                Log.Info("OnDemand", $"'{_pair.Id}': folder '{remove.Path}' stays in the cloud ({e.Detail}).");
            }
            return;
        }
        // Look again: a file changed in the cloud since the listing is not deleted (the next run brings it back).
        var now = await _files.StatAsync(_remote, CloudFetcher.CloudPath(_pair, remove.Path), _withHashes, _cancel);
        if (now is not null && (now.Value.Size != remove.Item.CloudSize || now.Value.Ticks != remove.Item.CloudTicks))
        {
            Log.Info("OnDemand", $"'{_pair.Id}': '{remove.Path}' changed in the cloud meanwhile; not deleted.");
            return;
        }
        if (now is not null)
            await _rc.CallAsync("operations/deletefile", new JsonObject { ["fs"] = _cloudFs, ["remote"] = remove.Path }, TimeSpan.FromMinutes(2), _cancel);
        _store.Remove(remove.Item.Id);
        _deletes++;
        _changes.Add(new FileChange(ChangeKind.DeletedInCloud, remove.Path));
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
                foreach (var below in Directory.EnumerateDirectories(folder))
                    if (relative.Length > 0 || !Path.GetFileName(below).StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase)) folders.Push(below);
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
