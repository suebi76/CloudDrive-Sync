using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

/// <summary>What the first synchronisation will roughly do, shown before it starts.</summary>
/// <param name="UnreadableFolders">Cloud folders the server did not let be read; they are not counted.</param>
public sealed record SyncPreviewResult(long CloudFiles, long CloudBytes, long LocalFiles, long LocalBytes, long FreeBytes, int UnreadableFolders = 0)
{
    /// <summary>Enough free space for everything in the cloud selection, with 10 % reserve.</summary>
    public bool EnoughSpace => FreeBytes >= (long)(Math.Max(0, CloudBytes - LocalBytes) * 1.1);
}

/// <summary>
/// Looks ahead at the first synchronisation of a new folder pair: how much lies in the chosen part of the cloud and in
/// the local folder, and whether the drive has room for what comes from the cloud. The cloud is counted the way runs
/// list it (<see cref="CloudWalker"/>, with the synchronisation's own filter): fast, a refused folder left out, and
/// telling how far it got - a whole account can take minutes.
/// </summary>
public static class SyncPreview
{
    internal static async Task<SyncPreviewResult> CalculateAsync(RcClient rc, AppPaths paths, SyncPairSettings draft, IProgress<ListingProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.CacheDir);
        var filters = Path.Combine(paths.CacheDir, $"preview-{Guid.NewGuid():N}.txt");
        CloudWalk walk;
        try
        {
            await File.WriteAllTextAsync(filters, SyncFilters.Build(draft), cancellationToken);
            walk = await CloudWalker.WalkAsync(rc, BisyncCommand.CloudPath(draft), filters, withHashes: false, p => progress?.Report(p), "preview", cancellationToken);
        }
        finally
        {
            File.Delete(filters);
        }
        var cloudFiles = walk.Entries.LongCount(e => !e.IsDirectory && e.Path != SyncFilters.SentinelFile);
        var cloudBytes = walk.Entries.Where(e => !e.IsDirectory && e.Path != SyncFilters.SentinelFile).Sum(e => e.Size);

        long localFiles = 0, localBytes = 0;
        if (Directory.Exists(draft.LocalPath))
        {
            foreach (var file in FolderWalk.Files(draft.LocalPath, FileAttributes.ReparsePoint))
            {
                if (Path.GetRelativePath(draft.LocalPath, file.FullName).StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase)) continue;
                localFiles++;
                localBytes += file.Length;
            }
        }

        var root = Path.GetPathRoot(Path.GetFullPath(draft.LocalPath)) ?? "";
        long free = 0;
        try
        {
            if (!root.StartsWith(@"\\", StringComparison.Ordinal)) free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (IOException)
        {
        }
        return new SyncPreviewResult(cloudFiles, cloudBytes, localFiles, localBytes, free, walk.Unreadable.Count);
    }
}
