using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

/// <summary>What the first synchronisation will roughly do, shown before it starts.</summary>
public sealed record SyncPreviewResult(long CloudFiles, long CloudBytes, long LocalFiles, long LocalBytes, long FreeBytes)
{
    /// <summary>Enough free space for everything in the cloud selection, with 10 % reserve.</summary>
    public bool EnoughSpace => FreeBytes >= (long)(Math.Max(0, CloudBytes - LocalBytes) * 1.1);
}

/// <summary>
/// Looks ahead at the first synchronisation of a new folder pair: how much lies in the chosen part of the cloud and in
/// the local folder, and whether the drive has room for what comes from the cloud.
/// </summary>
public static class SyncPreview
{
    public static async Task<SyncPreviewResult> CalculateAsync(AccountService accounts, SyncPairSettings draft, CancellationToken cancellationToken = default)
    {
        long cloudFiles = 0, cloudBytes = 0;
        var basePath = draft.RemotePath.Trim('/');
        var parts = draft.Selection.Mode == SelectionMode.All
            ? [basePath]
            : SyncFilters.Normalise(draft.Selection.Include).Select(p => Join(basePath, p.TrimEnd('/'))).ToList();
        foreach (var part in parts)
        {
            var (bytes, count) = await accounts.GetSizeAsync(draft.AccountId, part, cancellationToken);
            cloudBytes += bytes;
            cloudFiles += count;
        }

        long localFiles = 0, localBytes = 0;
        if (Directory.Exists(draft.LocalPath))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in new DirectoryInfo(draft.LocalPath).EnumerateFiles("*", options))
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
        return new SyncPreviewResult(cloudFiles, cloudBytes, localFiles, localBytes, free);
    }

    private static string Join(string a, string b) => string.Join('/', new[] { a, b }.Where(p => p.Length > 0));
}
