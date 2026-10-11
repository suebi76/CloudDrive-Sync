using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudDriveSync.Core.Accounts;
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
    /// external storage. Every two hours (as the Nextcloud client does), those are in, too.
    /// </summary>
    internal static readonly TimeSpan FullListingEvery = TimeSpan.FromHours(2);

    /// <param name="mode">Resync: first run or rebuild - both sides are merged, nothing is deleted.</param>
    /// <param name="freeUpDays">Files not used for so many days give their space back; 0 = never.</param>
    /// <param name="converting">Switching a classic synchronisation: what its last run left in step (see <see cref="InStep"/>).</param>
    /// <param name="activity">What the run is doing, for its card - large trees take a while to read.</param>
    /// <param name="placeholderCheck">Whether Windows wrote a new placeholder broken; tests stand in for Windows.</param>
    /// <param name="pcOnly">Only a change on the PC asked for the run: the cloud may be taken as last read (see <see cref="QuickCloudAsync"/>).</param>
    public async Task<SyncRunOutcome> RunAsync(OnDemandPair live, SyncPairSettings pair, AccountSettings account, BisyncMode mode, Action<JobProgress>? progress, bool keepTrash,
        int freeUpDays, DateTime nowUtc, CancellationToken cancellationToken, BisyncRecord? converting = null, Action<string>? activity = null, Func<string, bool>? placeholderCheck = null,
        bool pcOnly = false)
    {
        try
        {
            var cloudChanged = false;
            var outcome = await RunCoreAsync(live, pair, account, mode, progress, keepTrash, freeUpDays, nowUtc, converting, activity, placeholderCheck, pcOnly,
                () => cloudChanged = true, cancellationToken);
            if (cloudChanged && outcome.Success)
            {
                // The cloud was not as the run took it: it is read now, rather than when the interval comes.
                Log.Info("OnDemand", $"'{pair.Id}': the cloud changed since it was last read; reading it now.");
                var full = await RunCoreAsync(live, pair, account, mode, progress, keepTrash, freeUpDays, nowUtc, converting, activity, placeholderCheck, false,
                    () => { }, cancellationToken);
                outcome = full with
                {
                    Deletes = outcome.Deletes + full.Deletes,
                    LocalOnlyAdded = Both(outcome.LocalOnlyAdded, full.LocalOnlyAdded),
                    Changes = Both(outcome.Changes, full.Changes),
                };
            }
            // Explorer shows it at the folder itself: what is left for the next run is no trouble, no connection is "offline".
            live.Report(outcome.Success || outcome.ErrorCode is "CD-4510" or "CD-4513" or "CD-4605" ? ProviderStatus.Idle
                : outcome.ErrorCode is "CD-5001" ? ProviderStatus.Offline : ProviderStatus.Error);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            live.Report(ProviderStatus.Idle);
            throw;
        }
    }

    private static IReadOnlyList<T>? Both<T>(IReadOnlyList<T>? first, IReadOnlyList<T>? second) =>
        first is null || first.Count == 0 ? second : second is null || second.Count == 0 ? first : [.. first, .. second];

    private async Task<SyncRunOutcome> RunCoreAsync(OnDemandPair live, SyncPairSettings pair, AccountSettings account, BisyncMode mode, Action<JobProgress>? progress, bool keepTrash,
        int freeUpDays, DateTime nowUtc, BisyncRecord? converting, Action<string>? activity, Func<string, bool>? placeholderCheck, bool pcOnly, Action cloudChanged,
        CancellationToken cancellationToken)
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
            // since the last one. Not when rebuilding or switching - and every two hours everything (FullListingEvery). What
            // was kept goes now and comes back with a run that carried its plan out (single files may have failed); after a
            // run that broke off as a whole, everything is read.
            var nextcloud = account.Kind == WebDavKind.Nextcloud;
            var trees = new CloudTreeStore(folder);
            var saved = nextcloud && mode == BisyncMode.Normal && converting is null ? trees.Load(rules) : null;
            if (saved is not null && nowUtc - saved.FullUtc >= FullListingEvery) saved = null;
            trees.Forget();

            var clock = Stopwatch.StartNew();
            // Servers without folder times that pass changes on (IServ, WebDAV): a change on the PC alone goes up without
            // reading the whole cloud folder, where that is safe (QuickCloudAsync).
            var quickable = !nextcloud && converting is null;
            var cloud = quickable && pcOnly && mode == BisyncMode.Normal ? await QuickCloudAsync(live.Store, folder, pair, rules, nowUtc, cancellationToken) : null;
            var quick = cloud is not null;
            if (cloud is null)
            {
                // Until this run has carried its plan out, no later run takes the cloud from the store (switching, too).
                if (!nextcloud) LastListing.Clear(live.Store);
                activity?.Invoke("Liest die Cloud …");
                var rootTicks = nextcloud && converting is null ? await Listings.RootTicksAsync(rc, pair, cancellationToken) : 0;
                cloud = await Listings.ListCloudAsync(rc, pair, filters, nextcloud, cancellationToken,
                    p => activity?.Invoke($"Liest die Cloud: {Count(p.Folders)} Ordner, {Count(p.Files)} Dateien …"), saved?.Tree, rootTicks, () => nowUtc);
            }
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
            if (mode == BisyncMode.Normal && plan.TooManyDeletions(pair.MaxDeletePercent, pair.MaxDeleteFiles))
            {
                Log.Warn("OnDemand", $"Run of '{pair.Id}' stopped: {plan.Deletions} of {plan.KnownFiles} files would be deleted.");
                // Path2 = deleted on the PC (would go in the cloud), Path1 = deleted in the cloud (would leave the PC).
                var sides = plan.LocalDeletions == 0 ? ") on Path2" : plan.CloudDeletions == 0 ? ") on Path1"
                    : $": {plan.CloudDeletions} on Path2, {plan.LocalDeletions} on Path1)";
                return Failed("CD-4502", $"too many deletes ({DeleteGuard.Limits(pair)}, {plan.Deletions} of {plan.KnownFiles}{sides} (CloudDrive-Sync, files on demand)",
                    SyncDecision.Deletions, retryable: false);
            }
            var cloudText = quick ? $"cloud not read (PC changes only; {cloud.Entries.Count} entries as last read)"
                : $"cloud listed in {cloudTime.TotalSeconds:0.0} s ({cloud.Entries.Count} entries in {cloud.Folders} folders, {cloud.Read} read"
                + $"{(cloud.Unreadable.Count > 0 ? $", {cloud.Unreadable.Count} not readable" : "")})";
            Log.Info("OnDemand", $"Run of '{pair.Id}' started ({mode}): {plan.Actions.Count} step(s); {cloudText}, "
                + $"PC in {localTime.TotalSeconds:0.0} s ({local.Count} entries{(pc.Refused.Count > 0 ? $", {pc.Refused.Count} refused by Windows" : "")}).");
            if (plan.Actions.Count > 0) activity?.Invoke($"Gleicht ab: {Count(plan.Actions.Count)} Schritte …");
            clock.Restart();

            var executor = new Executor(rc, _files, live.Store, pair, account, keepTrash, cloud.Entries.Keys, progress, cancellationToken,
                PlaceholderAlarm.IsRaised(folder), placeholderCheck);
            var result = await executor.RunAsync(plan);
            if (quick && result.CloudChanged > 0)
            {
                LastListing.Clear(live.Store);
                cloudChanged();
            }
            // What the store knows is the cloud as listed, with everything this run did to it: what left the selection is
            // forgotten, and no step found the cloud other than listed.
            else if (!quick && quickable && result.CloudChanged == 0 && plan.Actions.OfType<Forget>().All(f => live.Store.Find(f.Item.Id) is null))
                LastListing.Save(live.Store, nowUtc, rules, cloud.Unreadable);
            if (result.Broken.Count > 0) PlaceholderAlarm.Raise(folder, result.Broken.Count);
            if (mode == BisyncMode.Resync && live.MergeNeeded) live.Merged();
            executor.ApplyPinStates(local);
            executor.MarkFoldersInSync(result.Failed.Concat(result.Locked).Concat(result.ServerLocked), leftOut);
            var freed = executor.FreeUpSpace(local, freeUpDays, nowUtc);
            var space = Executor.Measure(local, cloud.Entries.Values, freed);
            var final = new JobProgress(result.Bytes, result.Bytes, result.Transfers, result.Transfers, 0, 0, 0, result.Failed.Count);
            var conflicts = SyncRunner.FindConflicts(pair.LocalPath);
            var localOnly = result.LocalOnlyAdded.Count > 0 ? result.LocalOnlyAdded : null;
            // What the listing saw is kept also when single files failed: only what this run touched is read again next
            // time. Before, one file the server refused made every following run read the whole cloud folder again - 3,649
            // folders, four minutes - and fail on the same file. A run that broke off as a whole keeps nothing.
            if (nextcloud && converting is null && cloud.Tree is not null)
                trees.Save(rules, cloud.Tree, saved?.FullUtc ?? nowUtc, plan.Actions.SelectMany(a => a is MoveCloud move ? [a.Path, move.Item.Path] : new[] { a.Path }));
            // Not read: the folders left out stay as the last listing found them.
            var leftOutFolders = quick ? null : cloud.Unreadable;
            if (result.Broken.Count > 0 || result.HeldBack > 0)
            {
                var detail = result.Broken.Count > 0
                    ? $"Windows wrote {result.Broken.Count} new placeholder(s) broken, e.g. {result.Broken[0]}; {result.HeldBack} more held back"
                    : $"{result.HeldBack} new entr(y/ies) held back since Windows wrote placeholders broken";
                Log.Warn("OnDemand", $"Run of '{pair.Id}': {detail}.");
                return new SyncRunOutcome(false, "CD-4610", detail, SyncDecision.None, final, result.Deletes, conflicts, false, localOnly, result.Changes, space, leftOutFolders);
            }
            if (result.Locked.Count > 0)
                return new SyncRunOutcome(false, "CD-4510", string.Join(", ", result.Locked.Take(5)), SyncDecision.None, final, result.Deletes, conflicts, true, localOnly, result.Changes, space,
                    leftOutFolders);
            if (result.ServerLocked.Count > 0)
                return new SyncRunOutcome(false, "CD-4513", string.Join(", ", result.ServerLocked.Take(5)), SyncDecision.None, final, result.Deletes, conflicts, true, localOnly,
                    result.Changes, space, leftOutFolders);
            if (result.Failed.Count > 0)
                return new SyncRunOutcome(false, "CD-4605", string.Join(", ", result.Failed.Take(5)), SyncDecision.None, final, result.Deletes, conflicts, true, localOnly, result.Changes, space,
                    leftOutFolders);
            Log.Info("OnDemand", $"Run of '{pair.Id}' succeeded in {clock.Elapsed.TotalSeconds:0.0} s: {result.Transfers} transfers, {result.Deletes} deletions, {conflicts.Count} conflict copies.");
            return new SyncRunOutcome(true, null, null, SyncDecision.None, final, result.Deletes, conflicts, false, localOnly, result.Changes, space, leftOutFolders);
        }
        catch (CdException e) when (e.Code != "CD-9000")
        {
            Log.Warn("OnDemand", $"Run of '{pair.Id}' failed: {e.Code} {e.Detail ?? e.Message}");
            var decision = e.Code == "CD-3012" ? SyncDecision.SignIn : SyncDecision.None;
            return Failed(e.Code, e.Detail ?? e.Message, decision, retryable: decision == SyncDecision.None);
        }
    }

    /// <summary>
    /// The cloud side for a run that only a change on the PC asked for, taken from the store instead of reading the cloud
    /// folder - on servers without folder times that pass changes on, reading means every folder (IServ: 859 folders,
    /// 20 s); now only the folder a new file goes into is looked at. Only while that is safe: the whole cloud folder was read less than one
    /// interval ago (two hours at most) by a run that carried its plan out, with the filter rules of now, and Windows
    /// refused nothing on the PC. The protection file is looked at all the same. Every step that changes the cloud looks
    /// at it again first (Executor); when it was changed meanwhile, the cloud is read at once. What changed only in the
    /// cloud comes with the next run that reads it - at the latest when the interval comes. Null: the cloud is read.
    /// </summary>
    private async Task<Listings.CloudSide?> QuickCloudAsync(ItemStore store, string folder, SyncPairSettings pair, string rules, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var last = LastListing.Load(store);
        if (last is null || last.Rules != LastListing.Hash(rules)) return null;
        var age = nowUtc - new DateTime(last.Ticks, DateTimeKind.Utc);
        var interval = TimeSpan.FromMinutes(Math.Clamp(pair.IntervalMinutes, 1, 1440));
        if (age < TimeSpan.Zero || age >= interval || age >= FullListingEvery) return null;
        if (RefusedOnPc.Load(folder).Count > 0) return null;
        if (pair.CloudCheckFile && await _files.StatAsync(AccountService.RemoteName(pair.AccountId) + ":", CloudFetcher.CloudPath(pair, SyncFilters.SentinelFile),
                withHash: false, cancellationToken) is null)
            return null;
        var entries = store.All().ToDictionary(item => item.Path, item => new CloudEntry(item.Path, item.IsDirectory, item.CloudSize, item.CloudTicks, item.CloudHash),
            StringComparer.Ordinal);
        return new Listings.CloudSide(entries, SentinelFound: pair.CloudCheckFile, CaseClashes: [], last.Unreadable, Folders: 0);
    }

    /// <summary>
    /// When the cloud folder was last read as a whole by a run that carried its plan out (UTC ticks of the run's start),
    /// with which filter rules, and the folders the server did not let be read then - kept in the store, cleared while a
    /// run reads the cloud, so a run that breaks off leaves none behind.
    /// </summary>
    private sealed record LastListing(long Ticks, string Rules, IReadOnlyList<string> Unreadable)
    {
        private const string Key = "cloud-listed";

        public static LastListing? Load(ItemStore store)
        {
            try
            {
                return store.GetMeta(Key) is { Length: > 0 } text ? JsonSerializer.Deserialize<LastListing>(text) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static void Save(ItemStore store, DateTime listedUtc, string rules, IReadOnlyList<string> unreadable) =>
            store.SetMeta(Key, JsonSerializer.Serialize(new LastListing(listedUtc.Ticks, Hash(rules), unreadable)));

        public static void Clear(ItemStore store) => store.SetMeta(Key, "");

        public static string Hash(string rules) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rules)));
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
