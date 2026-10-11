using System.Text.RegularExpressions;
using CloudDriveSync.Core.Diagnostics;

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

    /// <summary>The storage in the cloud is full ("507 Insufficient Storage", a quota reached): passes once there is room.</summary>
    public static bool IsStorageFull(string text) => StorageFullPattern().IsMatch(text);

    /// <summary>Someone has a file open in the cloud ("423 Locked", e.g. Nextcloud Office): passes once it is closed.</summary>
    public static bool IsLockedInCloud(string text) => LockedInCloudPattern().IsMatch(text);

    /// <summary>
    /// The code of trouble that passes, by what passes - a run report says "must run --resync" in any case, which says
    /// nothing about it: a file open on the PC, the cloud's storage full, a file open in the cloud, the PC's drive full,
    /// or (anything else) the network.
    /// </summary>
    public static string PassingCode(string text) =>
        IsFileInUse(text) ? "CD-4510"
        : IsStorageFull(text) ? "CD-3006"
        : IsLockedInCloud(text) ? "CD-4514"
        : DiskFullPattern().IsMatch(text) ? "CD-4515"
        : "CD-5001";

    /// <summary>
    /// Files bisync could not upload because the server did not take them (a folder to read only), relative to the
    /// synchronised folder. Only uploads count - bisync's queue of copies to path 1 names them.
    /// </summary>
    public static IReadOnlyList<string> RefusedUploads(string report, string cloudPath)
    {
        var prefix = cloudPath.EndsWith(':') ? cloudPath : cloudPath + "/";
        var uploads = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match queued in UploadQueuePattern().Matches(report))
        {
            var target = queued.Groups[1].Value;
            if (target.StartsWith(prefix, StringComparison.Ordinal)) uploads.Add(target[prefix.Length..]);
        }
        var refused = new List<string>();
        foreach (Match failed in FailedCopyPattern().Matches(report))
        {
            var path = failed.Groups["path"].Value;
            if (uploads.Contains(path) && IsRefusal(failed.Groups["reason"].Value) && !refused.Contains(path)) refused.Add(path);
        }
        return refused;
    }

    private static IEnumerable<string> Listings(string workDir) =>
        Directory.Exists(workDir)
            ? Directory.EnumerateFiles(workDir, "*.lst").Where(f => f.EndsWith(".path1.lst", StringComparison.Ordinal) || f.EndsWith(".path2.lst", StringComparison.Ordinal))
            : [];

    [GeneratedRegex(@"(?i)Bisync critical error|cannot find prior Path1 or Path2 listings")]
    private static partial Regex CriticalPattern();

    [GeneratedRegex(@"(?i)being used by another process|cannot access the file|failed to open source object|i/o timeout|connection reset|connection refused|unexpected EOF|context deadline exceeded|TLS handshake timeout|no such host|network is unreachable|50[234] |429 Too Many Requests|423 Locked|507 Insufficient Storage|insufficient storage|quota (?:limit )?(?:reached|exceeded)|not enough space on the disk|cannot find prior Path1 or Path2 listings")]
    private static partial Regex PassingPattern();

    [GeneratedRegex(@"(?i)being used by another process|cannot access the file")]
    private static partial Regex FileInUsePattern();

    [GeneratedRegex(@"(?i)507 Insufficient Storage|insufficient storage|quota (?:limit )?(?:reached|exceeded)")]
    private static partial Regex StorageFullPattern();

    [GeneratedRegex(@"(?im):\s*423 Locked\s*$|Exception\\FileLocked")]
    private static partial Regex LockedInCloudPattern();

    // rclone reports Windows' errors in English (Go asks Windows for English texts), whatever the language of Windows.
    [GeneratedRegex(@"(?i)not enough space on the disk")]
    private static partial Regex DiskFullPattern();

    [GeneratedRegex(@"(?m)Queue copy to Path1\s+-\s+(.+?)\s*$")]
    private static partial Regex UploadQueuePattern();

    /// <summary>
    /// Removes bisync's lock files that an earlier engine left behind - a run broken off by a restart of Windows or a crash.
    /// bisync would otherwise refuse every run for up to 30 minutes (CD-4508, "prior lock file found"). A lock names the
    /// engine process that set it; only CloudDrive-Sync's engine runs bisync in this folder, so a lock of another process
    /// is a left-over. A lock of the running engine (a run still going) and one that cannot be read stay.
    /// </summary>
    internal static void RemoveStaleLocks(string workDir, int? engineProcess)
    {
        if (engineProcess is null || !Directory.Exists(workDir)) return;
        foreach (var file in Directory.EnumerateFiles(workDir, "*.lck"))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                if (!document.RootElement.TryGetProperty("PID", out var pid) || pid.ValueKind != System.Text.Json.JsonValueKind.String
                    || !int.TryParse(pid.GetString(), out var process) || process == engineProcess) continue;
                File.Delete(file);
                Log.Info("Sync", $"Lock of an earlier engine (process {process}) removed: the run it belonged to was broken off.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // Unreadable or in use: bisync decides (an expired lock it takes over itself).
            }
        }
    }

    /// <summary>
    /// Whether the server refused a file for good - a folder to read only, a type of file it does not take - rather than a
    /// passing trouble. The reason may be a whole error page over several lines (Nextcloud, a filter in front of it): then
    /// the status rclone puts at its end counts, not what the page says. "423 Locked" is no refusal: someone has the file
    /// open (e.g. in Nextcloud Office), and it passes - before, such a file stayed on the PC for good.
    /// </summary>
    internal static bool IsRefusal(string reason) =>
        RefusalPattern().IsMatch(reason.Split('\n', 2)[0]) || TrailingRefusalPattern().IsMatch(reason) || RefusedNamePattern().IsMatch(reason);

    // The reason runs to the next line of the report: a server's error page spans several.
    [GeneratedRegex(@"(?ms)ERROR : (?<path>[^\r\n]+?): Failed to copy: (?<reason>.*?)\s*(?=^\d{4}/\d{2}/\d{2} |\z)")]
    private static partial Regex FailedCopyPattern();

    [GeneratedRegex(@"(?i):\s*(?:403 Forbidden|404 Not Found|405 Method Not Allowed|409 Conflict|415 Unsupported Media Type)\s*$")]
    private static partial Regex TrailingRefusalPattern();

    // Nextcloud answers a name it does not take (a reserved one, an ending like ".part") to the upload itself with 400 - not
    // to be mixed up with a 400 to a question after a finished upload ("couldn't calculate checksum after upload").
    [GeneratedRegex(@"(?is)^(?:operations/copyfile:\s*)?unchunked simple update failed:.*:\s*400 Bad Request\s*$")]
    private static partial Regex RefusedNamePattern();

    // IServ: "Failed to write file … 500", Nextcloud: 403, others 404, 405 or 409.
    [GeneratedRegex(@"(?i)Failed to write file|403 Forbidden|404 Not Found|405 Method Not Allowed|409 Conflict|read[- ]only|permission denied")]
    private static partial Regex RefusalPattern();
}
