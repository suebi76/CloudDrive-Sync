using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using CloudDriveSync.Core.CloudFiles;
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

    /// <summary>
    /// How long a run may go on reading only what changed (see <see cref="CloudWalker"/>) before it reads the whole cloud
    /// folder again: Nextcloud does not pass every change on to the folder times above it - not always from shares or
    /// external storage. Once an hour, those are in, too.
    /// </summary>
    internal static readonly TimeSpan FullListingEvery = TimeSpan.FromHours(1);

    /// <param name="mode">Resync: first run or rebuild - both sides are merged, nothing is deleted.</param>
    /// <param name="freeUpDays">Files not used for so many days give their space back; 0 = never.</param>
    /// <param name="converting">Switching a classic synchronisation: what its last run left in step (see <see cref="InStep"/>).</param>
    /// <param name="activity">What the run is doing, for its card - large trees take a while to read.</param>
    /// <param name="placeholderCheck">Whether Windows wrote a new placeholder broken; tests stand in for Windows.</param>
    public async Task<SyncRunOutcome> RunAsync(OnDemandPair live, SyncPairSettings pair, AccountSettings account, BisyncMode mode, Action<JobProgress>? progress, bool keepTrash,
        int freeUpDays, DateTime nowUtc, CancellationToken cancellationToken, BisyncRecord? converting = null, Action<string>? activity = null, Func<string, bool>? placeholderCheck = null)
    {
        try
        {
            var outcome = await RunCoreAsync(live, pair, account, mode, progress, keepTrash, freeUpDays, nowUtc, converting, activity, placeholderCheck, cancellationToken);
            // Explorer shows it at the folder itself: what is left for the next run is no trouble, no connection is "offline".
            live.Report(outcome.Success || outcome.ErrorCode is "CD-4510" or "CD-4605" ? ProviderStatus.Idle
                : outcome.ErrorCode is "CD-5001" ? ProviderStatus.Offline : ProviderStatus.Error);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            live.Report(ProviderStatus.Idle);
            throw;
        }
    }

    private async Task<SyncRunOutcome> RunCoreAsync(OnDemandPair live, SyncPairSettings pair, AccountSettings account, BisyncMode mode, Action<JobProgress>? progress, bool keepTrash,
        int freeUpDays, DateTime nowUtc, BisyncRecord? converting, Action<string>? activity, Func<string, bool>? placeholderCheck, CancellationToken cancellationToken)
    {
        // Without the folder (e.g. its drive is not connected) nothing runs - an empty folder is never taken as "all deleted".
        if (!Directory.Exists(pair.LocalPath)) return Failed("CD-4501", pair.LocalPath, SyncDecision.Folder, retryable: true);
        if (!File.Exists(Path.Combine(pair.LocalPath, SyncFilters.SentinelFile)))
            return Failed("CD-4503", Path.Combine(pair.LocalPath, SyncFilters.SentinelFile), SyncDecision.Folder, retryable: false);
        try
        {
            live.EnsureConnected();
            // A connection Windows no longer takes shows in the first report: the folder is connected again at once.
            if (!live.Report(ProviderStatus.Syncing))
            {
                live.EnsureConnected();
                live.Report(ProviderStatus.Syncing);
            }
            // Online-only files that went with a lost registration are never taken as deleted - not even when deletions
            // were confirmed before.
            if (live.MergeNeeded) mode = BisyncMode.Resync;
            var rc = await _engine.EnsureRunningAsync(cancellationToken);
            var folder = _paths.SyncPairDir(pair.Id);
            Directory.CreateDirectory(folder);
            var filters = Path.Combine(folder, "filter.txt");
            var rules = SyncFilters.Build(pair);
            await File.WriteAllTextAsync(filters, rules, cancellationToken);

            // Nextcloud passes every change below a folder on to the folder's time: a run reads only what may have changed
            // since the last one. Not when rebuilding or switching - and every hour everything (FullListingEvery). What
            // was kept goes now and comes back only with a run that succeeds: after anything else, everything is read.
            var nextcloud = account.Kind == WebDavKind.Nextcloud;
            var trees = new CloudTreeStore(folder);
            var saved = nextcloud && mode == BisyncMode.Normal && converting is null ? trees.Load(rules) : null;
            if (saved is not null && nowUtc - saved.FullUtc >= FullListingEvery) saved = null;
            trees.Forget();

            var clock = Stopwatch.StartNew();
            activity?.Invoke("Liest die Cloud …");
            var rootTicks = nextcloud && converting is null ? await Listings.RootTicksAsync(rc, pair, cancellationToken) : 0;
            var cloud = await Listings.ListCloudAsync(rc, pair, filters, nextcloud, cancellationToken,
                p => activity?.Invoke($"Liest die Cloud: {Count(p.Folders)} Ordner, {Count(p.Files)} Dateien …"), saved?.Tree, rootTicks, () => nowUtc);
            var cloudTime = clock.Elapsed;
            if (pair.CloudCheckFile && !cloud.SentinelFound)
                return Failed("CD-4503", "check file check failed: the protection file is missing in the cloud folder", SyncDecision.Folder, retryable: false);
            var known = live.Store.All();
            if (!pair.CloudCheckFile && cloud.Entries.Count == 0 && known.Count > 0)
                return Failed("CD-4512", "cloud folder appears empty although the last run saw files in it", SyncDecision.Folder, retryable: false);
            foreach (var clash in cloud.CaseClashes) Log.Warn("OnDemand", $"'{pair.Id}': '{clash}' left out - another name in the cloud differs only in upper and lower case.");
            clock.Restart();
            activity?.Invoke("Liest den Ordner auf diesem PC …");
            var pc = await Listings.ListLocalAsync(rc, pair.LocalPath, filters, cancellationToken);
            pc = Listings.WithUnlisted(pc, pair.LocalPath, known.Where(item => cloud.Entries.ContainsKey(item.Path)).Select(item => item.Path));
            var local = pc.Entries;
            var localTime = clock.Elapsed;
            // Refused by Windows in the last run and gone now: cleaned up (a broken placeholder removed in Safe Mode), not
            // deleted by the user - nothing deletes such an entry. It is made again from the cloud, never deleted there.
            var vanished = RefusedOnPc.Vanished(RefusedOnPc.Load(folder), local, pc.Refused);
            if (vanished.Count > 0)
            {
                var gone = known.Where(item => vanished.Any(v => item.Path.Equals(v, StringComparison.OrdinalIgnoreCase)
                    || item.Path.StartsWith(v + "/", StringComparison.OrdinalIgnoreCase))).ToList();
                live.Store.Batch(() =>
                {
                    foreach (var item in gone) live.Store.Remove(item.Id);
                });
                known = live.Store.All();
                Log.Info("OnDemand", $"'{pair.Id}': {vanished.Count} entry(s) Windows refused before are gone now; {gone.Count} item(s) come again from the cloud.");
            }
            RefusedOnPc.Save(folder, pc.Refused, pc.Broken);
            // What the server does not let be read and what Windows refuses on the PC are left out alike: unknown, never deleted.
            var leftOut = cloud.Unreadable.Concat(pc.Refused).Distinct(StringComparer.Ordinal).ToList();

            var inStep = converting is null ? null : InStep(converting, cloud.Entries, local);
            var plan = Planner.Plan(new Planner.Input(known, cloud.Entries, local, Rebuild: mode == BisyncMode.Resync, leftOut, inStep));
            foreach (var skipped in plan.Skipped) Log.Info("OnDemand", $"'{pair.Id}': {skipped}");
            if (mode == BisyncMode.Normal && plan.TooManyDeletions(pair.MaxDeletePercent, DeleteGuard.MinimumDeletions))
            {
                Log.Warn("OnDemand", $"Run of '{pair.Id}' stopped: {plan.Deletions} of {plan.KnownFiles} files would be deleted.");
                return Failed("CD-4502", $"too many deletes (>{pair.MaxDeletePercent}%, {plan.Deletions} of {plan.KnownFiles}) (CloudDrive-Sync, files on demand)",
                    SyncDecision.Deletions, retryable: false);
            }
            Log.Info("OnDemand", $"Run of '{pair.Id}' started ({mode}): {plan.Actions.Count} step(s); cloud listed in {cloudTime.TotalSeconds:0.0} s "
                + $"({cloud.Entries.Count} entries in {cloud.Folders} folders, {cloud.Read} read{(cloud.Unreadable.Count > 0 ? $", {cloud.Unreadable.Count} not readable" : "")}), "
                + $"PC in {localTime.TotalSeconds:0.0} s ({local.Count} entries{(pc.Refused.Count > 0 ? $", {pc.Refused.Count} refused by Windows" : "")}).");
            if (plan.Actions.Count > 0) activity?.Invoke($"Gleicht ab: {Count(plan.Actions.Count)} Schritte …");
            clock.Restart();

            var executor = new Executor(rc, _files, live.Store, pair, account, keepTrash, cloud.Entries.Keys, progress, cancellationToken,
                PlaceholderAlarm.IsRaised(folder), placeholderCheck);
            var result = await executor.RunAsync(plan);
            if (result.Broken.Count > 0) PlaceholderAlarm.Raise(folder, result.Broken.Count);
            if (mode == BisyncMode.Resync && live.MergeNeeded) live.Merged();
            executor.ApplyPinStates(local);
            executor.MarkFoldersInSync(result.Failed.Concat(result.Locked), leftOut);
            var freed = executor.FreeUpSpace(local, freeUpDays, nowUtc);
            var space = Executor.Measure(local, cloud.Entries.Values, freed);
            var final = new JobProgress(result.Bytes, result.Bytes, result.Transfers, result.Transfers, 0, 0, 0, result.Failed.Count);
            var conflicts = SyncRunner.FindConflicts(pair.LocalPath);
            var localOnly = result.LocalOnlyAdded.Count > 0 ? result.LocalOnlyAdded : null;
            if (result.Broken.Count > 0 || result.HeldBack > 0)
            {
                var detail = result.Broken.Count > 0
                    ? $"Windows wrote {result.Broken.Count} new placeholder(s) broken, e.g. {result.Broken[0]}; {result.HeldBack} more held back"
                    : $"{result.HeldBack} new entr(y/ies) held back since Windows wrote placeholders broken";
                Log.Warn("OnDemand", $"Run of '{pair.Id}': {detail}.");
                return new SyncRunOutcome(false, "CD-4610", detail, SyncDecision.None, final, result.Deletes, conflicts, false, localOnly, result.Changes, space);
            }
            if (result.Locked.Count > 0)
                return new SyncRunOutcome(false, "CD-4510", string.Join(", ", result.Locked.Take(5)), SyncDecision.None, final, result.Deletes, conflicts, true, localOnly, result.Changes, space);
            if (result.Failed.Count > 0)
                return new SyncRunOutcome(false, "CD-4605", string.Join(", ", result.Failed.Take(5)), SyncDecision.None, final, result.Deletes, conflicts, true, localOnly, result.Changes, space);
            // What this run changed in the cloud is read again next time, whatever the folder times say.
            if (nextcloud && converting is null && cloud.Tree is not null)
                trees.Save(rules, cloud.Tree, saved?.FullUtc ?? nowUtc, plan.Actions.SelectMany(a => a is MoveCloud move ? [a.Path, move.Item.Path] : new[] { a.Path }));
            Log.Info("OnDemand", $"Run of '{pair.Id}' succeeded in {clock.Elapsed.TotalSeconds:0.0} s: {result.Transfers} transfers, {result.Deletes} deletions, {conflicts.Count} conflict copies.");
            return new SyncRunOutcome(true, null, null, SyncDecision.None, final, result.Deletes, conflicts, false, localOnly, result.Changes, space);
        }
        catch (CdException e) when (e.Code != "CD-9000")
        {
            Log.Warn("OnDemand", $"Run of '{pair.Id}' failed: {e.Code} {e.Detail ?? e.Message}");
            var decision = e.Code == "CD-3012" ? SyncDecision.SignIn : SyncDecision.None;
            return Failed(e.Code, e.Detail ?? e.Message, decision, retryable: decision == SyncDecision.None);
        }
    }

    /// <summary>
    /// Switching a classic synchronisation: the files neither side changed since bisync's last run - on the PC and in the
    /// cloud they still have the size and time bisync noted (to the second). Only these count as the same without a
    /// checksum; a file of the same size that changed on one side meanwhile is kept in both versions.
    /// </summary>
    internal static HashSet<string> InStep(BisyncRecord before, IReadOnlyDictionary<string, CloudEntry> cloud, IReadOnlyList<LocalEntry> local)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in local)
        {
            if (entry.IsDirectory || !before.Pc.TryGetValue(entry.Path, out var pc) || !Unchanged(pc, entry.Size, entry.Ticks)) continue;
            if (!cloud.TryGetValue(entry.Path, out var now) || !before.Cloud.TryGetValue(entry.Path, out var noted) || !Unchanged(noted, now.Size, now.Ticks)) continue;
            result.Add(entry.Path);
        }
        return result;

        static bool Unchanged(NotedFile noted, long size, long ticks) => noted.Size == size && Math.Abs(noted.Time.Ticks - ticks) <= TimeSpan.TicksPerSecond;
    }

    private static string Count(long number) => number.ToString("N0", CultureInfo.GetCultureInfo("de-DE"));

    private static SyncRunOutcome Failed(string code, string detail, SyncDecision decision, bool retryable) =>
        new(false, code, detail, decision, JobProgress.None, 0, [], retryable);
}
