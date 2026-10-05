using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

/// <summary>Result of one run.</summary>
public sealed record SyncRunOutcome(
    bool Success,
    string? ErrorCode,
    string? ErrorDetail,
    SyncDecision Decision,
    JobProgress Final,
    long Deletes,
    IReadOnlyList<string> Conflicts,
    bool Retryable,
    IReadOnlyList<string>? LocalOnlyAdded = null,
    IReadOnlyList<FileChange>? Changes = null);

/// <summary>
/// Carries out one run of a synchronisation: checks first (folder and sentinel file there, engine running), then
/// rclone's bisync with CloudDrive-Sync's safety settings, and finally reads the result - including bisync's own report,
/// which tells too many deletions, a missing sentinel file or a needed rebuild apart.
/// </summary>
public sealed partial class SyncRunner
{
    private readonly AppPaths _paths;
    private readonly RcloneEngine _engine;

    public SyncRunner(AppPaths paths, RcloneEngine engine)
    {
        _paths = paths;
        _engine = engine;
    }

    public Task<SyncRunOutcome> RunAsync(SyncPairSettings pair, AccountSettings account, BisyncMode mode, string resyncMode, Action<JobProgress>? progress, CancellationToken cancellationToken, bool keepTrash = true) =>
        RunCoreAsync(pair, account, mode, resyncMode, progress, cancellationToken, keepTrash, repeatAllowed: true);

