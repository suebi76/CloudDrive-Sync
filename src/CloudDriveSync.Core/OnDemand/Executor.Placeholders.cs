using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.OnDemand;

internal sealed partial class Executor
{
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

    private static bool PinnedFolder(string folder) => ((uint)File.GetAttributes(folder) & 0x00080000) != 0;
}
