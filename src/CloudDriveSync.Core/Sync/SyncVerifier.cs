using System.Text.Json.Nodes;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

/// <summary>What "Abgleich überprüfen" found; paths relative to the synchronised folder.</summary>
public sealed record VerifyResult(
    long Matching,
    IReadOnlyList<string> OnlyOnPc,
    IReadOnlyList<string> OnlyInCloud,
    IReadOnlyList<string> Different,
    IReadOnlyList<string> Unreadable,
    bool ComparedContent)
{
    public int Differences => OnlyOnPc.Count + OnlyInCloud.Count + Different.Count + Unreadable.Count;
    public bool InStep => Differences == 0;
}

/// <summary>
/// "Abgleich überprüfen": compares every file of a synchronisation on the PC with the one in the cloud - names and
/// sizes, checksums where both sides have them, on request the content itself (which downloads everything). It changes
/// nothing. It uses the synchronisation's own filter, so CloudDrive-Sync's own files and the files that stay on the PC
/// on purpose do not count.
/// </summary>
internal static class SyncVerifier
{
    public static async Task<VerifyResult> VerifyAsync(RcClient rc, AppPaths paths, SyncPairSettings pair, bool compareContent, Action<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var folder = paths.SyncPairDir(pair.Id);
        Directory.CreateDirectory(folder);
        var filters = Path.Combine(folder, "filter.txt");
        SyncFilters.Write(folder, filters, pair);
        var body = new JsonObject
        {
            ["srcFs"] = pair.LocalPath,
            ["dstFs"] = BisyncCommand.CloudPath(pair),
            ["download"] = compareContent,
            ["match"] = true,
            ["_filter"] = new JsonObject { ["FilterFrom"] = new JsonArray(filters) },
        };
        var result = await rc.RunJobAsync("operations/check", body, $"check/{pair.Id}", progress, cancellationToken);
        if (!result.Success) throw new CdException(ErrorCatalog.Classify(result.Error), $"operations/check: {result.Error}");
        return new VerifyResult(
            Paths(result.Output, "match").Count,
            Paths(result.Output, "missingOnDst"),
            Paths(result.Output, "missingOnSrc"),
            Paths(result.Output, "differ"),
            Paths(result.Output, "error"),
            compareContent);
    }

    private static IReadOnlyList<string> Paths(JsonObject output, string name) =>
        (output[name] as JsonArray ?? [])
            .Select(node => node?.GetValue<string>() ?? "")
            .Where(path => path.Length > 0 && !path.StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}