using System.Runtime.Versioning;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.OnDemand;

/// <summary>
/// Carries out one run of a synchronisation with files on demand, with the same result as a classic run
/// (<see cref="SyncRunOutcome"/>), so scheduling, retries, decisions and the activity list work alike. First the checks
/// that need no network (folder, protection file), then the registration with Windows, both listings, the plan and its
/// safety checks (protection file in the cloud, a cloud folder that suddenly looks empty, too many deletions) - only
/// then anything changes.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed class OnDemandRunner
{
    private readonly AppPaths _paths;
    private readonly RcloneEngine _engine;
    private readonly FileServer _files;

    public OnDemandRunner(AppPaths paths, RcloneEngine engine, FileServer files)
    {
        _paths = paths;
        _engine = engine;
        _files = files;
    }

    /// <param name="mode">Resync: first run or rebuild - both sides are merged, nothing is deleted.</param>
    public async Task<SyncRunOutcome> RunAsync(OnDemandPair live, SyncPairSettings pair, AccountSettings account, BisyncMode mode, Action<JobProgress>? progress, bool keepTrash, CancellationToken cancellationToken)
    {
        // Without the folder (e.g. its drive is not connected) nothing runs - an empty folder is never taken as "all deleted".
        if (!Directory.Exists(pair.LocalPath)) return Failed("CD-4501", pair.LocalPath, SyncDecision.Folder, retryable: true);
        if (!File.Exists(Path.Combine(pair.LocalPath, SyncFilters.SentinelFile)))
            return Failed("CD-4503", Path.Combine(pair.LocalPath, SyncFilters.SentinelFile), SyncDecision.Folder, retryable: false);
        try
        {
            live.EnsureConnected();
            // Online-only files that went with a lost registration are never taken as deleted - not even when deletions
            // were confirmed before.
            if (live.MergeNeeded) mode = BisyncMode.Resync;
            var rc = await _engine.EnsureRunningAsync(cancellationToken);
            var folder = _paths.SyncPairDir(pair.Id);
            Directory.CreateDirectory(folder);
            var filters = Path.Combine(folder, "filter.txt");
            await File.WriteAllTextAsync(filters, SyncFilters.Build(pair), cancellationToken);

            var cloud = await Listings.ListCloudAsync(rc, pair, filters, account.Kind == WebDavKind.Nextcloud, cancellationToken);
            if (pair.CloudCheckFile && !cloud.SentinelFound)
                return Failed("CD-4503", "check file check failed: the protection file is missing in the cloud folder", SyncDecision.Folder, retryable: false);
            var known = live.Store.All();
            if (!pair.CloudCheckFile && cloud.Entries.Count == 0 && known.Count > 0)
                return Failed("CD-4512", "cloud folder appears empty although the last run saw files in it", SyncDecision.Folder, retryable: false);
            foreach (var clash in cloud.CaseClashes) Log.Warn("OnDemand", $"'{pair.Id}': '{clash}' left out - another name in the cloud differs only in upper and lower case.");
            var local = await Listings.ListLocalAsync(rc, pair.LocalPath, filters, cancellationToken);

            var plan = Planner.Plan(new Planner.Input(known, cloud.Entries, local, Rebuild: mode == BisyncMode.Resync));
            foreach (var skipped in plan.Skipped) Log.Info("OnDemand", $"'{pair.Id}': {skipped}");
            if (mode == BisyncMode.Normal && plan.TooManyDeletions(pair.MaxDeletePercent, DeleteGuard.MinimumDeletions))
            {
                Log.Warn("OnDemand", $"Run of '{pair.Id}' stopped: {plan.Deletions} of {plan.KnownFiles} files would be deleted.");
                return Failed("CD-4502", $"too many deletes (>{pair.MaxDeletePercent}%, {plan.Deletions} of {plan.KnownFiles}) (CloudDrive-Sync, files on demand)",
                    SyncDecision.Deletions, retryable: false);
            }
            Log.Info("OnDemand", $"Run of '{pair.Id}' started ({mode}): {plan.Actions.Count} step(s).");

            var executor = new Executor(rc, _files, live.Store, pair, account, keepTrash, cloud.Entries.Keys, progress, cancellationToken);
            var result = await executor.RunAsync(plan);
            if (mode == BisyncMode.Resync && live.MergeNeeded) live.Merged();
            executor.ApplyPinStates(local);
            var final = new JobProgress(result.Bytes, result.Bytes, result.Transfers, result.Transfers, 0, 0, 0, result.Failed.Count);
            var conflicts = SyncRunner.FindConflicts(pair.LocalPath);
            var localOnly = result.LocalOnlyAdded.Count > 0 ? result.LocalOnlyAdded : null;
            if (result.Locked.Count > 0)
                return new SyncRunOutcome(false, "CD-4510", string.Join(", ", result.Locked.Take(5)), SyncDecision.None, final, result.Deletes, conflicts, true, localOnly, result.Changes);
            if (result.Failed.Count > 0)
                return new SyncRunOutcome(false, "CD-4605", string.Join(", ", result.Failed.Take(5)), SyncDecision.None, final, result.Deletes, conflicts, true, localOnly, result.Changes);
            Log.Info("OnDemand", $"Run of '{pair.Id}' succeeded: {result.Transfers} transfers, {result.Deletes} deletions, {conflicts.Count} conflict copies.");
            return new SyncRunOutcome(true, null, null, SyncDecision.None, final, result.Deletes, conflicts, false, localOnly, result.Changes);
        }
        catch (CdException e) when (e.Code != "CD-9000")
        {
            Log.Warn("OnDemand", $"Run of '{pair.Id}' failed: {e.Code} {e.Detail ?? e.Message}");
            var decision = e.Code == "CD-3012" ? SyncDecision.SignIn : SyncDecision.None;
            return Failed(e.Code, e.Detail ?? e.Message, decision, retryable: decision == SyncDecision.None);
        }
    }

    private static SyncRunOutcome Failed(string code, string detail, SyncDecision decision, bool retryable) =>
        new(false, code, detail, decision, JobProgress.None, 0, [], retryable);
}
