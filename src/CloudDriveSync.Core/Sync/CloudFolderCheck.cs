using System.Text.Json.Nodes;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

/// <summary>
/// For cloud folders without the protection file (<see cref="SyncPairSettings.CloudCheckFile"/>): before each run
/// CloudDrive-Sync makes sure itself that the cloud folder is there and does not suddenly look empty - what the
/// protection file shows otherwise. A folder that had content at the last good run and is empty now (a server
/// answering wrongly, access taken away) stops the run before anything is deleted on the PC.
/// </summary>
internal static class CloudFolderCheck
{
    /// <summary>Null when everything is fine, otherwise what is wrong (for the technical details).</summary>
    public static async Task<string?> ProblemAsync(RcClient rc, SyncPairSettings pair, string pairFolder, CancellationToken cancellationToken)
    {
        JsonObject result;
        try
        {
            result = await rc.CallAsync("operations/list", new JsonObject
            {
                ["fs"] = BisyncCommand.CloudPath(pair),
                ["remote"] = "",
                ["opt"] = new JsonObject { ["noModTime"] = true, ["noMimeType"] = true },
            }, TimeSpan.FromMinutes(2), cancellationToken);
        }
        catch (CdException e) when (e.Code is not ("CD-5001" or "CD-3012"))
        {
            // No connection or sign-in: the run reports that itself. Anything else: the folder is not there.
            return $"cloud folder not readable: {e.Detail}";
        }
        var entries = (result["list"] as JsonArray)?.Count ?? 0;
        if (entries == 0 && KnownEntries(pairFolder) > 0) return "cloud folder appears empty although the last good run saw files in it";
        return null;
    }

    /// <summary>The entries of the cloud side at the last good run (bisync's listing of path 1).</summary>
    internal static int KnownEntries(string pairFolder)
    {
        var good = Path.Combine(pairFolder, "last-good");
        if (!Directory.Exists(good)) return 0;
        var listing = Directory.EnumerateFiles(good, "*.path1.lst").FirstOrDefault();
        return listing is null ? 0 : File.ReadLines(listing).Count(line => line.Length > 0 && !line.StartsWith('#'));
    }
}
