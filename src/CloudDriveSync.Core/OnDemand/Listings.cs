using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
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
    /// <param name="Unreadable">
    /// Folders the server did not let CloudDrive-Sync read (e.g. a share to upload only). What is in them is unknown, so
    /// the run leaves them alone.
    /// </param>
    /// <param name="Folders">How many folders were read.</param>
    /// <param name="Tree">What the listing saw, for the next run (see <see cref="CloudTreeStore"/>).</param>
    /// <param name="Read">Folders read from the server; the others were unchanged since the last run.</param>
    public sealed record CloudSide(IReadOnlyDictionary<string, CloudEntry> Entries, bool SentinelFound, IReadOnlyList<string> CaseClashes,
        IReadOnlyList<string> Unreadable, int Folders, CloudTree? Tree = null, int Read = 0);

    /// <summary>
    /// The cloud side, folder by folder, several at once (<see cref="CloudWalker"/>): a folder the server refuses stays
    /// out, and the rest is listed.
    /// </summary>
    /// <param name="previous">The last run's listing: only what may have changed since is read (Nextcloud).</param>
    /// <param name="rootTicks">The cloud folder's own time from its parent (see <see cref="RootTicksAsync"/>).</param>
    /// <param name="utcNow">This PC's clock.</param>
    internal static async Task<CloudSide> ListCloudAsync(RcClient rc, SyncPairSettings pair, string filtersFile, bool withHashes, CancellationToken cancellationToken,
        Action<ListingProgress>? progress = null, CloudTree? previous = null, long rootTicks = 0, Func<DateTime>? utcNow = null)
    {
        var walk = await CloudWalker.WalkAsync(rc, BisyncCommand.CloudPath(pair), filtersFile, withHashes, progress, pair.Id, cancellationToken, previous, rootTicks, utcNow);
        var all = new Dictionary<string, CloudEntry>(StringComparer.Ordinal);
        var sentinel = false;
        foreach (var entry in walk.Entries)
        {
            if (entry.Path == SyncFilters.SentinelFile) sentinel = true;
            else all[entry.Path] = entry;
        }
        // Two names that differ only in upper and lower case cannot both live in one Windows folder. Neither of them is
        // taken, so one is never taken for the other (and its content never carried over to it).
        var clashes = all.Keys.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        foreach (var clash in clashes)
        {
            all.Remove(clash);
            foreach (var below in all.Keys.Where(p => p.StartsWith(clash + "/", StringComparison.Ordinal)).ToList()) all.Remove(below);
        }
        return new CloudSide(all, sentinel, clashes, walk.Unreadable, walk.Folders, walk.Tree, walk.Read);
    }

    /// <summary>
    /// The time of the synchronised cloud folder itself, from its parent's listing - one request, and an unchanged folder
    /// needs no other. 0 for a whole account (it has no parent to ask) or when it cannot be read.
    /// </summary>
    internal static Task<long> RootTicksAsync(RcClient rc, SyncPairSettings pair, CancellationToken cancellationToken)
    {
        var path = pair.RemotePath.Trim('/');
        if (path.Length == 0) return Task.FromResult(0L);
        var slash = path.LastIndexOf('/');
        var parent = $"{AccountService.RemoteName(pair.AccountId)}:{(slash < 0 ? "" : path[..slash])}";
        return CloudWalker.FolderTicksAsync(rc, parent, slash < 0 ? path : path[(slash + 1)..], cancellationToken);
    }

    /// <summary>The PC side (see <see cref="ListLocalAsync"/>).</summary>
    /// <param name="Refused">
    /// Entries Windows refuses to open - a folder it cannot list, a placeholder it calls broken. What is in or below them
    /// is unknown: they are left out like folders the server does not let be read, never taken as deleted on the PC.
    /// </param>
    /// <param name="Broken">
    /// Those of them Windows calls broken itself (<see cref="Placeholders.IsBroken"/>) - not just a folder rclone could
    /// not list: only these may be removed in Safe Mode (see <see cref="RefusedOnPc"/>).
    /// </param>
    public sealed record LocalSide(IReadOnlyList<LocalEntry> Entries, IReadOnlyList<string> Refused, IReadOnlyList<string> Broken);

    /// <summary>The PC side, with what Windows knows about each placeholder.</summary>
    public static async Task<LocalSide> ListLocalAsync(RcClient rc, string localRoot, string filtersFile, CancellationToken cancellationToken)
    {
        IReadOnlyList<CloudEntry> listed;
        var refused = new List<string>();
        var broken = new List<string>();
        try
        {
            listed = await CloudWalker.ListAsync(rc, localRoot, "", filtersFile, new JsonObject { ["recurse"] = true, ["noMimeType"] = true }, cancellationToken);
        }
        catch (Errors.CdException e)
        {
            // rclone's recursive listing stops at the first folder it cannot read. Folder by folder, such a folder is left
            // out and everything else is read.
            Log.Warn("OnDemand", $"Folder on this PC not completely readable ({e.Detail ?? e.Message}); it is read folder by folder.");
            var options = new JsonObject { ["recurse"] = false, ["noMimeType"] = true };
            var walk = await CloudWalker.WalkAsync((folder, token) => CloudWalker.ListAsync(rc, localRoot, folder, filtersFile, options, token), previous: null, progress: null,
                "PC", cancellationToken);
            listed = walk.Entries;
            refused.AddRange(walk.Unreadable);
        }
        return await Task.Run(() =>
        {
            // rclone leaves a folder it cannot open out of its listing without a word - all in it would count as deleted
            // on the PC. Every folder is opened here, too.
            refused.AddRange(RefusedFolders(localRoot, broken));
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
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Windows refuses it (e.g. a placeholder it calls broken): left out, never taken as deleted.
                    refused.Add(entry.Path);
                    if (Placeholders.IsBroken(e)) broken.Add(entry.Path);
                    continue;
                }
                entries.Add(new LocalEntry(entry.Path, entry.IsDirectory, entry.Size, entry.Ticks, placeholder));
            }
            if (refused.Count > 0) Log.Warn("OnDemand", $"{refused.Count} entry(s) on this PC refused by Windows, left out: {string.Join(", ", refused.Take(5))}");
            return new LocalSide(entries, refused.Distinct(StringComparer.Ordinal).ToList(), broken.Distinct(StringComparer.Ordinal).ToList());
        }, cancellationToken);
    }

    /// <summary>
    /// What the listing does not show although it is still on the PC - checked for every entry the last run knew and the
    /// cloud still has, the ones a run would otherwise delete in the cloud. rclone passes over what it cannot take without
    /// a word: a placeholder Windows calls broken looks like a link to it, and links are not followed. So nothing counts as
    /// deleted on the PC unless the PC says it is gone; what is still there is left out like a refused entry.
    /// </summary>
    internal static LocalSide WithUnlisted(LocalSide pc, string localRoot, IEnumerable<string> knownInCloud)
    {
        var listed = new HashSet<string>(pc.Entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        var refused = pc.Refused.ToList();
        var broken = pc.Broken.ToList();
        var added = 0;
        foreach (var path in knownInCloud)
        {
            if (listed.Contains(path) || refused.Any(r => path.Equals(r, StringComparison.OrdinalIgnoreCase) || path.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase)))
                continue;
            var full = Path.Combine(localRoot, NameEncoding.ToLocalPath(path));
            if (!StillThere(full)) continue;
            refused.Add(path);
            if (Placeholders.IsBrokenAt(full)) broken.Add(path);
            added++;
        }
        if (added == 0) return pc;
        Log.Warn("OnDemand", $"{added} entry(s) on this PC not listed although still there, left out: {string.Join(", ", refused.Skip(pc.Refused.Count).Take(5))}");
        return pc with { Refused = refused, Broken = broken };
    }

    /// <summary>Whether something of that name is still on the PC - asked twice, the second time by listing its folder.</summary>
    private static bool StillThere(string full)
    {
        if (Path.Exists(full)) return true;
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(parent)) return false;
        try
        {
            var name = Path.GetFileName(full);
            return Directory.EnumerateFileSystemEntries(parent).Any(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not known: rather there than deleted.
            return true;
        }
    }

    /// <summary>
    /// The folders below <paramref name="root"/> Windows refuses to open (no right to, a placeholder it calls broken), as
    /// paths of the cloud; those it calls broken also go into <paramref name="broken"/>. Links are not followed. The
    /// folder itself unreadable ends the run, as before.
    /// </summary>
    internal static List<string> RefusedFolders(string root, List<string>? broken = null)
    {
        var refused = new List<string>();
        var folders = new Stack<string>([root]);
        while (folders.Count > 0)
        {
            var folder = folders.Pop();
            List<string> below;
            try
            {
                below = Directory.EnumerateDirectories(folder).ToList();
            }
            catch (Exception e) when (folder != root && e is IOException or UnauthorizedAccessException)
            {
                refused.Add(NameEncoding.ToStandardPath(Path.GetRelativePath(root, folder)));
                if (Placeholders.IsBroken(e)) broken?.Add(refused[^1]);
                continue;
            }
            foreach (var sub in below)
            {
                bool link;
                try
                {
                    link = new DirectoryInfo(sub).LinkTarget is not null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    refused.Add(NameEncoding.ToStandardPath(Path.GetRelativePath(root, sub)));
                    if (Placeholders.IsBroken(e)) broken?.Add(refused[^1]);
                    continue;
                }
                if (!link) folders.Push(sub);
            }
        }
        return refused;
    }
}
