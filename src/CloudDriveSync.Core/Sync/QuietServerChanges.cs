using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

/// <summary>A file in the cloud folder: size and the time the server reports (Unix seconds).</summary>
internal sealed record CloudFile(long Size, long Time);

/// <summary>
/// Catches changes on servers without modification times of their own (IServ and other WebDAV servers). rclone cannot
/// rely on their times and compares such files by size only, so a change on the server that keeps the size would never
/// arrive. CloudDrive-Sync therefore remembers size and time of every file on both sides after each run. Before the next
/// run, a file whose server time changed while its size stayed the same is fetched (the local version goes to the
/// recycle bin) - or, when it was changed on the PC as well, kept beside it as a conflict copy.
/// </summary>
internal sealed class QuietServerChanges
{
    private readonly RcClient _rc;
    private readonly SyncPairSettings _pair;
    private readonly string _cloud;
    private readonly string _snapshotFile;
    private readonly string _filtersFile;

    public QuietServerChanges(RcClient rc, SyncPairSettings pair, string pairFolder, string filtersFile)
    {
        _rc = rc;
        _pair = pair;
        _cloud = BisyncCommand.CloudPath(pair);
        _snapshotFile = Path.Combine(pairFolder, "server-times.json");
        _filtersFile = filtersFile;
    }

    /// <summary>What happened before a run, with the cloud listing it was based on.</summary>
    public sealed record Result(IReadOnlyDictionary<string, CloudFile> Listing, int Fetched, int ConflictCopies, int Uploaded)
    {
        public bool ChangedAnything => Fetched + ConflictCopies + Uploaded > 0;
    }

