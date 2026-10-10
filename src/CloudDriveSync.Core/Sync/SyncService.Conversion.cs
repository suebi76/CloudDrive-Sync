using System.Runtime.Versioning;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

public sealed partial class SyncService
{
    /// <summary>
    /// Switches a classic synchronisation to files on demand - without any transfer. A normal run brings both sides in
    /// step first. Then the folder is registered with Windows and its files become placeholders whose data stays on the
    /// PC. Only files neither side changed since that run count as the same; any other file is kept in both versions.
    /// Should the switch break off, the registration ends again - every file stays as a normal file - and the classic
    /// synchronisation goes on as before.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763")]
    public async Task ConvertToOnDemandAsync(string id, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await ExclusiveAsync(id, async () =>
        {
            var pair = FindPair(id) ?? throw new CdException("CD-9000", $"unknown synchronisation '{id}'");
            if (pair.Mode != SyncMode.Classic) return true;
            if (OnDemandProblem(pair.LocalPath) is { } problem) throw new CdException("CD-4601", problem);
            var account = _accounts.Find(pair.AccountId) ?? throw new CdException("CD-9000", $"unknown account '{pair.AccountId}'");
            var keepTrash = _settings.Current.Preferences.TrashDays > 0;

            progress?.Report("Gleicht beide Seiten ab …");
            var classic = await _runner.RunAsync(pair, account, BisyncMode.Normal, "newer", null, cancellationToken, keepTrash);
            if (!classic.Success) throw new CdException("CD-4607", $"{classic.ErrorCode}: {classic.ErrorDetail}");
            var before = PcListingTimes.ReadRecord(Path.Combine(_paths.SyncPairDir(id), "bisync"));

            progress?.Report("Stellt auf „Dateien bei Bedarf“ um …");
            SetMode(id, SyncMode.OnDemand);
            _settings.Update(s =>
            {
                if (s.Syncs.FirstOrDefault(p => p.Id == id) is { } converted) converted.RegistrationKey = SyncRoots.NewKey(id);
            });
            // Windows makes its own entry in Explorer for the registered folder.
            RemoveExplorerEntry(id);
            SyncRunOutcome outcome;
            try
            {
                outcome = await RunOnDemandAsync(FindPair(id)!, account, BisyncMode.Resync, _ => { }, keepTrash, cancellationToken, before);
            }
            catch
            {
                BackToClassic(id);
                throw;
            }
            if (!outcome.Success)
            {
                BackToClassic(id);
                throw new CdException("CD-4607", $"{outcome.ErrorCode}: {outcome.ErrorDetail}");
            }
            // What only bisync needed goes; switching back starts with a first run of its own.
            foreach (var folder in new[] { "bisync", "last-good" }) TryDeleteFolder(Path.Combine(_paths.SyncPairDir(id), folder));
            foreach (var file in new[] { "local-files.txt", "server-times.json" }) TryDelete(Path.Combine(_paths.SyncPairDir(id), file));
            Log.Info("Sync", $"'{id}' switched to files on demand: {outcome.Final.Transfers} transfers, {outcome.Conflicts.Count} conflict copies.");
            return true;
        }, cancellationToken);
        Worker(id)?.SettingsChanged();
    }

