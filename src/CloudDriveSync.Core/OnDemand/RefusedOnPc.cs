using System.Runtime.Versioning;
using CloudDriveSync.Core.Diagnostics;

namespace CloudDriveSync.Core.OnDemand;

/// <summary>
/// What Windows refused on the PC in the last run of a synchronisation with files on demand
/// (sync\&lt;id&gt;\refused-on-pc.txt): one entry per line, "broken" (Windows calls it broken itself) or "refused", a tab,
/// the path relative to the folder as it is on the PC. Two uses: an entry refused before and gone now was not deleted by
/// the user - nothing deletes such an entry while Windows refuses it -, so it is made again from the cloud and never
/// deleted there (<see cref="Vanished"/>). And the clean-up script for Safe Mode removes exactly the broken ones of a
/// folder that is still synchronised - nothing else in it.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal static class RefusedOnPc
{
    private const string BrokenMark = "broken", RefusedMark = "refused";

    public static string FileOf(string pairFolder) => Path.Combine(pairFolder, "refused-on-pc.txt");

    /// <summary>The entries refused in the last run (paths of the cloud), or none.</summary>
    public static IReadOnlyList<string> Load(string pairFolder)
    {
        try
        {
            var file = FileOf(pairFolder);
            if (!File.Exists(file)) return [];
            return File.ReadAllLines(file)
                .Select(line => line.Split('\t', 2))
                .Where(parts => parts.Length == 2 && parts[0] is BrokenMark or RefusedMark && parts[1].Length > 0)
                .Select(parts => NameEncoding.ToStandardPath(parts[1]))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("OnDemand", $"List of entries refused on this PC not read: {e.Message}");
            return [];
        }
    }

    /// <summary>Remembers what this run found refused; nothing refused, no file.</summary>
    public static void Save(string pairFolder, IReadOnlyCollection<string> refused, IReadOnlyCollection<string> broken)
    {
        var file = FileOf(pairFolder);
        try
        {
            if (refused.Count == 0)
            {
                File.Delete(file);
                return;
            }
            var brokenSet = new HashSet<string>(broken, StringComparer.Ordinal);
            var temporary = file + ".tmp";
            File.WriteAllLines(temporary, refused.Order(StringComparer.Ordinal)
                .Select(path => $"{(brokenSet.Contains(path) ? BrokenMark : RefusedMark)}\t{NameEncoding.ToLocalPath(path)}"));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("OnDemand", $"List of entries refused on this PC not saved: {e.Message}");
        }
    }

    /// <summary>
    /// The entries refused in the last run that are gone now: neither listed on the PC nor refused again, nor in or below
    /// a folder refused now (then nothing is known of them).
    /// </summary>
    public static List<string> Vanished(IReadOnlyList<string> before, IReadOnlyList<LocalEntry> listed, IReadOnlyList<string> refusedNow)
    {
        if (before.Count == 0) return [];
        var seen = new HashSet<string>(listed.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        return before
            .Where(path => !seen.Contains(path)
                && !refusedNow.Any(r => path.Equals(r, StringComparison.OrdinalIgnoreCase) || path.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
