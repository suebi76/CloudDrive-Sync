using System.Collections.Concurrent;
using System.Runtime.Versioning;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.OnDemand;

/// <summary>
/// Delivers the data of online-only files when a program opens them (or CloudDrive-Sync fetches them on purpose).
/// Only the version a placeholder stands for is ever delivered: the server's size must match before the first byte,
/// and its size, time and checksum must still match before the last piece goes out - so a file changed in the cloud
/// meanwhile never ends up as a mix of two versions. Then the request fails, and a run brings the new version
/// (<see cref="Planner"/>: the placeholder is refreshed and the old data dropped).
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed class CloudFetcher : IFileFetcher
{
    /// <summary>Size of the pieces handed to Windows: a multiple of 4 KB, small enough to keep its 60-second clock going.</summary>
    private const int PieceSize = 1024 * 1024;

    private readonly FileServer _files;
    private readonly ItemStore _store;
    private readonly Func<SyncPairSettings?> _pair;
    private readonly bool _withHashes;
    private readonly Action _changedInCloud;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();

    /// <param name="changedInCloud">Called when the cloud has another version than a placeholder: a run should follow soon.</param>
    public CloudFetcher(FileServer files, ItemStore store, Func<SyncPairSettings?> pair, bool withHashes, Action changedInCloud)
    {
        _files = files;
        _store = store;
        _pair = pair;
        _withHashes = withHashes;
        _changedInCloud = changedInCloud;
    }

    public void Fetch(FetchRequest request) => _ = Task.Run(() => FetchAsync(request));

    public void Cancel(long transferKey)
    {
        if (_running.TryGetValue(transferKey, out var cancel)) cancel.Cancel();
    }

    private async Task FetchAsync(FetchRequest request)
    {
        using var cancel = new CancellationTokenSource();
        _running[request.TransferKey] = cancel;
        var offset = request.Offset;
        var name = Path.GetFileName(request.Path);
        try
        {
            var pair = _pair() ?? throw new VersionMismatch("the synchronisation is gone");
            var item = ItemIdentity.Decode(request.Identity) is { } identity ? _store.Find(identity.Id) : null;
            if (item is null || item.IsDirectory) throw new VersionMismatch("the placeholder belongs to nothing CloudDrive-Sync knows");
            if (item.CloudSize != request.FileSize) throw new VersionMismatch($"placeholder {request.FileSize} bytes, known version {item.CloudSize}");
            var remote = AccountService.RemoteName(pair.AccountId) + ":";
            var path = CloudPath(pair, item.Path);

            await using var read = await _files.OpenAsync(remote, path, request.Offset, request.End - request.Offset, cancel.Token);
            if (read.TotalSize is { } total && total != item.CloudSize) throw new VersionMismatch($"the cloud has {total} bytes now, the placeholder {item.CloudSize}");
            var piece = new byte[PieceSize];
            while (offset < request.End)
            {
                var wanted = (int)Math.Min(PieceSize, request.End - offset);
                var got = 0;
                while (got < wanted)
                {
                    var count = await read.Content.ReadAsync(piece.AsMemory(got, wanted - got), cancel.Token);
                    if (count == 0) throw new IOException($"the cloud delivered only {offset + got - request.Offset} bytes");
                    got += count;
                }
                if (offset + got >= request.End)
                {
                    // The last piece goes out only when the cloud still has the very version that was read.
                    var now = await _files.StatAsync(remote, path, _withHashes, cancel.Token);
                    if (now is null || now.Value.Size != item.CloudSize || now.Value.Ticks != item.CloudTicks || (item.CloudHash is not null && now.Value.Hash is not null && now.Value.Hash != item.CloudHash))
                        throw new VersionMismatch("the file changed in the cloud while it was being fetched");
                }
                request.Transfer(piece.AsSpan(0, got), offset);
                offset += got;
            }
            Log.Info("OnDemand", $"Fetched '{name}' ({offset - request.Offset} bytes{(request.Process is null ? "" : $" for {request.Process}")}).");
        }
        catch (Exception e)
        {
            var failure = e switch
            {
                OperationCanceledException => FetchFailure.Cancelled,
                CdException { Code: "CD-5001" or "CD-5003" } => FetchFailure.NetworkUnavailable,
                CdException { Code: "CD-3012" } => FetchFailure.AuthenticationFailed,
                HttpRequestException => FetchFailure.NetworkUnavailable,
                IOException io when io.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) => FetchFailure.DiskFull,
                _ => FetchFailure.Unsuccessful,
            };
            if (failure != FetchFailure.Cancelled) Log.Warn("OnDemand", $"'{name}' could not be fetched ({failure}): {(e as CdException)?.Detail ?? e.Message}");
            if (e is VersionMismatch) _changedInCloud();
            try
            {
                request.Fail(failure, offset);
            }
            catch (IOException answer)
            {
                Log.Warn("OnDemand", $"The failure for '{name}' could not be reported to Windows: {answer.Message}");
            }
        }
        finally
        {
            _running.TryRemove(request.TransferKey, out _);
        }
    }

    /// <summary>Path of an item below the account: the synchronisation's cloud folder plus the item's path.</summary>
    internal static string CloudPath(SyncPairSettings pair, string itemPath) =>
        string.Join('/', new[] { pair.RemotePath.Trim('/'), itemPath }.Where(p => p.Length > 0));

    /// <summary>The cloud has another version than the placeholder stands for.</summary>
    private sealed class VersionMismatch(string message) : Exception(message);
}
