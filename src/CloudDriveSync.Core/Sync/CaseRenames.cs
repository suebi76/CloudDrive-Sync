using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

/// <summary>
/// Renames that only change upper and lower case ("bericht.docx" to "Bericht.docx"). Windows takes both spellings for
/// the same file, IServ and Nextcloud for two - rclone's bisync then stops with "out of sync". CloudDrive-Sync brings
/// both sides to the same spelling itself, so bisync only sees a file that is new on both sides and equal: renamed on
/// the PC, the server follows before the run; renamed on the server, the PC follows. Nothing is copied or deleted.
/// </summary>
internal static partial class CaseRenames
{
    /// <summary>
    /// Before a run: files and folders renamed on the PC only in case since the last run are renamed on the server, too -
    /// unless the server already has something under the new spelling.
    /// </summary>
    public static async Task<int> CarryPcRenamesAsync(RcClient rc, SyncPairSettings pair, string pairFolder, CancellationToken cancellationToken)
    {
        var known = DeleteGuard.Load(pairFolder);
        if (known is null || known.Count == 0) return 0;
        // The keys keep the spelling of the last run; looked up regardless of case.
        var spelling = known.Keys.ToDictionary(path => path, path => path, StringComparer.OrdinalIgnoreCase);
        var renames = new List<(string From, string To, bool Folder, int Level)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var current in DeleteGuard.LocalFiles(pair).Keys)
        {
            if (!spelling.TryGetValue(current, out var before) || before == current) continue;
            var was = before.Split('/');
            var now = current.Split('/');
            for (var level = 0; level < now.Length; level++)
            {
                if (was[level] == now[level]) continue;
                // Folders above are renamed first, so the old name sits under their new spelling.
                var parent = string.Join('/', now[..level]);
                var from = Join(parent, was[level]);
                var to = Join(parent, now[level]);
                if (seen.Add(from + "\n" + to)) renames.Add((from, to, level < now.Length - 1, level));
            }
        }
        if (renames.Count == 0) return 0;

        var cloud = BisyncCommand.CloudPath(pair);
        var done = 0;
        foreach (var (from, to, folder, _) in renames.OrderBy(r => r.Level))
        {
            try
            {
                // Only a real rename: the old name is there, the new one is not (a server that ignores case finds both).
                var source = await StatAsync(rc, cloud, from, cancellationToken);
                if (source is null || source["IsDir"]?.GetValue<bool>() != folder || await StatAsync(rc, cloud, to, cancellationToken) is not null) continue;
                if (folder)
                {
                    await rc.CallAsync("sync/move", new JsonObject
                    {
                        ["srcFs"] = JoinFs(cloud, from),
                        ["dstFs"] = JoinFs(cloud, to),
                        ["createEmptySrcDirs"] = true,
                        ["deleteEmptySrcDirs"] = true,
                    }, TimeSpan.FromMinutes(30), cancellationToken);
                }
                else
                {
                    await rc.CallAsync("operations/movefile", new JsonObject
                    {
                        ["srcFs"] = cloud,
                        ["srcRemote"] = from,
                        ["dstFs"] = cloud,
                        ["dstRemote"] = to,
                    }, TimeSpan.FromMinutes(5), cancellationToken);
                }
                done++;
            }
            catch (CdException e)
            {
                Log.Warn("Sync", $"'{pair.Id}': renaming on the server not possible ({e.Code}); the synchronisation takes over.");
            }
        }
        if (done > 0) Log.Info("Sync", $"'{pair.Id}': {done} rename(s) of upper and lower case carried over to the server.");
        return done;
    }

    /// <summary>The paths bisync's final check found on one side only ("Path1 file not found in Path2 - Bericht.txt").</summary>
    public static IReadOnlyList<string> OutOfSync(string report) =>
        OutOfSyncPattern().Matches(report).Select(m => m.Groups[1].Value.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// After bisync stopped because names differ only in case: the PC takes the server's spelling. True when every path
    /// in question exists on both sides exactly once and now has the same spelling - then the run can be repeated.
    /// </summary>
    public static async Task<bool> PcFollowsServerAsync(RcClient rc, SyncPairSettings pair, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (paths.Count == 0) return false;
        var cloud = BisyncCommand.CloudPath(pair);
        var listings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var local = pair.LocalPath;
            var server = "";
            foreach (var name in path.Split('/'))
            {
                if (!listings.TryGetValue(server, out var entries))
                    listings[server] = entries = await ListAsync(rc, cloud, server, cancellationToken);
                var onServer = entries.Where(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                var onPc = Directory.Exists(local)
                    ? Directory.EnumerateFileSystemEntries(local).Select(Path.GetFileName).Where(n => name.Equals(n, StringComparison.OrdinalIgnoreCase)).ToList()
                    : [];
                // Missing on one side, or two spellings on the server: a real difference, not a matter of spelling.
                if (onServer.Count != 1 || onPc.Count != 1) return false;
                if (onPc[0] != onServer[0])
                {
                    var from = Path.Combine(local, onPc[0]!);
                    var to = Path.Combine(local, onServer[0]);
                    if (Directory.Exists(from)) Directory.Move(from, to);
                    else File.Move(from, to);
                    Log.Info("Sync", $"'{pair.Id}': a rename of upper and lower case on the server carried over to the PC.");
                }
                local = Path.Combine(local, onServer[0]);
                server = Join(server, onServer[0]);
            }
        }
        return true;
    }

    private static async Task<JsonObject?> StatAsync(RcClient rc, string cloud, string path, CancellationToken cancellationToken)
    {
        var result = await rc.CallAsync("operations/stat", new JsonObject
        {
            ["fs"] = cloud,
            ["remote"] = path,
            ["opt"] = new JsonObject { ["noMimeType"] = true },
        }, TimeSpan.FromSeconds(30), cancellationToken);
        return result["item"] as JsonObject;
    }

    private static async Task<IReadOnlyList<string>> ListAsync(RcClient rc, string cloud, string folder, CancellationToken cancellationToken)
    {
        var result = await rc.CallAsync("operations/list", new JsonObject
        {
            ["fs"] = cloud,
            ["remote"] = folder,
            ["opt"] = new JsonObject { ["noMimeType"] = true, ["noModTime"] = true },
        }, TimeSpan.FromMinutes(2), cancellationToken);
        return (result["list"] as JsonArray ?? []).OfType<JsonObject>().Select(e => e["Name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList();
    }

    private static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    private static string JoinFs(string cloud, string path) => cloud.EndsWith(':') ? cloud + path : cloud + "/" + path;

    [GeneratedRegex(@"(?m)Path[12] (?:file|dir|directory) not found in Path[12]\s+-\s+(.+?)\s*$")]
    private static partial Regex OutOfSyncPattern();
}
