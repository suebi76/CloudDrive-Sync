using System.Text.Json.Nodes;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

/// <summary>How a run of bisync is carried out.</summary>
public enum BisyncMode
{
    /// <summary>A normal run: changes on both sides since the last run are carried over.</summary>
    Normal,
    /// <summary>First run or rebuild: both sides are merged; nothing is deleted.</summary>
    Resync,
    /// <summary>A normal run that applies deletions beyond the safety limit (the user confirmed them).</summary>
    Force,
}

/// <summary>
/// Builds the request for rclone's "sync/bisync" with CloudDrive-Sync's safety settings. The cloud is path 1, the PC
/// path 2.
/// </summary>
public static class BisyncCommand
{
    public const string ConflictSuffix = "Konflikt-Cloud,Konflikt-PC";

    public static JsonObject Build(SyncPairSettings pair, AccountSettings account, string workDir, string filtersFile, BisyncMode mode, string resyncMode = "newer", DateTimeOffset? now = null, bool keepTrash = true)
    {
        var stamp = SyncTrash.NewStamp(now);
        var (resolve, _) = Conflicts(pair.Conflicts);
        var body = new JsonObject
        {
            ["path1"] = CloudPath(pair),
            ["path2"] = pair.LocalPath,
            ["workdir"] = workDir,
            ["filtersFile"] = filtersFile,
            // A sentinel file on both sides: is a folder gone or moved, bisync stops instead of deleting everything.
            // Without one in the cloud, SyncRunner checks the cloud folder itself before the run (CloudFolderCheck).
            ["checkAccess"] = pair.CloudCheckFile,
            ["checkFilename"] = SyncFilters.SentinelFile,
            // More deletions than allowed stop the run until the user decides.
            ["maxDelete"] = Math.Clamp(pair.MaxDeletePercent, 1, 100),
            // Size, time and checksum - each side uses what it supports; local checksums only where needed.
            ["compare"] = "size,modtime,checksum",
            ["slowHashSyncOnly"] = true,
            ["conflictResolve"] = resolve,
            ["conflictLoser"] = "num",
            ["conflictSuffix"] = ConflictSuffix,
            // Continue cleanly after an interruption; retry less serious errors at the next run.
            ["recover"] = true,
            ["resilient"] = true,
            ["maxLock"] = "30m",
            ["createEmptySrcDirs"] = true,
            ["_config"] = new JsonObject
            {
                // Renamed or moved files are not uploaded again; conflict copies keep their extension (".docx").
                ["TrackRenames"] = true,
                ["SuffixKeepExtension"] = true,
            },
        };
        // Files the synchronisation deletes or replaces on the PC go to its recycle bin there (excluded from the sync) -
        // unless the user switched it off.
        if (keepTrash) body["backupDir2"] = Path.Combine(pair.LocalPath, SyncFilters.TrashFolder, stamp);
        // No recycle bin folder in the cloud: a file deleted on the PC is deleted on the server, as everyone expects
        // (it is still in the Windows recycle bin; Nextcloud keeps its own). A folder in the cloud folder would stay
        // visible on the server - in shared folders even to everybody else.
        if (mode == BisyncMode.Resync)
        {
            body["resync"] = true;
            body["resyncMode"] = resyncMode;
        }
        if (mode == BisyncMode.Force) body["force"] = true;
        return body;
    }

    public static string CloudPath(SyncPairSettings pair) => $"{AccountService.RemoteName(pair.AccountId)}:{pair.RemotePath.Trim('/')}";

    public static (string Resolve, string Description) Conflicts(ConflictPolicy policy) => policy switch
    {
        ConflictPolicy.KeepBoth => ("none", "beide Fassungen werden umbenannt und behalten"),
        ConflictPolicy.CloudWins => ("path1", "die Cloud-Fassung behält den Namen"),
        ConflictPolicy.PcWins => ("path2", "die PC-Fassung behält den Namen"),
        _ => ("newer", "die neuere Fassung behält den Namen"),
    };

    private static string Join(params string[] parts) =>
        string.Join('/', parts.Select(p => p.Trim('/')).Where(p => p.Length > 0));
}
