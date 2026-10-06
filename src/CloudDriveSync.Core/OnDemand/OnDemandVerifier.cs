using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.OnDemand;

/// <summary>
/// "Abgleich überprüfen" for a synchronisation with files on demand - without fetching anything. A file that is only
/// online is compared with the cloud by name and size: once fetched, its content is the cloud's anyway. Files whose data
/// is on the PC are compared as in classic synchronisations - checksums where the server has them, on request the
/// content - and rclone is given exactly these files, so it never touches an online-only one. A change on the PC that is
/// not uploaded yet counts as different. It changes nothing.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal static class OnDemandVerifier
{
    public static async Task<VerifyResult> VerifyAsync(RcClient rc, AppPaths paths, SyncPairSettings pair, AccountSettings account, bool compareContent,
        Action<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var folder = paths.SyncPairDir(pair.Id);
        Directory.CreateDirectory(folder);
        var filters = Path.Combine(folder, "filter.txt");
        await File.WriteAllTextAsync(filters, SyncFilters.Build(pair), cancellationToken);
        var cloud = await Listings.ListCloudAsync(rc, pair, filters, account.Kind == WebDavKind.Nextcloud, cancellationToken);
        var local = await Listings.ListLocalAsync(rc, pair.LocalPath, filters, cancellationToken);

        var inCloud = cloud.Entries.Values.Where(e => !e.IsDirectory).ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var onlyOnPc = new List<string>();
        var different = new List<string>();
        var onPc = new List<string>();
        long matching = 0;
        foreach (var entry in local.Where(e => !e.IsDirectory))
        {
            if (!inCloud.Remove(entry.Path, out var there)) onlyOnPc.Add(entry.Path);
            else if (entry.Size != there.Size || (entry.IsPlaceholder && !entry.InSync)) different.Add(entry.Path);
            else if (entry.OnDisk) onPc.Add(entry.Path);
            else matching++;
        }
        var onlyInCloud = inCloud.Keys.ToList();
        var unreadable = cloud.Unreadable.Select(f => f + "/").ToList();

        if (onPc.Count > 0)
        {
            var list = Path.Combine(folder, "check-files.txt");
            await File.WriteAllLinesAsync(list, onPc, cancellationToken);
            var body = new JsonObject
            {
                ["srcFs"] = pair.LocalPath,
                ["dstFs"] = BisyncCommand.CloudPath(pair),
                ["download"] = compareContent,
                ["match"] = true,
                ["_filter"] = new JsonObject { ["FilesFrom"] = new JsonArray(list) },
            };
            var result = await rc.RunJobAsync("operations/check", body, $"check/{pair.Id}", progress, cancellationToken);
            if (!result.Success) throw new CdException(ErrorCatalog.Classify(result.Error), $"operations/check: {result.Error}");
            matching += Paths(result.Output, "match").Count;
            onlyOnPc.AddRange(Paths(result.Output, "missingOnDst"));
            onlyInCloud.AddRange(Paths(result.Output, "missingOnSrc"));
            different.AddRange(Paths(result.Output, "differ"));
            unreadable.AddRange(Paths(result.Output, "error"));
        }
        return new VerifyResult(matching, Sorted(onlyOnPc), Sorted(onlyInCloud), Sorted(different), Sorted(unreadable), compareContent);
    }

    private static List<string> Paths(JsonObject output, string name) =>
        (output[name] as JsonArray ?? []).Select(node => node?.GetValue<string>() ?? "").Where(path => path.Length > 0).ToList();

    private static IReadOnlyList<string> Sorted(IEnumerable<string> paths) =>
        paths.Where(p => !p.StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
}
