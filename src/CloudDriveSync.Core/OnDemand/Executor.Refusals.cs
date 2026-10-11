using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.OnDemand;

internal sealed partial class Executor
{
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
                MoveBack(now, before, move.Item.IsDirectory, SyncTrash.FolderOf(_pair.LocalPath));
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

    /// <summary>
    /// Back to the old name on the PC - also when only upper and lower case differ: then through a place in the folder's
    /// recycle bin, which never takes part in the synchronisation (should CloudDrive-Sync stop right then, nothing of its
    /// own goes up; the next run brings the entry back from the server).
    /// </summary>
    private static void MoveBack(string now, string before, bool isDirectory, string trash)
    {
        var from = now;
        if (string.Equals(now, before, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(trash);
            from = Path.Combine(trash, $".move-{Guid.NewGuid():N}");
            if (isDirectory) Directory.Move(now, from);
            else File.Move(now, from);
        }
        if (isDirectory) Directory.Move(from, before);
        else File.Move(from, before);
    }
}