    private async Task<SyncRunOutcome> RunCoreAsync(SyncPairSettings pair, AccountSettings account, BisyncMode mode, string resyncMode, Action<JobProgress>? progress, CancellationToken cancellationToken, bool keepTrash, bool repeatAllowed)
    {
        // Without the folder (e.g. its drive is not connected) nothing runs - an empty folder is never taken as "all deleted".
        if (!Directory.Exists(pair.LocalPath))
            return Failed("CD-4501", pair.LocalPath, SyncDecision.Folder, retryable: true);
        if (!File.Exists(Path.Combine(pair.LocalPath, SyncFilters.SentinelFile)))
            return Failed("CD-4503", Path.Combine(pair.LocalPath, SyncFilters.SentinelFile), SyncDecision.Folder, retryable: false);

        var folder = _paths.SyncPairDir(pair.Id);
        // Many files gone from the PC: stop before anything is deleted in the cloud (rclone counts folders, too).
        if (mode == BisyncMode.Normal && DeleteGuard.TooManyMissing(folder, pair) is { } missing)
        {
            Log.Warn("Sync", $"Run of '{pair.Id}' stopped: {missing.Missing} of {missing.Known} files are gone from the PC.");
            return Failed("CD-4502", $"too many deletes (>{pair.MaxDeletePercent}%, {missing.Missing} of {missing.Known}) on Path2 (CloudDrive-Sync)", SyncDecision.Deletions, retryable: false);
        }

        // A changed file another program holds exclusively would make rclone give the whole run up; wait for it instead.
        if (mode != BisyncMode.Resync && RunSafety.LockedFiles(pair.LocalPath, DeleteGuard.ChangedSinceLastRun(folder, pair)) is { Count: > 0 } locked)
        {
            Log.Info("Sync", $"Run of '{pair.Id}' waits: {locked.Count} changed file(s) are open in another program.");
            return Failed("CD-4510", string.Join(", ", locked.Take(5)) + (locked.Count > 5 ? $" (+{locked.Count - 5})" : ""), SyncDecision.None, retryable: true);
        }

        var rc = await _engine.EnsureRunningAsync(cancellationToken);
        var workDir = Path.Combine(folder, "bisync");
        Directory.CreateDirectory(workDir);
        var filtersFile = Path.Combine(folder, "filter.txt");
        SyncFilters.Write(folder, filtersFile, pair);
        if (!pair.CloudCheckFile && await CloudFolderCheck.ProblemAsync(rc, pair, folder, cancellationToken) is { } problem)
        {
            Log.Warn("Sync", $"Run of '{pair.Id}' stopped: {problem}.");
            return Failed("CD-4512", problem, SyncDecision.Folder, retryable: false);
        }
        if (mode != BisyncMode.Resync)
        {
            try
            {
                await CaseRenames.CarryPcRenamesAsync(rc, pair, folder, cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Sync", $"'{pair.Id}': renames of upper and lower case not checked: {e.Message}");
            }
        }

        // Servers without modification times of their own: CloudDrive-Sync catches same-size changes there itself.
        var quiet = account.Kind == WebDavKind.Nextcloud ? null : new QuietServerChanges(rc, pair, folder, filtersFile);
        QuietServerChanges.Result? before = null;
        if (quiet is not null && mode != BisyncMode.Resync)
        {
            try
            {
                var trash = keepTrash ? Path.Combine(pair.LocalPath, SyncFilters.TrashFolder, SyncTrash.NewStamp()) : null;
                before = await quiet.FetchAsync(trash, cancellationToken);
            }
            catch (Exception e) when (e is CdException or IOException or UnauthorizedAccessException)
            {
                Log.Warn("Sync", $"'{pair.Id}': check for changes on the server failed: {(e as CdException)?.Detail ?? e.Message}");
            }
        }

        var group = $"sync/{pair.Id}";
        await TryCallAsync(rc, "core/stats-reset", new JsonObject { ["group"] = group });
        var body = BisyncCommand.Build(pair, account, workDir, filtersFile, mode, resyncMode, keepTrash: keepTrash);
        Log.Info("Sync", $"Run of '{pair.Id}' started ({mode}).");
        var result = await rc.RunJobAsync("sync/bisync", body, group, progress, cancellationToken);

        var stats = await TryCallAsync(rc, "core/stats", new JsonObject { ["group"] = group }) ?? new JsonObject();
        await TryCallAsync(rc, "core/stats-delete", new JsonObject { ["group"] = group });
        var final = RcClient.ParseProgress(stats);
        var deletes = Number(stats, "deletes");
        var conflicts = FindConflicts(pair.LocalPath);
        var report = StripColours(result.Output["output"]?.GetValue<string>() ?? "");
        WriteReport(folder, report);

        if (result.Success)
        {
            try
            {
                DeleteGuard.Remember(folder, pair);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Sync", $"'{pair.Id}': local file list not remembered: {e.Message}");
            }
            if (quiet is not null)
            {
                try
                {
                    // The listing from before the run still holds when the run changed nothing in the cloud.
                    var unchanged = before is { ChangedAnything: false } && final.Transfers == 0 && deletes == 0 && Number(stats, "renames") == 0;
                    await quiet.RememberAsync(unchanged ? before!.Listing : null, cancellationToken);
                }
                catch (Exception e) when (e is CdException or IOException or UnauthorizedAccessException)
                {
                    Log.Warn("Sync", $"'{pair.Id}': server times not remembered: {(e as CdException)?.Detail ?? e.Message}");
                }
            }
            try
            {
                RunSafety.RememberGoodState(workDir, folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Sync", $"'{pair.Id}': last good state not kept: {e.Message}");
            }
            // Same-size changes CloudDrive-Sync carried over itself count as transfers, too.
            if (before is { ChangedAnything: true }) final = final with { Transfers = final.Transfers + before.Fetched + before.Uploaded + before.ConflictCopies };
            Log.Info("Sync", $"Run of '{pair.Id}' succeeded: {final.Transfers} transfers, {deletes} deletions, {conflicts.Count} conflict copies.");
            // Which files went where - for the activity list.
            List<FileChange> changes = [.. before?.Changes ?? [], .. RunChanges.FromReport(report, BisyncCommand.CloudPath(pair), pair.LocalPath)];
            return new SyncRunOutcome(true, null, null, SyncDecision.None, final, deletes, conflicts, false, Changes: changes);
        }

        var text = report + "\n" + result.Error;
        // Files the server did not take (a folder to read only) stay on the PC and out of the synchronisation; the run
        // is repeated without them - nothing is lost and no rebuild is needed.
        if (repeatAllowed && RunSafety.RefusedUploads(report, BisyncCommand.CloudPath(pair)) is { Count: > 0 } refused
            && (mode == BisyncMode.Resync || RunSafety.RestoreGoodState(workDir, folder)))
        {
            var kept = Copy(pair);
            kept.LocalOnly = kept.LocalOnly.Union(refused, StringComparer.OrdinalIgnoreCase).ToList();
            Log.Info("Sync", $"'{pair.Id}': {refused.Count} file(s) the server did not take stay on the PC; the run is repeated without them.");
            var again = await RunCoreAsync(kept, account, mode, resyncMode, progress, cancellationToken, keepTrash, repeatAllowed: false);
            return again with
            {
                LocalOnlyAdded = refused,
                Changes = [.. again.Changes ?? [], .. refused.Select(path => new FileChange(ChangeKind.KeptOnPc, path))],
            };
        }
        // Names that differ only in upper and lower case: the PC takes the server's spelling and the run is repeated once.
        if (repeatAllowed && mode != BisyncMode.Resync && CaseRenames.OutOfSync(report) is { Count: > 0 } outOfSync)
        {
            var aligned = false;
            try
            {
                aligned = await CaseRenames.PcFollowsServerAsync(rc, pair, outOfSync, cancellationToken);
            }
            catch (Exception e) when (e is CdException or IOException or UnauthorizedAccessException)
            {
                Log.Warn("Sync", $"'{pair.Id}': spelling not aligned: {(e as CdException)?.Detail ?? e.Message}");
            }
            if (aligned && RunSafety.RestoreGoodState(workDir, folder))
            {
                Log.Info("Sync", $"Run of '{pair.Id}' is repeated after aligning upper and lower case.");
                return await RunCoreAsync(pair, account, mode, resyncMode, progress, cancellationToken, keepTrash, repeatAllowed: false);
            }
        }
        // Broke off on something that passes (a file in use, the network): back to the last good state, try again soon -
        // no rebuild. A rebuild that broke off is simply repeated.
        if (RunSafety.IsCritical(text) && RunSafety.IsPassing(text) && (mode == BisyncMode.Resync || RunSafety.RestoreGoodState(workDir, folder)))
        {
            var passing = RunSafety.IsFileInUse(text) ? "CD-4510" : "CD-5001";
            var why = Summarise(report, result.Error);
            Log.Warn("Sync", $"Run of '{pair.Id}' broke off ({passing}); the last good state is kept and the run follows soon: {why}");
            return new SyncRunOutcome(false, passing, why, SyncDecision.None, final, deletes, conflicts, true);
        }
        var code = ErrorCatalog.Classify(text);
        var decision = code switch
        {
            "CD-4502" or "CD-4509" => SyncDecision.Deletions,
            "CD-4504" => SyncDecision.Rebuild,
            "CD-4503" => SyncDecision.Folder,
            "CD-3012" => SyncDecision.SignIn,
            _ => SyncDecision.None,
        };
        // Changes the server does not take (a folder to read only) stay on the PC; trying again every minute would
        // not help - the next regular run tries again.
        var retryable = decision == SyncDecision.None && code != "CD-4511";
        var detail = Summarise(report, result.Error);
        Log.Warn("Sync", $"Run of '{pair.Id}' failed: {code} {detail}");
        return new SyncRunOutcome(false, code, detail, decision, final, deletes, conflicts, retryable);
    }

    /// <summary>Conflict copies in the local folder ("Bericht.Konflikt-PC1.docx"), relative to the folder.</summary>
    public static IReadOnlyList<string> FindConflicts(string localPath)
    {
        if (!Directory.Exists(localPath)) return [];
        var found = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(localPath, "*Konflikt-*", options))
        {
            var relative = Path.GetRelativePath(localPath, file);
            if (relative.StartsWith(SyncFilters.TrashFolder, StringComparison.OrdinalIgnoreCase)) continue;
            if (ConflictPattern().IsMatch(Path.GetFileName(file))) found.Add(relative);
        }
        return found.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static long Number(JsonObject stats, string name) =>
        stats[name] is JsonValue value && value.TryGetValue<double>(out var number) ? (long)number : 0;

    private static SyncRunOutcome Failed(string code, string detail, SyncDecision decision, bool retryable) =>
        new(false, code, detail, decision, JobProgress.None, 0, [], retryable);

    private static SyncPairSettings Copy(SyncPairSettings pair) =>
        JsonSerializer.Deserialize<SyncPairSettings>(JsonSerializer.Serialize(pair, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;

    private static void WriteReport(string folder, string report)
    {
        try
        {
            File.WriteAllText(Path.Combine(folder, "last-run.txt"), Log.Redact(report));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The lines of bisync's report that explain an error, shortened.</summary>
    private static string Summarise(string report, string error)
    {
        var lines = report.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("ERROR", StringComparison.Ordinal) || l.Contains("critical", StringComparison.OrdinalIgnoreCase) || l.Contains("abort", StringComparison.OrdinalIgnoreCase))
            .TakeLast(4)
            .ToList();
        var text = lines.Count > 0 ? string.Join(" | ", lines) : error;
        text = Log.Redact(text);
        return text.Length > 600 ? text[..600] + " …" : text;
    }

    private static async Task<JsonObject?> TryCallAsync(RcClient rc, string command, JsonObject body)
    {
        try
        {
            return await rc.CallAsync(command, body, TimeSpan.FromSeconds(15));
        }
        catch (CdException)
        {
            return null;
        }
    }

    private static string StripColours(string text) => AnsiPattern().Replace(text, "");

    [GeneratedRegex(@"\x1B\[[0-9;]*m")]
    private static partial Regex AnsiPattern();

    [GeneratedRegex(@"\.Konflikt-(Cloud|PC)\d+(\.|$)")]
    private static partial Regex ConflictPattern();
}