    /// <summary>
    /// Switches a synchronisation with files on demand to "all files on this PC". Changes on the PC go up first; then
    /// every file comes onto the PC - when the drive has room for it -, the registration with Windows ends (the files
    /// stay as normal files), and the first classic run merges both sides without deleting anything. Should the
    /// download break off, the synchronisation stays with files on demand; what arrived stays on the PC.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763")]
    public async Task ConvertToClassicAsync(string id, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await ExclusiveAsync(id, async () =>
        {
            var pair = FindPair(id) ?? throw new CdException("CD-9000", $"unknown synchronisation '{id}'");
            if (pair.Mode != SyncMode.OnDemand) return true;
            var account = _accounts.Find(pair.AccountId) ?? throw new CdException("CD-9000", $"unknown account '{pair.AccountId}'");
            var keepTrash = _settings.Current.Preferences.TrashDays > 0;

            progress?.Report("Gleicht beide Seiten ab …");
            var outcome = await RunOnDemandAsync(pair, account, BisyncMode.Normal, _ => { }, keepTrash, cancellationToken);
            if (!outcome.Success) throw new CdException("CD-4607", $"{outcome.ErrorCode}: {outcome.ErrorDetail}");

            var missing = await Task.Run(() => OnlineOnlyFiles(pair.LocalPath), cancellationToken);
            var bytes = missing.Sum(f => f.Missing);
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(pair.LocalPath))!).AvailableFreeSpace;
            // Room for everything, and a reserve: at least 1 GB or a twentieth of the download.
            if (bytes + Math.Max(1L << 30, bytes / 20) > free) throw new CdException("CD-4606", $"{bytes} bytes to download, {free} bytes free");
            for (var i = 0; i < missing.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Lädt alle Dateien auf diesen PC: {i + 1} von {missing.Count} …");
                await Task.Run(() => Placeholders.Hydrate(missing[i].Path), cancellationToken);
            }
            if (await Task.Run(() => OnlineOnlyFiles(pair.LocalPath), cancellationToken) is { Count: > 0 } left)
                throw new CdException("CD-4604", $"{left.Count} file(s) did not arrive, e.g. {Path.GetFileName(left[0].Path)}");

            progress?.Report("Stellt auf „Alle Dateien auf diesem PC“ um …");
            _ = EndOnDemand(id);
            DeleteOnDemandState(id);
            SetMode(id, SyncMode.Classic);
            UpdateExplorerEntry(id);
            Log.Info("Sync", $"'{id}' switched to all files on this PC: {missing.Count} file(s) fetched.");
            // The first classic run merges both sides; nothing is deleted.
            RebuildNext(id);
            return true;
        }, cancellationToken);
        Worker(id)?.SettingsChanged();
    }

    /// <summary>Waits for a running synchronisation of the folder and keeps the next one waiting meanwhile.</summary>
    private Task<T> ExclusiveAsync<T>(string id, Func<Task<T>> action, CancellationToken cancellationToken) =>
        Worker(id) is { } worker ? worker.ExclusiveAsync(action, cancellationToken) : action();

    /// <summary>The next classic run merges both sides - also after a restart.</summary>
    private void RebuildNext(string id)
    {
        if (Worker(id) is { } worker)
        {
            worker.RebuildNext();
            return;
        }
        var file = Path.Combine(_paths.SyncPairDir(id), "state.json");
        var saved = PersistedSyncState.Load(file);
        saved.ResyncPending = true;
        saved.Save(file);
    }

    /// <summary>A switch to files on demand broke off: registration and state go, every file stays as a normal file.</summary>
    [SupportedOSPlatform("windows10.0.17763")]
    private void BackToClassic(string id)
    {
        _ = EndOnDemand(id);
        DeleteOnDemandState(id);
        SetMode(id, SyncMode.Classic);
        UpdateExplorerEntry(id);
        Log.Warn("Sync", $"Switching '{id}' to files on demand broke off; it stays a classic synchronisation.");
    }

    private void SetMode(string id, SyncMode mode) => _settings.Update(s =>
    {
        if (s.Syncs.FirstOrDefault(p => p.Id == id) is { } pair) pair.Mode = mode;
    });

    private void DeleteOnDemandState(string id)
    {
        foreach (var ending in new[] { "", "-wal", "-shm" }) TryDelete(Path.Combine(_paths.SyncPairDir(id), "items.db" + ending));
    }

    /// <summary>Placeholders whose data is not (completely) on the PC, with the bytes missing.</summary>
    [SupportedOSPlatform("windows10.0.17763")]
    private static List<(string Path, long Missing)> OnlineOnlyFiles(string root)
    {
        var result = new List<(string, long)>();
        foreach (var top in Directory.EnumerateFileSystemEntries(root))
        {
            if (Path.GetFileName(top).StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase)) continue;
            var files = Directory.Exists(top) ? Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories) : [top];
            foreach (var file in files)
                if (Placeholders.Read(file) is { IsFullyOnDisk: false } info) result.Add((file, info.Size - info.OnDiskSize));
        }
        return result;
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Sync", $"Folder not removed: {e.Message}");
        }
    }
}
