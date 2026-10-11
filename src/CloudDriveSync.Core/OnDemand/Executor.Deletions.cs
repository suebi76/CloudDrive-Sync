using System.Text.Json.Nodes;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.OnDemand;

internal sealed partial class Executor
{
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
            var target = NameEncoding.ToLocalFullPath(Path.Combine(SyncTrash.FolderOf(_pair.LocalPath), _trashStamp ??= SyncTrash.NewStamp()), remove.Path);
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
}
