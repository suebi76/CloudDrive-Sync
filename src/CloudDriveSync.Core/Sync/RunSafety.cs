using System.Text.RegularExpressions;

namespace CloudDriveSync.Core.Sync;

/// <summary>
/// Keeps a synchronisation going through things that pass by themselves - a file another program holds open, a
/// network that drops for a moment - instead of stopping for a rebuild.
/// </summary>
internal static partial class RunSafety
{
    private const int MaxChecked = 5000;

    /// <summary>
    /// Changed files that cannot be read right now because another program holds them exclusively. rclone would stop
    /// the whole run on them (and then demand a rebuild); CloudDrive-Sync waits for them instead.
    /// </summary>
    public static IReadOnlyList<string> LockedFiles(string localPath, IEnumerable<string> relativePaths)
    {
        var locked = new List<string>();
        foreach (var relative in relativePaths.Take(MaxChecked))
        {
            var file = Path.Combine(localPath, relative.Replace('/', '\\'));
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException e) when ((e.HResult & 0xFFFF) is 32 or 33)
            {
                locked.Add(relative); // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
            }
            catch (UnauthorizedAccessException)
            {
                locked.Add(relative);
            }
        }
        return locked;
    }

    /// <summary>After a successful run: keeps bisync's listings as the last good state.</summary>
    public static void RememberGoodState(string workDir, string pairFolder)
    {
        var good = Path.Combine(pairFolder, "last-good");
        Directory.CreateDirectory(good);
        foreach (var old in Directory.EnumerateFiles(good)) File.Delete(old);
        foreach (var listing in Listings(workDir)) File.Copy(listing, Path.Combine(good, Path.GetFileName(listing)), overwrite: true);
    }

    /// <summary>
    /// After a run that broke off on something passing (a file in use, the network): puts the listings of the last good
    /// run back, so the next run carries on without a rebuild. This is what bisync's own recovery does after an
    /// interruption: what the broken run already copied looks "changed on both sides" and turns out to be equal.
    /// </summary>
    public static bool RestoreGoodState(string workDir, string pairFolder)
    {
        var good = Path.Combine(pairFolder, "last-good");
        var saved = Directory.Exists(good) ? Directory.GetFiles(good, "*.lst") : [];
        if (saved.Length != 2) return false;
        foreach (var file in Directory.EnumerateFiles(workDir, "*.lst*")) File.Delete(file);
        foreach (var file in saved) File.Copy(file, Path.Combine(workDir, Path.GetFileName(file)), overwrite: true);
        return true;
    }

    /// <summary>A run that bisync gave up on for good (it would demand a rebuild).</summary>
    public static bool IsCritical(string report) => CriticalPattern().IsMatch(report);

    /// <summary>The reason passes by itself: a file in use, the network, the server for a moment.</summary>
    public static bool IsPassing(string text) => PassingPattern().IsMatch(text);

    public static bool IsFileInUse(string text) => FileInUsePattern().IsMatch(text);

    private static IEnumerable<string> Listings(string workDir) =>
        Directory.Exists(workDir)
            ? Directory.EnumerateFiles(workDir, "*.lst").Where(f => f.EndsWith(".path1.lst", StringComparison.Ordinal) || f.EndsWith(".path2.lst", StringComparison.Ordinal))
            : [];

    [GeneratedRegex(@"(?i)Bisync critical error|cannot find prior Path1 or Path2 listings")]
    private static partial Regex CriticalPattern();

    [GeneratedRegex(@"(?i)being used by another process|cannot access the file|failed to open source object|i/o timeout|connection reset|connection refused|unexpected EOF|context deadline exceeded|TLS handshake timeout|no such host|network is unreachable|50[234] |429 Too Many Requests|cannot find prior Path1 or Path2 listings")]
    private static partial Regex PassingPattern();

    [GeneratedRegex(@"(?i)being used by another process|cannot access the file")]
    private static partial Regex FileInUsePattern();
}
