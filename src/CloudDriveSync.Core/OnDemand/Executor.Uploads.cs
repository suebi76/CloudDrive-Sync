using System.Text.Json.Nodes;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.OnDemand;

internal sealed partial class Executor
{
    private async Task AdoptAsync(Adopt adopt)
    {
        var full = LocalFull(adopt.Path);
        if (adopt.Cloud.IsDirectory)
        {
            var id = _store.Upsert(new SyncItem(0, adopt.Cloud.Path, true, 0, adopt.Cloud.Ticks, null, null, null));
            MakePlaceholder(adopt.Local, id);
            return;
        }
        // The same size is not enough: different content is a conflict after all - checked by the server's checksum, or,
        // where it has none (IServ, most WebDAV servers), by the content itself. Before, a file changed in the cloud that
        // kept its size (a date in a text) was taken as the PC's copy - and the PC's older version would later have gone up.
        if (adopt.Local.OnDisk && (adopt.Cloud.Hash is { } hash ? !LocalHashMatches(full, hash) : !adopt.Proven && !await SameContentAsync(full, adopt.Cloud)))
        {
            await ResolveAsync(new Conflict(adopt.Local, adopt.Cloud, null));
            return;
        }
        var version = Placeholders.ReadVersion(full);
        var itemId = _store.Upsert(new SyncItem(0, adopt.Cloud.Path, false, adopt.Cloud.Size, adopt.Cloud.Ticks, adopt.Cloud.Hash, version.Size, version.LastWriteTicks));
        if (adopt.Local.OnDisk) FinishUpload(adopt.Local, itemId, version);
        else MakePlaceholder(adopt.Local, itemId);
    }

    /// <summary>
    /// Whether the file on the PC has the content of the cloud's file - read from the cloud and compared piece by piece; it
    /// stops at the first difference.
    /// </summary>
    private async Task<bool> SameContentAsync(string full, CloudEntry cloud)
    {
        if (cloud.Size == 0) return new FileInfo(full).Length == 0;
        try
        {
            await using var remote = await _files.OpenAsync(_remote, CloudFetcher.CloudPath(_pair, cloud.Path), 0, cloud.Size, _cancel);
            await using var local = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
            var cloudPiece = new byte[1 << 16];
            var pcPiece = new byte[1 << 16];
            while (true)
            {
                var count = await remote.Content.ReadAsync(cloudPiece, _cancel);
                if (count == 0) return await local.ReadAsync(pcPiece.AsMemory(0, 1), _cancel) == 0;
                var got = await local.ReadAtLeastAsync(pcPiece.AsMemory(0, count), count, throwOnEndOfStream: false, _cancel);
                if (got != count || !cloudPiece.AsSpan(0, count).SequenceEqual(pcPiece.AsSpan(0, count))) return false;
            }
        }
        catch (HttpRequestException e)
        {
            throw new CdException("CD-5001", $"{cloud.Path}: {e.Message}", e);
        }
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
            // A file the cloud has is replaced so that a failed upload never costs its version there.
            await (known is not null && before is not null ? ReplaceAsync(local) : CopyUpAsync(local));
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
        // One group of statistics per synchronisation, started afresh for each file (the engine keeps every group). Deleted,
        // not reset: resetting the group before the first file wrote an error into rclone's log.
        var group = $"upload/{_pair.Id}";
        try
        {
            await _rc.CallAsync("core/stats-delete", new JsonObject { ["group"] = group }, cancellationToken: _cancel);
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
}
