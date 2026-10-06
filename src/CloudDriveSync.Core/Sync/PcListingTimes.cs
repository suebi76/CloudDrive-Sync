using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CloudDriveSync.Core.Sync;

/// <summary>
/// Keeps bisync's record of the PC side true to the files on the PC. When a file lies on both sides with the same size
/// and the server's time is the newer one, bisync notes the server's time for the PC as well - without carrying it over,
/// because servers like IServ keep no modification times rclone could set (it compares sizes there). At the next run
/// the PC file then looks "changed (older)": rclone uploads it again, possibly over a newer version on the server, and
/// when every file looks changed - the protection file included - it stops with "all files were changed". This happens
/// after the first synchronisation of a folder whose files were on both sides already, and now and then to the
/// protection file, which the server receives a moment after the PC wrote it.
/// After each successful run, such entries get the time the file really has on the PC. Only entries that carry the
/// server's time to the digit, have the file's size and are newer than the file are corrected: a change on the PC makes
/// a file newer, never older, so no real change is hidden. The files themselves are not touched.
/// </summary>
internal static partial class PcListingTimes
{
    /// <summary>Corrects the PC listing in bisync's work folder; returns how many entries changed.</summary>
    public static int Align(string workDir, string localPath)
    {
        var cloudListing = Find(workDir, ".path1.lst");
        var pcListing = Find(workDir, ".path2.lst");
        if (cloudListing is null || pcListing is null) return 0;

        var cloudTimes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(cloudListing))
            if (EntryPattern().Match(line) is { Success: true } entry) cloudTimes[entry.Groups["path"].Value] = entry.Groups["time"].Value;

        // bisync reads its listings only with its own line ends (LF): everything but the corrected times stays as it is.
        var lines = File.ReadAllText(pcListing).Split('\n');
        var aligned = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (EntryPattern().Match(lines[i]) is not { Success: true } entry) continue;
            var time = entry.Groups["time"];
            if (!cloudTimes.TryGetValue(entry.Groups["path"].Value, out var cloudTime) || cloudTime != time.Value) continue;
            if (Unquote(entry.Groups["path"].Value) is not { } relative || ParseTime(time.Value) is not { } noted) continue;
            var file = new FileInfo(Path.Combine(localPath, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!file.Exists || file.Length.ToString(CultureInfo.InvariantCulture) != entry.Groups["size"].Value) continue;
            var real = file.LastWriteTimeUtc;
            if (real >= noted) continue;
            lines[i] = lines[i][..time.Index] + FormatTime(real) + lines[i][(time.Index + time.Length)..];
            aligned++;
        }
        if (aligned == 0) return 0;
        var temporary = pcListing + ".tmp";
        File.WriteAllText(temporary, string.Join('\n', lines));
        File.Move(temporary, pcListing, overwrite: true);
        return aligned;
    }

    /// <summary>A time as bisync writes it into its listings: UTC, nine decimals ("2026-10-06T09:16:26.123456700+0000").</summary>
    internal static string FormatTime(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "00+0000";

    /// <summary>A time from bisync's listings in UTC, or null when it cannot be read.</summary>
    internal static DateTime? ParseTime(string text)
    {
        if (TimePattern().Match(text) is not { Success: true } time) return null;
        var start = DateTime.ParseExact(time.Groups["second"].Value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var ticks = long.Parse(time.Groups["fraction"].Value.PadRight(7, '0')[..7], CultureInfo.InvariantCulture);
        var offset = new TimeSpan(int.Parse(time.Groups["hours"].Value, CultureInfo.InvariantCulture), int.Parse(time.Groups["minutes"].Value, CultureInfo.InvariantCulture), 0);
        if (time.Groups["sign"].Value == "-") offset = offset.Negate();
        return DateTime.SpecifyKind(start.AddTicks(ticks) - offset, DateTimeKind.Utc);
    }

    /// <summary>
    /// A path as bisync quotes it ("Ordner/Datei.txt"), without the quotes; null when it holds an escape other than \" and
    /// \\ - such entries are left alone.
    /// </summary>
    internal static string? Unquote(string quoted)
    {
        if (quoted.Length < 2 || quoted[0] != '"' || quoted[^1] != '"') return null;
        var text = new StringBuilder(quoted.Length);
        for (var i = 1; i < quoted.Length - 1; i++)
        {
            if (quoted[i] != '\\')
            {
                text.Append(quoted[i]);
                continue;
            }
            if (i + 1 >= quoted.Length - 1 || quoted[i + 1] is not ('"' or '\\')) return null;
            text.Append(quoted[++i]);
        }
        return text.ToString();
    }

    private static string? Find(string workDir, string ending) =>
        Directory.Exists(workDir) ? Directory.EnumerateFiles(workDir, "*.lst").FirstOrDefault(f => f.EndsWith(ending, StringComparison.Ordinal)) : null;

    // A file in a bisync listing: "-      162 - - 2026-10-06T09:16:26.000000000+0000 ".clouddrive-sync"".
    [GeneratedRegex("^-\\s+(?<size>\\d+)\\s+\\S+\\s+\\S+\\s+(?<time>\\S+)\\s+(?<path>\".*\")\\s*$")]
    private static partial Regex EntryPattern();

    [GeneratedRegex(@"^(?<second>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(?<fraction>\d{1,9}))?(?<sign>[+-])(?<hours>\d{2}):?(?<minutes>\d{2})$")]
    private static partial Regex TimePattern();
}
