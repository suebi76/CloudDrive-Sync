using CloudDriveSync.Core.CloudFiles;

namespace CloudDriveSync.Core.OnDemand;

/// <summary>
/// Decides what a run of files on demand does - a pure function of three pictures: what both sides had in step after
/// the last run (the <see cref="ItemStore"/>), the cloud now and the PC now. A three-way comparison: a side "changed"
/// when it differs from the common past. The rules put safety first:
/// <list type="bullet">
/// <item>Nothing is lost when both sides changed: both versions are kept (<see cref="Conflict"/>).</item>
/// <item>A change beats a deletion: changed in the cloud but deleted on the PC (or the other way round) brings the file
/// back instead of deleting the change.</item>
/// <item>Without a common past (first run, rebuild) nothing is deleted at all.</item>
/// <item>A normal file at the place of a placeholder is a change, not a deletion plus a new file - Office and many
/// other programs save that way.</item>
/// <item>A placeholder found at another place was renamed or moved on the PC: it is moved in the cloud, not uploaded
/// again. A moved folder takes everything in it along.</item>
/// <item>A folder the server did not let be read is left alone with everything in it: its content is unknown, so
/// nothing there counts as deleted, and nothing is created or uploaded there.</item>
/// </list>
/// Paths on the PC are compared with those of the cloud without regard to upper and lower case, as Windows does.
/// </summary>
public static class Planner
{
    /// <param name="Rebuild">First run or rebuild: the past only helps to recognise files, nothing is deleted.</param>
    /// <param name="Unreadable">Cloud folders the server did not let be read in this run (see <see cref="Listings.CloudSide"/>).</param>
    /// <param name="InStep">
    /// Converting a classic synchronisation: the files its last run left in step that did not change since. Only these
    /// count as the same file on both sides without a checksum; any other file on both sides is kept twice.
    /// </param>
    public sealed record Input(IReadOnlyList<SyncItem> Known, IReadOnlyDictionary<string, CloudEntry> Cloud, IReadOnlyList<LocalEntry> Local, bool Rebuild,
        IReadOnlyCollection<string>? Unreadable = null, IReadOnlySet<string>? InStep = null);

