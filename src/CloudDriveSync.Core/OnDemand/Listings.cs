using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.OnDemand;

/// <summary>A file or folder in the cloud folder of a synchronisation, as rclone lists it.</summary>
/// <param name="Path">Relative to the cloud folder, "/" separated, in rclone's standard encoding.</param>
/// <param name="Ticks">The server's modification time (UTC ticks).</param>
/// <param name="Hash">A checksum where the server has one (Nextcloud), e.g. "sha1:…"; null otherwise.</param>
public sealed record CloudEntry(string Path, bool IsDirectory, long Size, long Ticks, string? Hash);

/// <summary>A file or folder in the synchronised folder on the PC.</summary>
/// <param name="Path">Relative to the folder, "/" separated, in rclone's standard encoding - comparable with the cloud.</param>
/// <param name="Ticks">Last write time (UTC ticks).</param>
/// <param name="Placeholder">What Windows knows about it, or null for a normal file or folder.</param>
public sealed record LocalEntry(string Path, bool IsDirectory, long Size, long Ticks, PlaceholderInfo? Placeholder)
{
    public bool IsPlaceholder => Placeholder is not null;

    /// <summary>The item the placeholder belongs to (it travels with the file when it is renamed or moved).</summary>
    public long? ItemId => Placeholder is null ? null : ItemIdentity.Decode(Placeholder.Identity)?.Id;

    /// <summary>Windows' in-sync state: cleared on every change of a placeholder; a normal file is never in sync.</summary>
    public bool InSync => Placeholder?.InSync == true;

    /// <summary>All the file's data is on the PC (always for normal files).</summary>
    public bool OnDisk => Placeholder is null || Placeholder.IsFullyOnDisk;
}

/// <summary>
/// The two sides of a synchronisation with files on demand, listed by rclone with the same filter file - so the
/// selection, the standard exclusions (Office's lock files, the recycle bin …) and the files that stay on the PC apply
/// to both sides exactly as in classic synchronisations, and both sides come in rclone's standard encoding. Listing
/// reads only metadata: no placeholder is ever fetched for it.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal static class Listings
{
    /// <summary>The cloud side.</summary>
    /// <param name="SentinelFound">The protection file lies in the cloud folder (it is not one of the entries).</param>
    /// <param name="CaseClashes">Names that differ only in upper and lower case: Windows cannot hold both, so both stay out.</param>
    public sealed record CloudSide(IReadOnlyDictionary<string, CloudEntry> Entries, bool SentinelFound, IReadOnlyList<string> CaseClashes);

    public static async Task<CloudSide> ListCloudAsync(RcClient rc, SyncPairSettings pair, string filtersFile, bool withHashes, CancellationToken cancellationToken)
    {
        var options = new JsonObject { ["recurse"] = true, ["noMimeType"] = true };
        if (withHashes) options["showHash"] = true;
        var entries = new Dictionary<string, CloudEntry>(StringComparer.Ordinal);
        var sentinel = false;
        foreach (var entry in await ListAsync(rc, BisyncCommand.CloudPath(pair), filtersFile, options, cancellationToken))
        {
            if (entry.Path == SyncFilters.SentinelFile)
            {
                sentinel = true;
                continue;
            }
            entries[entry.Path] = entry;
        }
        // Two names that differ only in upper and lower case cannot both live in one Windows folder. Neither of them is
        // taken, so one is never taken for the other (and its content never carried over to it).
        var clashes = entries.Keys.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        foreach (var clash in clashes)
        {
            entries.Remove(clash);
            foreach (var below in entries.Keys.Where(p => p.StartsWith(clash + "/", StringComparison.Ordinal)).ToList()) entries.Remove(below);
        }
        return new CloudSide(entries, sentinel, clashes);
    }

    /// <summary>The PC side, with what Windows knows about each placeholder.</summary>
    public static async Task<IReadOnlyList<LocalEntry>> ListLocalAsync(RcClient rc, string localRoot, string filtersFile, CancellationToken cancellationToken)
    {
        var listed = await ListAsync(rc, localRoot, filtersFile, new JsonObject { ["recurse"] = true, ["noMimeType"] = true }, cancellationToken);
        return await Task.Run(() =>
        {
            var entries = new List<LocalEntry>(listed.Count);
            foreach (var entry in listed)
            {
                if (entry.Path == SyncFilters.SentinelFile) continue;
                cancellationToken.ThrowIfCancellationRequested();
                var full = Path.Combine(localRoot, NameEncoding.ToLocalPath(entry.Path));
                PlaceholderInfo? placeholder;
                try
                {
                    placeholder = Placeholders.Read(full);
                }
                catch (FileNotFoundException)
                {
                    // Gone since it was listed: the next run sees it as it is then.
                    continue;
                }
                entries.Add(new LocalEntry(entry.Path, entry.IsDirectory, entry.Size, entry.Ticks, placeholder));
            }
            return (IReadOnlyList<LocalEntry>)entries;
        }, cancellationToken);
    }

    private static async Task<List<CloudEntry>> ListAsync(RcClient rc, string fs, string filtersFile, JsonObject options, CancellationToken cancellationToken)
    {
        var result = await rc.CallAsync("operations/list", new JsonObject
        {
            ["fs"] = fs,
            ["remote"] = "",
            ["opt"] = options,
            ["_filter"] = new JsonObject { ["FilterFrom"] = new JsonArray(filtersFile) },
        }, TimeSpan.FromMinutes(10), cancellationToken);
        var entries = new List<CloudEntry>();
        foreach (var node in result["list"] as JsonArray ?? [])
        {
            if (node is not JsonObject item) continue;
            var path = item["Path"]?.GetValue<string>() ?? "";
            if (path.Length == 0) continue;
            var isDirectory = item["IsDir"]?.GetValue<bool>() == true;
            var size = isDirectory ? 0 : item["Size"] is JsonValue s && s.TryGetValue<long>(out var bytes) ? bytes : 0;
            var ticks = DateTimeOffset.TryParse(item["ModTime"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var modified)
                ? modified.UtcTicks : 0;
            entries.Add(new CloudEntry(path, isDirectory, size, ticks, FileServer.HashOf(item)));
        }
        return entries;
    }
}
