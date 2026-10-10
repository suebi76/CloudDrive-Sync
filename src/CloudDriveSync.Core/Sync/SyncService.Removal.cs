using System.Runtime.Versioning;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.OnDemand;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

/// <summary>What stays on this PC when a synchronisation ends.</summary>
public enum KeepOnPc
{
    /// <summary>What lies on the PC stays; with files on demand the files whose data is on the PC (online-only ones leave the PC, they stay in the cloud).</summary>
    OnPc,

    /// <summary>Files on demand: every file comes onto the PC first and stays.</summary>
    Everything,

    /// <summary>
    /// Once everything is up, files that are in the cloud leave the PC (into Windows' recycle bin), with CloudDrive-Sync's
    /// own copies in the folder's trash; files only on the PC stay until the user lets them go, too (<see cref="SyncService.RecycleRestAsync"/>).
    /// </summary>
    Nothing,
}

/// <summary>What ending a synchronisation did on the PC.</summary>
/// <param name="Recycled">Files moved into Windows' recycle bin.</param>
/// <param name="Stayed">Files that stayed although nothing was to stay - only on the PC, changed meanwhile, or held by a program.</param>
/// <param name="StillHeld">
/// Files on demand: a program held placeholders, so the folder stays registered with Windows until it is free - then
/// CloudDrive-Sync clears it by itself (also after a restart).
/// </param>
public sealed record EndResult(int Recycled, IReadOnlyList<string> Stayed, bool StillHeld = false);

public sealed partial class SyncService
{
    /// <summary>Where files go that are to leave the PC: Windows' recycle bin; tests delete them instead.</summary>
    internal Func<IReadOnlyList<string>, int> Recycle { get; set; } = RecycleBin.Move;

    /// <summary>
    /// Before a synchronisation ends: a last run, so what changed on the PC is in the cloud; with
    /// <see cref="KeepOnPc.Everything"/> every file comes onto the PC (when the drive has room); with
    /// <see cref="KeepOnPc.Nothing"/> the run must succeed, and the files that are provably the cloud's version are
    /// named - only they will leave. Should anything fail here, the synchronisation stays as it was.
    /// </summary>
    private async Task<IReadOnlyList<string>> PrepareEndAsync(SyncPairSettings pair, KeepOnPc keep, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var account = _accounts.Find(pair.AccountId) ?? throw new CdException("CD-9000", $"unknown account '{pair.AccountId}'");
        var keepTrash = _settings.Current.Preferences.TrashDays > 0;
        progress?.Report("Gleicht ein letztes Mal ab …");
        var onDemand = pair.Mode == SyncMode.OnDemand && OnDemandSupported;
        var outcome = onDemand
            ? await RunOnDemandAsync(pair, account, BisyncMode.Normal, _ => { }, keepTrash, cancellationToken, activity: text => progress?.Report(text))
            : await _runner.RunAsync(pair, account, BisyncMode.Normal, "newer", null, cancellationToken, keepTrash);
        if (keep == KeepOnPc.Nothing && !outcome.Success) throw new CdException("CD-4608", $"{outcome.ErrorCode}: {outcome.ErrorDetail}");

        if (keep == KeepOnPc.Everything && pair.Mode == SyncMode.OnDemand && OnDemandSupported)
        {
            await FetchEverythingAsync(pair, progress, cancellationToken);
            return [];
        }
        if (keep != KeepOnPc.Nothing) return [];
        return onDemand
            ? await Task.Run(() => OnDemandSupported ? InCloudOnDemand(pair.LocalPath) : [], cancellationToken)
            : await Task.Run(() => InCloudClassic(pair), cancellationToken);
    }

