using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CloudDriveSync.Core.Sync;

/// <summary>What a run did with a file.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChangeKind>))]
public enum ChangeKind
{
    /// <summary>From the PC to the cloud.</summary>
    Uploaded,
    /// <summary>From the cloud to the PC.</summary>
    Downloaded,
    /// <summary>Deleted on the PC, so deleted in the cloud, too.</summary>
    DeletedInCloud,
    /// <summary>Deleted in the cloud, so deleted on the PC, too (it waits in CloudDrive-Sync's recycle bin).</summary>
    DeletedOnPc,
    /// <summary>The server did not take it; it stays on the PC (see SyncPairSettings.LocalOnly).</summary>
    KeptOnPc,
}

/// <summary>One file a run changed, relative to the synchronised folder and "/" separated.</summary>
public sealed record FileChange(ChangeKind Kind, string Path);

/// <summary>
/// Reads from bisync's report which files a successful run uploaded, downloaded or deleted where. A normal run lists
/// every change in its queue with the full path of the side it goes to ("Queue copy to Path1 - cloud:folder/a.txt");
/// a first run or rebuild has one section per direction ("Resync is copying files to - Path1") followed by the
/// copied files.
/// </summary>
public static partial class RunChanges
{
    /// <summary>At most this many changes are kept per run; a run with more records how many were left out.</summary>
    public const int Limit = 300;

    public static IReadOnlyList<FileChange> FromReport(string report, string cloudPath, string localPath)
    {
        var cloudPrefix = cloudPath.EndsWith(':') ? cloudPath : cloudPath + "/";
        var localPrefix = Path.TrimEndingDirectorySeparator(localPath) + Path.DirectorySeparatorChar;
        var changes = new List<FileChange>();
        ChangeKind? resyncDirection = null;
        foreach (var line in report.Split('\n'))
        {
            Match match;
            if ((match = QueueCopyPattern().Match(line)).Success)
            {
                var target = match.Groups["target"].Value;
                if (match.Groups["side"].Value == "1" && target.StartsWith(cloudPrefix, StringComparison.Ordinal))
                    Add(changes, ChangeKind.Uploaded, target[cloudPrefix.Length..]);
                else if (match.Groups["side"].Value == "2" && target.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase))
                    Add(changes, ChangeKind.Downloaded, target[localPrefix.Length..]);
            }
            else if ((match = QueueDeletePattern().Match(line)).Success)
            {
                var target = match.Groups["target"].Value;
                if (target.StartsWith(cloudPrefix, StringComparison.Ordinal)) Add(changes, ChangeKind.DeletedInCloud, target[cloudPrefix.Length..]);
                else if (target.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase)) Add(changes, ChangeKind.DeletedOnPc, target[localPrefix.Length..]);
            }
            else if ((match = ResyncSectionPattern().Match(line)).Success)
            {
                resyncDirection = match.Groups["side"].Value == "1" ? ChangeKind.Uploaded : ChangeKind.Downloaded;
            }
            else if (line.Contains("Resync updating listings", StringComparison.Ordinal))
            {
                resyncDirection = null;
            }
            else if (resyncDirection is { } direction && (match = CopiedPattern().Match(line)).Success)
            {
                Add(changes, direction, match.Groups["path"].Value);
            }
        }
        return changes;
    }

    private static void Add(List<FileChange> changes, ChangeKind kind, string path)
    {
        path = path.Replace('\\', '/').Trim();
        // CloudDrive-Sync's own files are not news for anyone.
        if (path.Length == 0 || path.StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase)) return;
        changes.Add(new FileChange(kind, path));
    }

    [GeneratedRegex(@"Queue copy to Path(?<side>[12])\s+-\s+(?<target>.+?)\s*$")]
    private static partial Regex QueueCopyPattern();

    [GeneratedRegex(@"Queue delete\s+-\s+(?<target>.+?)\s*$")]
    private static partial Regex QueueDeletePattern();

    [GeneratedRegex(@"Resync is copying files to\s+-\s+Path(?<side>[12])")]
    private static partial Regex ResyncSectionPattern();

    [GeneratedRegex(@"INFO\s+:\s+(?<path>.+?): Copied \((?:new|replaced existing)\)\s*$")]
    private static partial Regex CopiedPattern();
}
