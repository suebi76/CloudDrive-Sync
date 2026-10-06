using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
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
    /// <param name="Unreadable">
    /// Folders the server did not let CloudDrive-Sync read (e.g. a share to upload only). What is in them is unknown, so
    /// the run leaves them alone.
    /// </param>
    /// <param name="Folders">How many folders were read.</param>
    public sealed record CloudSide(IReadOnlyDictionary<string, CloudEntry> Entries, bool SentinelFound, IReadOnlyList<string> CaseClashes,
        IReadOnlyList<string> Unreadable, int Folders);

    /// <summary>Folders read at once - rclone's own default for walking a tree; more hardly helps and burdens the server.</summary>
    private const int ParallelFolders = 8;

    /// <summary>
    /// The cloud side, folder by folder, several at once. rclone's recursive listing ends at the first folder the server
    /// refuses, without naming it; here such a folder stays out, and the rest is listed.
    /// </summary>
    public static async Task<CloudSide> ListCloudAsync(RcClient rc, SyncPairSettings pair, string filtersFile, bool withHashes, CancellationToken cancellationToken)
    {
        var fs = WithoutPause(BisyncCommand.CloudPath(pair));
        var options = new JsonObject { ["recurse"] = false, ["noMimeType"] = true };
        if (withHashes) options["showHash"] = true;
        var listed = new ConcurrentDictionary<string, CloudEntry>(StringComparer.Ordinal);
        var unreadable = new ConcurrentBag<string>();
        var sentinel = false;
        var folders = 0;
        using var gate = new SemaphoreSlim(ParallelFolders);

        async Task ListFolderAsync(string folder)
        {
            List<CloudEntry> entries;
            await gate.WaitAsync(cancellationToken);
            try
            {
                entries = await ListAsync(rc, fs, folder, filtersFile, options, cancellationToken);
                Interlocked.Increment(ref folders);
            }
            catch (CdException e) when (folder.Length > 0 && !EndsTheRun(e))
            {
                unreadable.Add(folder);
                Log.Warn("OnDemand", $"'{pair.Id}': folder '{folder}' could not be read ({e.Code}: {Shorten(e.Detail ?? e.Message)}); it is left out of this run.");
                return;
            }
            finally
            {
                gate.Release();
            }
            var below = new List<Task>();
            foreach (var entry in entries)
            {
                if (entry.Path == SyncFilters.SentinelFile)
                {
                    sentinel = true;
                    continue;
                }
                listed[entry.Path] = entry;
                if (entry.IsDirectory) below.Add(ListFolderAsync(entry.Path));
            }
            await Task.WhenAll(below);
        }

        await ListFolderAsync("");
        var all = new Dictionary<string, CloudEntry>(listed, StringComparer.Ordinal);
        // Two names that differ only in upper and lower case cannot both live in one Windows folder. Neither of them is
        // taken, so one is never taken for the other (and its content never carried over to it).
        var clashes = all.Keys.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        foreach (var clash in clashes)
        {
            all.Remove(clash);
            foreach (var below in all.Keys.Where(p => p.StartsWith(clash + "/", StringComparison.Ordinal)).ToList()) all.Remove(below);
        }
        return new CloudSide(all, sentinel, clashes, unreadable.Order(StringComparer.Ordinal).ToList(), folders);
    }

    /// <summary>
    /// rclone's WebDAV backend waits at least 10 ms between two requests (pacer_min_sleep): at most 100 folders a second,
    /// however fast the server. The listing limits itself to a few requests at once, so the pause shrinks to 1 ms - not to
    /// nothing, because rclone doubles it after an answer like "too many requests".
    /// </summary>
    private static string WithoutPause(string fs)
    {
        var colon = fs.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? fs : $"{fs[..colon]},pacer_min_sleep=1ms{fs[colon..]}";
    }

    /// <summary>No connection, no sign-in, no engine: the whole run ends and is tried again, as everywhere else.</summary>
    private static bool EndsTheRun(CdException e) => e.Code is "CD-5001" or "CD-5003" or "CD-3012" or "CD-3006";

    /// <summary>Servers answer a refused folder with a whole web page; the log needs its first line and the status at its end.</summary>
    private static string Shorten(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return "";
        var first = lines[0].Length > 120 ? lines[0][..120] + "…" : lines[0];
        if (lines.Length == 1) return first;
        var last = lines[^1];
        var status = last.LastIndexOf(": ", StringComparison.Ordinal) is var at and >= 0 ? last[(at + 2)..] : last;
        return $"{first} … {(status.Length > 60 ? status[..60] : status)}";
    }

    /// <summary>The PC side, with what Windows knows about each placeholder.</summary>
    public static async Task<IReadOnlyList<LocalEntry>> ListLocalAsync(RcClient rc, string localRoot, string filtersFile, CancellationToken cancellationToken)
    {
        var listed = await ListAsync(rc, localRoot, "", filtersFile, new JsonObject { ["recurse"] = true, ["noMimeType"] = true }, cancellationToken);
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

    /// <param name="remote">The folder to list, relative to <paramref name="fs"/>; the entries' paths are relative to <paramref name="fs"/>, too.</param>
    private static async Task<List<CloudEntry>> ListAsync(RcClient rc, string fs, string remote, string filtersFile, JsonObject options, CancellationToken cancellationToken)
    {
        var result = await rc.CallAsync("operations/list", new JsonObject
        {
            ["fs"] = fs,
            ["remote"] = remote,
            ["opt"] = options.DeepClone(),
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