    /// <summary>Every online-only file onto the PC - when the drive has room for it, with a reserve.</summary>
    [SupportedOSPlatform("windows10.0.17763")]
    private static async Task FetchEverythingAsync(SyncPairSettings pair, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var missing = await Task.Run(() => OnlineOnlyFiles(pair.LocalPath), cancellationToken);
        var bytes = missing.Sum(f => f.Missing);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(pair.LocalPath))!).AvailableFreeSpace;
        if (bytes + Math.Max(1L << 30, bytes / 20) > free) throw new CdException("CD-4606", $"{bytes} bytes to download, {free} bytes free");
        for (var i = 0; i < missing.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Lädt alle Dateien auf diesen PC: {i + 1} von {missing.Count} …");
            await Task.Run(() => Placeholders.Hydrate(missing[i].Path), cancellationToken);
        }
        if (await Task.Run(() => OnlineOnlyFiles(pair.LocalPath), cancellationToken) is { Count: > 0 } left)
            throw new CdException("CD-4604", $"{left.Count} file(s) did not arrive, e.g. {Path.GetFileName(left[0].Path)}");
    }

    /// <summary>Files on demand: the placeholders in sync - they are the cloud's version; anything else stays.</summary>
    [SupportedOSPlatform("windows10.0.17763")]
    private static List<string> InCloudOnDemand(string root)
    {
        var files = new List<string>();
        foreach (var file in Files(root))
        {
            try
            {
                if (Placeholders.Read(file) is { InSync: true }) files.Add(file);
            }
            catch (IOException)
            {
                // Unreadable: it stays.
            }
        }
        return files;
    }

    /// <summary>
    /// Classic: the files bisync's run just now left in step that did not change since - size and time as it noted them.
    /// Files outside its record (left on the PC on purpose, excluded) stay.
    /// </summary>
    private List<string> InCloudClassic(SyncPairSettings pair)
    {
        var record = PcListingTimes.ReadRecord(Path.Combine(_paths.SyncPairDir(pair.Id), "bisync"));
        var files = new List<string>();
        foreach (var (path, noted) in record.Pc)
        {
            if (path == SyncFilters.SentinelFile) continue;
            var file = new FileInfo(Path.Combine(pair.LocalPath, NameEncoding.ToLocalPath(path)));
            if (file.Exists && file.Length == noted.Size && Math.Abs((file.LastWriteTimeUtc - noted.Time).Ticks) <= TimeSpan.TicksPerSecond) files.Add(file.FullName);
        }
        return files;
    }

    /// <summary>
    /// After the synchronisation ended with <see cref="KeepOnPc.Nothing"/>: the named files into the recycle bin, and the
    /// copies in the folder's trash (<see cref="SyncFilters.TrashFolder"/>) - they were CloudDrive-Sync's own, the recycle
    /// bin keeps them as well. Then empty folders go - the folder, too, when nothing stayed in it.
    /// </summary>
    private EndResult RecycleAndTidy(string root, IReadOnlyList<string> leave, IProgress<string>? progress)
    {
        progress?.Report("Legt die Dateien in den Papierkorb …");
        var trash = SyncTrash.FolderOf(root);
        var existing = leave.Where(File.Exists).Concat(Directory.Exists(trash) ? AllFiles(trash) : []).ToList();
        var recycled = Recycle(existing);
        return Tidy(root, recycled);
    }

    /// <summary>
    /// When a synchronisation ended with <see cref="KeepOnPc.Nothing"/> and files stayed (only on this PC, or changed
    /// just then) and the user lets them go, too: every file left in the folder into the recycle bin, then the folder
    /// goes. Never a folder another synchronisation uses, or one inside or around it.
    /// </summary>
    public Task<EndResult> RecycleRestAsync(string folder, IProgress<string>? progress = null) => Task.Run(() =>
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        foreach (var other in _settings.Current.Syncs.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.LocalPath))))
        {
            if (string.Equals(other, root, StringComparison.OrdinalIgnoreCase) || other.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new CdException("CD-9000", "the folder belongs to a synchronisation that runs");
        }
        if (!Directory.Exists(root)) return new EndResult(0, []);
        progress?.Report("Legt die übrigen Dateien in den Papierkorb …");
        return Tidy(root, Recycle(AllFiles(root).ToList()));
    });

    /// <summary>
    /// Empty folders away - the folder itself, too, when nothing is left in it. What is left is named, with folders Windows
    /// refuses to open (what is in them is unknown) and links (they stay as they are; what they point to was never touched).
    /// </summary>
    private static EndResult Tidy(string root, int recycled)
    {
        var folders = Directory.Exists(root) ? FolderWalk.Entries(root, 0).OfType<DirectoryInfo>().OrderByDescending(d => d.FullName.Length).ToList() : [];
        var links = new List<string>();
        foreach (var directory in folders)
        {
            try
            {
                if (FolderWalk.IsLink(directory)) links.Add(directory.FullName);
                else if (!Directory.EnumerateFileSystemEntries(directory.FullName).Any()) Directory.Delete(directory.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A folder that stays is no harm.
            }
        }
        var refused = new List<string>();
        var stayed = Directory.Exists(root) ? AllFiles(root, refused).Select(f => Path.GetRelativePath(root, f)).ToList() : [];
        stayed.AddRange(refused.Concat(links).Distinct(StringComparer.OrdinalIgnoreCase).Select(f => Path.GetRelativePath(root, f) + Path.DirectorySeparatorChar));
        try
        {
            if (stayed.Count == 0 && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An empty folder Explorer has open right now stays; it can be deleted like any other.
            Log.Warn("Sync", $"Empty folder not removed: {e.Message}");
        }
        Log.Info("Sync", $"{recycled} file(s) moved into the recycle bin; {stayed.Count} stayed on the PC.");
        return new EndResult(recycled, stayed);
    }

    /// <summary>Every file below <paramref name="root"/>; a folder Windows refuses is passed over (and named in <paramref name="refused"/>).</summary>
    private static IEnumerable<string> AllFiles(string root, List<string>? refused = null) =>
        FolderWalk.Files(root, 0, refused).Select(f => f.FullName);

    private static IEnumerable<string> Files(string root) =>
        AllFiles(root).Where(f => !Path.GetRelativePath(root, f).StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase));
}
