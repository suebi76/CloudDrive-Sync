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
    bool Retryable);

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

    public async Task<SyncRunOutcome> RunAsync(SyncPairSettings pair, AccountSettings account, BisyncMode mode, string resyncMode, Action<JobProgress>? progress, CancellationToken cancellationToken, bool keepTrash = true)
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

        var rc = await _engine.EnsureRunningAsync(cancellationToken);
        var workDir = Path.Combine(folder, "bisync");
        Directory.CreateDirectory(workDir);
        var filtersFile = Path.Combine(folder, "filter.txt");
        WriteIfChanged(filtersFile, SyncFilters.Build(pair.Selection));

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
                    var unchanged = before is { Fetched: 0, ConflictCopies: 0 } && final.Transfers == 0 && deletes == 0 && Number(stats, "renames") == 0;
                    await quiet.RememberAsync(unchanged ? before!.Listing : null, cancellationToken);
                }
                catch (Exception e) when (e is CdException or IOException or UnauthorizedAccessException)
                {
                    Log.Warn("Sync", $"'{pair.Id}': server times not remembered: {(e as CdException)?.Detail ?? e.Message}");
                }
            }
            Log.Info("Sync", $"Run of '{pair.Id}' succeeded: {final.Transfers} transfers, {deletes} deletions, {conflicts.Count} conflict copies.");
            return new SyncRunOutcome(true, null, null, SyncDecision.None, final, deletes, conflicts, false);
        }

        var text = report + "\n" + result.Error;
        var code = ErrorCatalog.Classify(text);
        var decision = code switch
        {
            "CD-4502" or "CD-4509" => SyncDecision.Deletions,
            "CD-4504" => SyncDecision.Rebuild,
            "CD-4503" => SyncDecision.Folder,
            "CD-3012" => SyncDecision.SignIn,
            _ => SyncDecision.None,
        };
        var retryable = decision == SyncDecision.None;
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

    private static void WriteIfChanged(string file, string content)
    {
        // Rewriting the same content keeps bisync's checksum of the filter file - no needless rebuild.
        if (File.Exists(file) && File.ReadAllText(file) == content) return;
        File.WriteAllText(file, content);
    }

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
