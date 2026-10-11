using System.Text.Json.Nodes;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.OnDemand;

internal sealed partial class Executor
{
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

    /// <summary>
    /// Renamed or moved in the cloud (e.g. on another PC): the placeholder moves along, with its data. Looks again first:
    /// only the unchanged placeholder of this item moves, and only to a free place in a folder that is there - otherwise
    /// it stays, and the next run sees what is there.
    /// </summary>
    private Task MoveLocalAsync(MoveLocal move)
    {
        var from = LocalFull(move.Item.Path);
        var to = LocalFull(move.Cloud.Path);
        var now = Path.Exists(from) ? Placeholders.Read(from) : null;
        if (now is null || !now.InSync || ItemIdentity.Decode(now.Identity)?.Id != move.Item.Id || Path.Exists(to) || !Directory.Exists(Path.GetDirectoryName(to)))
        {
            Log.Info("OnDemand", $"'{_pair.Id}': '{move.Item.Path}' not moved to '{move.Cloud.Path}' on the PC (changed or taken meanwhile); the next run sees it.");
            return Task.CompletedTask;
        }
        File.Move(from, to);
        _store.Update(move.Item with { Path = move.Cloud.Path, CloudSize = move.Cloud.Size, CloudTicks = move.Cloud.Ticks, CloudHash = move.Cloud.Hash ?? move.Item.CloudHash });
        // Windows takes a renamed placeholder as changed; unchanged, it is in step again.
        Placeholders.MarkInSyncIfUnchanged(to, new FileVersion(move.Item.LocalSize ?? -1, move.Item.LocalTicks ?? -1, -1));
        _takenNames.Add(move.Cloud.Path);
        _changes.Add(new FileChange(ChangeKind.Moved, move.Cloud.Path));
        return Task.CompletedTask;
    }
}
