using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.OnDemand;

internal sealed partial class Executor
{
    /// <summary>The note of a copy on the server not cleared yet, in the store: "file\ncopy" (see <see cref="ReplaceAsync"/>).</summary>
    private const string BackupKey = "upload-backup";

    /// <summary>
    /// Replaces a file in the cloud with the PC's version without ever costing the cloud's. rclone removes what it takes for
    /// the remains of a failed upload - for a file that was there, that is the file itself (seen with a full storage, "507",
    /// and with server errors): the cloud then had neither version. So the cloud's version is copied on the server first,
    /// beside it, under a name no synchronisation takes part in ("CloudDrive-Sync-Sicherung-….tmp", on no PC). After a
    /// failed upload it takes the file's place again if the file is gone; it goes in any case - also after a stop in between
    /// (see <see cref="RecoverAsync"/>). A copy the server does not take because its storage is full means the upload would
    /// not fit either: it is not tried. Where nothing may be made beside the file (some shares), it goes up without the copy.
    /// </summary>
    private async Task ReplaceAsync(LocalEntry local)
    {
        var backup = BackupName(local.Path);
        _store.SetMeta(BackupKey, $"{local.Path}\n{backup}");
        try
        {
            await _rc.CallAsync("operations/copyfile", new JsonObject
            {
                ["srcFs"] = _cloudFs,
                ["srcRemote"] = local.Path,
                ["dstFs"] = _cloudFs,
                ["dstRemote"] = backup,
            }, TimeSpan.FromMinutes(30), _cancel);
        }
        catch (CdException e) when (Refused(e))
        {
            Log.Info("OnDemand", $"'{_pair.Id}': no copy beside '{local.Path}' allowed ({e.Detail}); it goes up without one.");
            _store.SetMeta(BackupKey, "");
            await CopyUpAsync(local);
            return;
        }
        try
        {
            await CopyUpAsync(local);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await SettleAsync(_rc, _files, _store, _pair, local.Path, backup, _cancel);
            throw;
        }
        // Uploaded: the file is there, the copy just goes (one request instead of looking first).
        try
        {
            await _rc.CallAsync("operations/deletefile", new JsonObject { ["fs"] = _cloudFs, ["remote"] = backup }, TimeSpan.FromMinutes(2), _cancel);
            _store.SetMeta(BackupKey, "");
        }
        catch (CdException e)
        {
            Log.Warn("OnDemand", $"'{_pair.Id}': the copy beside '{local.Path}' goes with the next run: {e.Code} {e.Detail}");
        }
    }

    /// <summary>
    /// A copy an earlier run left (CloudDrive-Sync stopped right then): back in the file's place if the file is gone, away
    /// otherwise. Before anything else in a run, so the listing sees the file as it should be.
    /// </summary>
    internal static async Task RecoverAsync(RcClient rc, FileServer files, ItemStore store, SyncPairSettings pair, CancellationToken cancellationToken)
    {
        if (store.GetMeta(BackupKey) is not { Length: > 0 } note) return;
        var parts = note.Split('\n');
        if (parts.Length == 2) await SettleAsync(rc, files, store, pair, parts[0], parts[1], cancellationToken);
        else store.SetMeta(BackupKey, "");
    }

    /// <summary>
    /// The copy beside a file: in the file's place when the file is gone (a failed upload took it), removed otherwise; then
    /// the note goes. When the server cannot be reached the note stays, and the next run settles it.
    /// </summary>
    private static async Task SettleAsync(RcClient rc, FileServer files, ItemStore store, SyncPairSettings pair, string path, string backup,
        CancellationToken cancellationToken)
    {
        var remote = AccountService.RemoteName(pair.AccountId) + ":";
        var cloudFs = BisyncCommand.CloudPath(pair);
        try
        {
            if (await files.StatAsync(remote, CloudFetcher.CloudPath(pair, backup), withHash: false, cancellationToken) is not null)
            {
                if (await files.StatAsync(remote, CloudFetcher.CloudPath(pair, path), withHash: false, cancellationToken) is null)
                {
                    await rc.CallAsync("operations/movefile", new JsonObject
                    {
                        ["srcFs"] = cloudFs,
                        ["srcRemote"] = backup,
                        ["dstFs"] = cloudFs,
                        ["dstRemote"] = path,
                    }, TimeSpan.FromMinutes(30), cancellationToken);
                    Log.Warn("OnDemand", $"'{pair.Id}': the upload of '{path}' failed and the file was gone from the cloud; its version there is back.");
                }
                else
                {
                    await rc.CallAsync("operations/deletefile", new JsonObject { ["fs"] = cloudFs, ["remote"] = backup }, TimeSpan.FromMinutes(2), cancellationToken);
                }
            }
            store.SetMeta(BackupKey, "");
        }
        catch (CdException e)
        {
            Log.Warn("OnDemand", $"'{pair.Id}': the copy beside '{path}' is settled with the next run: {e.Code} {e.Detail}");
        }
    }

    /// <summary>A name beside the file that no synchronisation takes part in ("*.tmp" is never synchronised).</summary>
    private static string BackupName(string path)
    {
        var name = $"CloudDrive-Sync-Sicherung-{RandomNumberGenerator.GetHexString(8, lowercase: true)}.tmp";
        return path.Contains('/') ? $"{path[..path.LastIndexOf('/')]}/{name}" : name;
    }
}