    public static SyncPlan Plan(Input input)
    {
        var actions = new List<SyncAction>();
        var skipped = new List<string>();
        var knownById = input.Known.ToDictionary(i => i.Id);
        var knownByPath = input.Known.ToDictionary(i => i.Path, StringComparer.Ordinal);
        var cloud = new Dictionary<string, CloudEntry>(input.Cloud, StringComparer.Ordinal);
        var knownFiles = input.Rebuild ? 0 : input.Known.Count(i => !i.IsDirectory);
        var unreadable = input.Unreadable ?? [];
        bool LeftOut(string place) => unreadable.Any(folder => IsSameOrBelow(place, folder, StringComparison.OrdinalIgnoreCase));
        foreach (var folder in unreadable) skipped.Add($"{folder}: the server does not let it be read; left alone in this run");

        // 1. Renamed or moved on the PC, outermost first: a moved folder takes the places of everything in it along.
        if (!input.Rebuild)
        {
            foreach (var entry in input.Local.Where(e => e.ItemId is not null).OrderBy(e => Depth(e.Path)))
            {
                if (!knownById.TryGetValue(entry.ItemId!.Value, out var item)) continue;
                if (string.Equals(item.Path, entry.Path, StringComparison.Ordinal) || item.IsDirectory != entry.IsDirectory) continue;
                if (LeftOut(item.Path) || LeftOut(entry.Path)) continue;
                // Gone in the cloud meanwhile, or its new place is taken there or by another known item: handled below
                // as a new file on the PC, which keeps everything.
                if (!cloud.ContainsKey(item.Path)) continue;
                var caseOnly = string.Equals(item.Path, entry.Path, StringComparison.OrdinalIgnoreCase);
                if (!caseOnly && (cloud.ContainsKey(entry.Path) || knownByPath.ContainsKey(entry.Path))) continue;
                actions.Add(new MoveCloud(item, entry.Path));
                Relocate(item.Path, entry.Path, knownById, knownByPath, cloud);
            }
        }

        // 2. Every place on either side, with the PC's spelling mapped onto the cloud's where only the case differs.
        var knownIgnoringCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in knownByPath.Keys) knownIgnoringCase.TryAdd(path, path);
        var cloudIgnoringCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in cloud.Keys) cloudIgnoringCase.TryAdd(path, path);
        var local = new Dictionary<string, LocalEntry>(StringComparer.Ordinal);
        foreach (var entry in input.Local)
        {
            var place = knownIgnoringCase.GetValueOrDefault(entry.Path) ?? cloudIgnoringCase.GetValueOrDefault(entry.Path) ?? entry.Path;
            local[place] = entry;
        }
        var places = new SortedSet<string>(knownByPath.Keys.Concat(cloud.Keys).Concat(local.Keys), StringComparer.Ordinal);
        // Items whose placeholder is still somewhere on the PC are never "deleted on the PC", wherever they are now.
        var stillOnPc = input.Local.Where(e => e.ItemId is not null).Select(e => e.ItemId!.Value).ToHashSet();

        var cloudDeletions = 0;
        var localDeletions = 0;
        foreach (var place in places)
        {
            if (LeftOut(place)) continue;
            knownByPath.TryGetValue(place, out var known);
            cloud.TryGetValue(place, out var inCloud);
            local.TryGetValue(place, out var onPc);
            var isDirectory = known?.IsDirectory ?? inCloud?.IsDirectory ?? onPc!.IsDirectory;
            if ((inCloud is not null && inCloud.IsDirectory != isDirectory) || (onPc is not null && onPc.IsDirectory != isDirectory))
            {
                skipped.Add($"{place}: a file on one side, a folder on the other");
                continue;
            }

            if (known is null)
            {
                if (inCloud is not null && onPc is not null)
                    actions.Add(isDirectory || (Same(onPc, inCloud) && (input.InStep is null || input.InStep.Contains(place)))
                        ? new Adopt(onPc, inCloud) : new Conflict(onPc, inCloud, null));
                else if (inCloud is not null)
                    actions.Add(new CreatePlaceholder(inCloud));
                else if (onPc!.IsDirectory)
                    actions.Add(new CreateCloudFolder(onPc));
                else if (onPc.IsPlaceholder && !onPc.OnDisk)
                    actions.Add(new DropPlaceholder(onPc));
                else
                    actions.Add(new Upload(onPc, null));
                continue;
            }

            if (inCloud is null && onPc is null)
            {
                actions.Add(new Forget(known));
            }
            else if (inCloud is null)
            {
                // Deleted in the cloud.
                if (isDirectory)
                {
                    actions.Add(input.Rebuild ? new CreateCloudFolder(onPc!) : new RemoveLocal(known, onPc!));
                }
                else if (LocalChange(onPc!, known) is Change.Changed || input.Rebuild)
                {
                    // A change on the PC is kept: the file goes up again. A file without data cannot go anywhere.
                    if (onPc!.OnDisk) actions.Add(new Upload(onPc, known));
                    else actions.Add(new DropPlaceholder(onPc));
                }
                else
                {
                    actions.Add(new RemoveLocal(known, onPc!));
                    localDeletions++;
                }
            }
            else if (onPc is null)
            {
                // Deleted on the PC - unless it was moved to a place that is taken (its placeholder is still there).
                if (stillOnPc.Contains(known.Id))
                {
                    skipped.Add($"{place}: moved on the PC to a place that is taken in the cloud; left as it is");
                }
                else if (isDirectory)
                {
                    if (!input.Rebuild) actions.Add(new RemoveCloud(known));
                    else actions.Add(new CreatePlaceholder(inCloud));
                }
                else if (CloudChanged(inCloud, known) || input.Rebuild)
                {
                    actions.Add(new CreatePlaceholder(inCloud));
                }
                else
                {
                    actions.Add(new RemoveCloud(known));
                    cloudDeletions++;
                }
            }
            else if (!isDirectory)
            {
                var cloudChanged = CloudChanged(inCloud, known);
                var localChange = LocalChange(onPc, known);
                if (input.Rebuild && localChange is Change.Changed && cloudChanged)
                    actions.Add(Same(onPc, inCloud) ? new Adopt(onPc, inCloud) : new Conflict(onPc, inCloud, known));
                else if (cloudChanged && localChange is Change.Changed)
                    actions.Add(new Conflict(onPc, inCloud, known));
                else if (cloudChanged)
                    actions.Add(new RefreshPlaceholder(known, inCloud, onPc));
                else if (localChange is Change.Changed && onPc.OnDisk)
                    actions.Add(new Upload(onPc, known));
                else if (localChange is Change.Changed)
                    skipped.Add($"{place}: another file's placeholder without data lies here; left as it is");
                else if (localChange is Change.NotMarked)
                    actions.Add(new MarkInSync(known, onPc));
            }
        }
        return new SyncPlan(actions, knownFiles, cloudDeletions, localDeletions, skipped);
    }

    /// <summary>
    /// "Speicherplatz automatisch freigeben": whether a file gives its space back - its data is on the PC, it is in sync
    /// (a change not uploaded yet is never lost), it has no pin state of its own, and it was neither opened nor changed
    /// nor fetched after <paramref name="latestUtc"/>.
    /// </summary>
    public static bool ShouldFree(PlaceholderInfo info, DateTime lastAccessUtc, DateTime lastWriteUtc, long onDiskSinceTicks, DateTime latestUtc) =>
        info is { OnDiskSize: > 0, InSync: true, Pin: PinState.Unspecified }
        && Math.Max(Math.Max(lastAccessUtc.Ticks, lastWriteUtc.Ticks), onDiskSinceTicks) <= latestUtc.Ticks;

    private enum Change
    {
        None,
        /// <summary>The upload happened, but the file could not be marked in sync then (it was open).</summary>
        NotMarked,
        Changed,
    }

    private static Change LocalChange(LocalEntry entry, SyncItem known)
    {
        // A normal file where a placeholder was, or another placeholder (moved there): replaced, i.e. changed.
        if (entry.Placeholder is null || entry.ItemId != known.Id) return Change.Changed;
        if (entry.InSync) return Change.None;
        return entry.Size == known.LocalSize && entry.Ticks == known.LocalTicks ? Change.NotMarked : Change.Changed;
    }

    private static bool CloudChanged(CloudEntry entry, SyncItem known)
    {
        if (entry.Hash is not null && known.CloudHash is not null) return entry.Hash != known.CloudHash;
        return entry.Size != known.CloudSize || entry.Ticks != known.CloudTicks;
    }

    /// <summary>
    /// Both sides have a file without a common past: taken as the same when the sizes match - as classic synchronisations
    /// do on servers without checksums; where the server has a checksum, the run compares it before it adopts.
    /// </summary>
    private static bool Same(LocalEntry entry, CloudEntry cloud) => entry.Size == cloud.Size && (entry.Placeholder is null || entry.InSync);

    /// <summary>Moves a known item (and, for a folder, everything below it) to a new place in the plan's pictures.</summary>
    private static void Relocate(string from, string to, Dictionary<long, SyncItem> knownById, Dictionary<string, SyncItem> knownByPath, Dictionary<string, CloudEntry> cloud)
    {
        foreach (var item in knownById.Values.Where(i => IsSameOrBelow(i.Path, from)).ToList())
        {
            var moved = item with { Path = to + item.Path[from.Length..] };
            knownByPath.Remove(item.Path);
            knownById[item.Id] = moved;
            knownByPath[moved.Path] = moved;
        }
        foreach (var path in cloud.Keys.Where(p => IsSameOrBelow(p, from)).ToList())
        {
            var entry = cloud[path];
            cloud.Remove(path);
            var movedPath = to + path[from.Length..];
            cloud[movedPath] = entry with { Path = movedPath };
        }
    }

    private static bool IsSameOrBelow(string path, string folder, StringComparison comparison = StringComparison.Ordinal) =>
        string.Equals(path, folder, comparison) || path.StartsWith(folder + "/", comparison);

    private static int Depth(string path) => path.Count(c => c == '/');
}
