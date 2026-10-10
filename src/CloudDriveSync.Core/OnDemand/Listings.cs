using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.Accounts;
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

    /// <summary>The PC side, with what Windows knows about each placeholder.</summary>
    public static async Task<IReadOnlyList<LocalEntry>> ListLocalAsync(RcClient rc, string localRoot, string filtersFile, CancellationToken cancellationToken)
    {
        var listed = await CloudWalker.ListAsync(rc, localRoot, "", filtersFile, new JsonObject { ["recurse"] = true, ["noMimeType"] = true }, cancellationToken);
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
}
