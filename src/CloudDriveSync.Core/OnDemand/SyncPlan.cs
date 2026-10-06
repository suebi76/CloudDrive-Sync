namespace CloudDriveSync.Core.OnDemand;

/// <summary>One step a run of files on demand carries out. <see cref="Path"/> is where it happens, in the cloud's terms.</summary>
public abstract record SyncAction(string Path);

/// <summary>New in the cloud: a placeholder (or a folder) appears on the PC.</summary>
public sealed record CreatePlaceholder(CloudEntry Cloud) : SyncAction(Cloud.Path);

/// <summary>Changed in the cloud, unchanged on the PC: the placeholder gets the new version (and fetches it again when the old one was on the PC).</summary>
public sealed record RefreshPlaceholder(SyncItem Item, CloudEntry Cloud, LocalEntry Local) : SyncAction(Cloud.Path);

/// <summary>Deleted in the cloud, unchanged on the PC: it goes on the PC, too (its data, if any, into the recycle bin).</summary>
public sealed record RemoveLocal(SyncItem Item, LocalEntry Local) : SyncAction(Item.Path);

/// <summary>New or changed on the PC: it goes up. <see cref="Item"/> is null for a new file.</summary>
public sealed record Upload(LocalEntry Local, SyncItem? Item) : SyncAction(Local.Path);

/// <summary>A folder new on the PC: it is created in the cloud.</summary>
public sealed record CreateCloudFolder(LocalEntry Local) : SyncAction(Local.Path);

/// <summary>Deleted on the PC, unchanged in the cloud: it is deleted in the cloud, too.</summary>
public sealed record RemoveCloud(SyncItem Item) : SyncAction(Item.Path);

/// <summary>Renamed or moved on the PC: it is moved in the cloud (no upload; shares and versions stay).</summary>
public sealed record MoveCloud(SyncItem Item, string NewPath) : SyncAction(NewPath);

/// <summary>On both sides and the same, but not known yet (first run, rebuild): it is taken as in step.</summary>
public sealed record Adopt(LocalEntry Local, CloudEntry Cloud) : SyncAction(Cloud.Path);

/// <summary>A placeholder that was not marked in sync although it is the version that was uploaded: marked now.</summary>
public sealed record MarkInSync(SyncItem Item, LocalEntry Local) : SyncAction(Item.Path);

/// <summary>Changed on both sides, or different on both sides without a common past: both versions are kept.</summary>
public sealed record Conflict(LocalEntry Local, CloudEntry Cloud, SyncItem? Item) : SyncAction(Cloud.Path);

/// <summary>Gone on both sides: only CloudDrive-Sync's record of it goes.</summary>
public sealed record Forget(SyncItem Item) : SyncAction(Item.Path);

/// <summary>
/// A placeholder without data whose file is in the cloud no more (or no longer selected): it goes - nothing is lost,
/// its data was never on the PC.
/// </summary>
public sealed record DropPlaceholder(LocalEntry Local) : SyncAction(Local.Path);

/// <summary>
/// What a run does, decided before anything happens - so the safety checks can stop it as a whole. Deletions are
/// counted in files, as people count them (<see cref="Sync.DeleteGuard"/> does the same for classic synchronisations).
/// </summary>
/// <param name="KnownFiles">Files both sides had in step before the run.</param>
/// <param name="Skipped">Paths left alone, with the reason (shown in the log).</param>
public sealed record SyncPlan(IReadOnlyList<SyncAction> Actions, int KnownFiles, int CloudDeletions, int LocalDeletions, IReadOnlyList<string> Skipped)
{
    public int Deletions => CloudDeletions + LocalDeletions;

    /// <summary>
    /// Whether the deletions exceed the limit of the synchronisation: more than <paramref name="maxPercent"/> of the
    /// known files and at least <paramref name="minimum"/> of them.
    /// </summary>
    public bool TooManyDeletions(int maxPercent, int minimum) =>
        maxPercent < 100 && KnownFiles > 0 && Deletions >= minimum && Deletions * 100L > (long)Math.Max(1, maxPercent) * KnownFiles;
}