    /// <summary>
    /// Before a run: carries same-size changes over that rclone cannot tell apart - from the server to the PC and from
    /// the PC to the server. Replaced local versions go to <paramref name="trashFolder"/> (null: recycle bin off).
    /// </summary>
    public async Task<Result?> FetchAsync(string? trashFolder, CancellationToken cancellationToken)
    {
        var snapshot = Load();
        if (snapshot is null) return null;
        var listing = await ListCloudAsync(cancellationToken);
        int fetched = 0, copies = 0, uploaded = 0;
        foreach (var (path, now) in listing)
        {
            if (!snapshot.TryGetValue(path, out var before)) continue;
            var local = LocalPath(path);
            var info = new FileInfo(local);
            if (!info.Exists) continue;
            var serverChanged = now.Size == before[0] && Math.Abs(now.Time - before[1]) >= 1;
            var localChanged = info.Length != before[2] || info.LastWriteTimeUtc.Ticks != before[3];
            var serverTime = now.Time;
            if (serverChanged && !localChanged)
            {
                // Unchanged on the PC: the server's version replaces it; the local one waits in the recycle bin.
                // rclone would skip the copy (same size, no usable time), so it goes to a temporary file first (".tmp"
                // and CloudDrive-Sync's own name keep it out of the synchronisation).
                var temporary = $".clouddrive-holen-{Guid.NewGuid():N}.tmp";
                await CopyToPcAsync(path, temporary, cancellationToken);
                if (trashFolder is not null)
                {
                    var backup = Path.Combine(trashFolder, path.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(local, backup, overwrite: true);
                }
                File.Move(LocalPath(temporary), local, overwrite: true);
                fetched++;
            }
            else if (serverChanged)
            {
                // Changed on both sides: the server's version is kept beside the local one - unless both are the same.
                var copy = ConflictName(path, listing);
                await CopyToPcAsync(path, copy, cancellationToken);
                if (SameContent(local, LocalPath(copy))) File.Delete(LocalPath(copy));
                else copies++;
                // The local version has the same size as the server's: rclone would take them for equal and skip it.
                if (info.Length == now.Size)
                {
                    serverTime = await UploadAsync(path, cancellationToken) ?? serverTime;
                    uploaded++;
                }
            }
            else if (localChanged && info.Length == before[2] && info.Length == now.Size && now.Size == before[0])
            {
                // Changed on the PC without changing its size: without times of its own on the server, rclone would take
                // both versions for equal and never upload it.
                serverTime = await UploadAsync(path, cancellationToken) ?? serverTime;
                uploaded++;
            }
            else
            {
                continue;
            }
            // Remembered at once, so a run that breaks off afterwards does not see the same change again.
            var current = new FileInfo(local);
            snapshot[path] = [now.Size, serverTime, current.Length, current.LastWriteTimeUtc.Ticks];
        }
        if (fetched + copies + uploaded > 0)
        {
            Save(snapshot);
            Log.Info("Sync", $"'{_pair.Id}': same-size changes - {fetched} fetched, {uploaded} uploaded, {copies} conflict copy(ies) kept.");
        }
        return new Result(listing, fetched, copies, uploaded);
    }

    /// <summary>Uploads a file even when rclone takes it for unchanged; returns the server's new time (Unix seconds).</summary>
    private async Task<long?> UploadAsync(string path, CancellationToken cancellationToken)
    {
        await _rc.CallAsync("operations/copyfile", new JsonObject
        {
            ["srcFs"] = _pair.LocalPath,
            ["srcRemote"] = path,
            ["dstFs"] = _cloud,
            ["dstRemote"] = path,
            ["_config"] = new JsonObject { ["IgnoreTimes"] = true },
        }, TimeSpan.FromMinutes(30), cancellationToken);
        var stat = await _rc.CallAsync("operations/stat", new JsonObject
        {
            ["fs"] = _cloud,
            ["remote"] = path,
            ["opt"] = new JsonObject { ["noMimeType"] = true },
        }, TimeSpan.FromSeconds(30), cancellationToken);
        return DateTimeOffset.TryParse(stat["item"]?["ModTime"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToUnixTimeSeconds()
            : null;
    }
    /// <summary>After a successful run: remembers both sides. <paramref name="listing"/> is reused when nothing changed in the cloud.</summary>
    public async Task RememberAsync(IReadOnlyDictionary<string, CloudFile>? listing, CancellationToken cancellationToken)
    {
        listing ??= await ListCloudAsync(cancellationToken);
        var snapshot = new Dictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var (path, cloud) in listing)
        {
            var info = new FileInfo(LocalPath(path));
            if (info.Exists) snapshot[path] = [cloud.Size, cloud.Time, info.Length, info.LastWriteTimeUtc.Ticks];
        }
        Save(snapshot);
    }

    /// <summary>All files of the synchronised cloud folder, with the same filter the synchronisation uses.</summary>
    internal async Task<IReadOnlyDictionary<string, CloudFile>> ListCloudAsync(CancellationToken cancellationToken)
    {
        var result = await _rc.CallAsync("operations/list", new JsonObject
        {
            ["fs"] = _cloud,
            ["remote"] = "",
            ["opt"] = new JsonObject { ["recurse"] = true, ["filesOnly"] = true, ["noMimeType"] = true },
            ["_filter"] = new JsonObject { ["FilterFrom"] = new JsonArray(_filtersFile) },
        }, TimeSpan.FromMinutes(10), cancellationToken);
        var files = new Dictionary<string, CloudFile>(StringComparer.Ordinal);
        foreach (var item in result["list"] as JsonArray ?? [])
        {
            if (item is not JsonObject entry || entry["IsDir"]?.GetValue<bool>() == true) continue;
            var path = entry["Path"]?.GetValue<string>() ?? "";
            if (path.Length == 0 || path.StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase)) continue;
            var size = entry["Size"] is JsonValue s && s.TryGetValue<long>(out var bytes) ? bytes : -1;
            var time = DateTimeOffset.TryParse(entry["ModTime"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var modified)
                ? modified.ToUnixTimeSeconds() : 0;
            files[path] = new CloudFile(size, time);
        }
        // Two names that differ only in upper and lower case cannot both live in one Windows folder: rclone keeps one of
        // them on the PC and leaves the other on the server only. This check keeps out of such names, so the file on the
        // PC is never taken for the other one (and its content never carried over to it).
        foreach (var clash in files.Keys.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g).ToList())
            files.Remove(clash);
        return files;
    }

    private Dictionary<string, long[]>? Load()
    {
        try
        {
            return File.Exists(_snapshotFile) ? JsonSerializer.Deserialize<Dictionary<string, long[]>>(File.ReadAllText(_snapshotFile)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Save(Dictionary<string, long[]> snapshot)
    {
        var temporary = _snapshotFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
        File.Move(temporary, _snapshotFile, overwrite: true);
    }

    private string LocalPath(string path) => Path.Combine(_pair.LocalPath, path.Replace('/', '\\'));

    private async Task CopyToPcAsync(string cloudPath, string localPath, CancellationToken cancellationToken)
    {
        await _rc.CallAsync("operations/copyfile", new JsonObject
        {
            ["srcFs"] = _cloud,
            ["srcRemote"] = cloudPath,
            ["dstFs"] = _pair.LocalPath,
            ["dstRemote"] = localPath,
        }, TimeSpan.FromMinutes(30), cancellationToken);
    }

    /// <summary>"Plan.Konflikt-Cloud1.txt" - the first number that is free on both sides.</summary>
    private string ConflictName(string path, IReadOnlyDictionary<string, CloudFile> listing)
    {
        var folder = path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
        var name = path[folder.Length..];
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        for (var number = 1; ; number++)
        {
            var candidate = $"{folder}{stem}.Konflikt-Cloud{number}{extension}";
            if (!listing.ContainsKey(candidate) && !File.Exists(LocalPath(candidate))) return candidate;
        }
    }

    private static bool SameContent(string a, string b)
    {
        var first = new FileInfo(a);
        var second = new FileInfo(b);
        if (!first.Exists || !second.Exists || first.Length != second.Length) return false;
        using var x = first.OpenRead();
        using var y = second.OpenRead();
        Span<byte> bufferX = stackalloc byte[8192];
        Span<byte> bufferY = stackalloc byte[8192];
        for (var remaining = first.Length; remaining > 0;)
        {
            var count = (int)Math.Min(remaining, bufferX.Length);
            x.ReadExactly(bufferX[..count]);
            y.ReadExactly(bufferY[..count]);
            if (!bufferX[..count].SequenceEqual(bufferY[..count])) return false;
            remaining -= count;
        }
        return true;
    }
}
