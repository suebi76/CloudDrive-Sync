using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.OnDemand;

namespace CloudDriveSync.Core.Tests;

public class PlannerTests
{
    private const long T0 = 638_900_000_000_000_000;
    private const long T1 = T0 + 10_000_000;

    // --- Builders: a known item, a cloud entry, a placeholder on the PC, a normal file on the PC.
    private static SyncItem Known(long id, string path, long size = 10, long ticks = T0, string? hash = null, bool dir = false) =>
        new(id, path, dir, dir ? 0 : size, ticks, hash, dir ? null : size, dir ? null : ticks);

    private static CloudEntry Cloud(string path, long size = 10, long ticks = T0, string? hash = null, bool dir = false) => new(path, dir, dir ? 0 : size, ticks, hash);

    private static LocalEntry Placeholder(long id, string path, long size = 10, long ticks = T0, bool inSync = true, bool onDisk = false, bool dir = false) =>
        new(path, dir, dir ? 0 : size, ticks, new PlaceholderInfo(dir ? 0 : size, onDisk || dir ? size : 0, PinState.Unspecified, inSync, ItemIdentity.Encode(id, path)));

    private static LocalEntry Normal(string path, long size = 10, long ticks = T0, bool dir = false) => new(path, dir, dir ? 0 : size, ticks, null);

    private static SyncPlan Plan(SyncItem[] known, CloudEntry[] cloud, LocalEntry[] local, bool rebuild = false) =>
        Planner.Plan(new Planner.Input(known, cloud.ToDictionary(c => c.Path, StringComparer.Ordinal), local, rebuild));

    private static T Single<T>(SyncPlan plan) where T : SyncAction => Assert.IsType<T>(Assert.Single(plan.Actions));

    [Fact]
    public void Nothing_happens_when_both_sides_are_in_step()
    {
        var plan = Plan([Known(1, "a.txt")], [Cloud("a.txt")], [Placeholder(1, "a.txt")]);
        Assert.Empty(plan.Actions);
        Assert.Equal(1, plan.KnownFiles);
    }

    [Fact]
    public void A_new_file_in_the_cloud_becomes_a_placeholder() =>
        Assert.Equal("neu.txt", Single<CreatePlaceholder>(Plan([], [Cloud("neu.txt")], [])).Cloud.Path);

    [Fact]
    public void A_new_file_on_the_PC_goes_up() =>
        Assert.Null(Single<Upload>(Plan([], [], [Normal("neu.txt")])).Item);

    [Fact]
    public void A_new_folder_on_the_PC_is_created_in_the_cloud() =>
        Single<CreateCloudFolder>(Plan([], [], [Normal("Ordner", dir: true)]));

    [Fact]
    public void A_change_in_the_cloud_refreshes_the_placeholder()
    {
        var action = Single<RefreshPlaceholder>(Plan([Known(1, "a.txt")], [Cloud("a.txt", size: 12, ticks: T1)], [Placeholder(1, "a.txt")]));
        Assert.Equal(12, action.Cloud.Size);
    }

    [Fact]
    public void A_same_size_change_in_the_cloud_is_seen_by_its_time() =>
        Single<RefreshPlaceholder>(Plan([Known(1, "a.txt")], [Cloud("a.txt", ticks: T1)], [Placeholder(1, "a.txt")]));

    [Fact]
    public void With_checksums_only_the_checksum_counts()
    {
        // Nextcloud: a new time with the same checksum is no change; a new checksum is one, whatever the time.
        Assert.Empty(Plan([Known(1, "a.txt", hash: "sha1:aa")], [Cloud("a.txt", ticks: T1, hash: "sha1:aa")], [Placeholder(1, "a.txt")]).Actions);
        Single<RefreshPlaceholder>(Plan([Known(1, "a.txt", hash: "sha1:aa")], [Cloud("a.txt", hash: "sha1:bb")], [Placeholder(1, "a.txt")]));
    }

    [Fact]
    public void A_change_on_the_PC_goes_up() =>
        Assert.Equal(1, Single<Upload>(Plan([Known(1, "a.txt")], [Cloud("a.txt")], [Placeholder(1, "a.txt", size: 20, ticks: T1, inSync: false, onDisk: true)])).Item!.Id);

    [Fact]
    public void A_normal_file_where_a_placeholder_was_is_a_change_not_a_deletion()
    {
        // How Office saves: a new file under the old name.
        var plan = Plan([Known(1, "Bericht.docx")], [Cloud("Bericht.docx")], [Normal("Bericht.docx", size: 30, ticks: T1)]);
        Assert.Equal(1, Single<Upload>(plan).Item!.Id);
        Assert.Equal(0, plan.Deletions);
    }

    [Fact]
    public void An_upload_that_could_not_be_marked_is_only_marked_now() =>
        Single<MarkInSync>(Plan([Known(1, "a.txt")], [Cloud("a.txt")], [Placeholder(1, "a.txt", inSync: false, onDisk: true)]));

    [Fact]
    public void Changes_on_both_sides_keep_both_versions() =>
        Single<Conflict>(Plan([Known(1, "a.txt")], [Cloud("a.txt", size: 11, ticks: T1)], [Placeholder(1, "a.txt", size: 12, ticks: T1, inSync: false, onDisk: true)]));

    [Fact]
    public void Deleted_in_the_cloud_goes_on_the_PC_too()
    {
        var plan = Plan([Known(1, "a.txt")], [], [Placeholder(1, "a.txt")]);
        Single<RemoveLocal>(plan);
        Assert.Equal(1, plan.LocalDeletions);
    }

    [Fact]
    public void Deleted_on_the_PC_goes_in_the_cloud_too()
    {
        var plan = Plan([Known(1, "a.txt")], [Cloud("a.txt")], []);
        Single<RemoveCloud>(plan);
        Assert.Equal(1, plan.CloudDeletions);
    }

    [Fact]
    public void A_change_beats_a_deletion_on_the_other_side()
    {
        // Changed in the cloud, deleted on the PC: the cloud's version comes back.
        Single<CreatePlaceholder>(Plan([Known(1, "a.txt")], [Cloud("a.txt", size: 11, ticks: T1)], []));
        // Changed on the PC, deleted in the cloud: the PC's version goes up again.
        var plan = Plan([Known(1, "a.txt")], [], [Placeholder(1, "a.txt", size: 12, ticks: T1, inSync: false, onDisk: true)]);
        Single<Upload>(plan);
        Assert.Equal(0, plan.Deletions);
    }

    [Fact]
    public void Gone_on_both_sides_is_only_forgotten() =>
        Single<Forget>(Plan([Known(1, "a.txt")], [], []));

    [Fact]
    public void A_rename_on_the_PC_moves_the_file_in_the_cloud()
    {
        var plan = Plan([Known(1, "alt.txt")], [Cloud("alt.txt")], [Placeholder(1, "neu.txt")]);
        var move = Single<MoveCloud>(plan);
        Assert.Equal(("alt.txt", "neu.txt"), (move.Item.Path, move.NewPath));
        Assert.Equal(0, plan.Deletions);
    }

    [Fact]
    public void A_renamed_folder_takes_its_content_along()
    {
        var plan = Plan(
            [Known(1, "Alt", dir: true), Known(2, "Alt/a.txt"), Known(3, "Alt/Unter", dir: true), Known(4, "Alt/Unter/b.txt")],
            [Cloud("Alt", dir: true), Cloud("Alt/a.txt"), Cloud("Alt/Unter", dir: true), Cloud("Alt/Unter/b.txt"), Cloud("Alt/neu-in-cloud.txt")],
            [Placeholder(1, "Neu", dir: true), Placeholder(2, "Neu/a.txt"), Placeholder(3, "Neu/Unter", dir: true), Placeholder(4, "Neu/Unter/b.txt")]);
        var move = Assert.IsType<MoveCloud>(plan.Actions[0]);
        Assert.Equal(("Alt", "Neu"), (move.Item.Path, move.NewPath));
        // A file the cloud got in the old folder meanwhile moves along and appears in the new one.
        Assert.Equal("Neu/neu-in-cloud.txt", Assert.IsType<CreatePlaceholder>(Assert.Single(plan.Actions.Skip(1))).Cloud.Path);
        Assert.Equal(0, plan.Deletions);
    }

    [Fact]
    public void A_rename_that_only_changes_the_case_moves_the_file()
    {
        var move = Single<MoveCloud>(Plan([Known(1, "bericht.docx")], [Cloud("bericht.docx")], [Placeholder(1, "Bericht.docx")]));
        Assert.Equal("Bericht.docx", move.NewPath);
    }

    [Fact]
    public void A_different_case_on_the_PC_alone_is_no_change() =>
        Assert.Empty(Plan([Known(1, "Ordner", dir: true)], [Cloud("Ordner", dir: true)], [Normal("ordner", dir: true)]).Actions);

    [Fact]
    public void A_file_moved_onto_a_place_taken_in_the_cloud_never_deletes_anything()
    {
        // X (online only) was moved to where Y was; Y was deleted on the PC before.
        var plan = Plan([Known(1, "x.txt"), Known(2, "y.txt")], [Cloud("x.txt"), Cloud("y.txt")], [Placeholder(1, "y.txt")]);
        Assert.Empty(plan.Actions);
        Assert.Equal(0, plan.Deletions);
        Assert.Equal(2, plan.Skipped.Count);
    }

    [Fact]
    public void A_first_run_merges_and_deletes_nothing()
    {
        var plan = Plan([], [Cloud("gleich.txt"), Cloud("anders.txt", size: 11), Cloud("nur-cloud.txt")],
            [Normal("gleich.txt"), Normal("anders.txt", size: 12), Normal("nur-pc.txt")], rebuild: true);
        Assert.Equal(["anders.txt", "gleich.txt", "nur-cloud.txt", "nur-pc.txt"], plan.Actions.Select(a => a.Path));
        Assert.IsType<Conflict>(plan.Actions[0]);
        Assert.IsType<Adopt>(plan.Actions[1]);
        Assert.IsType<CreatePlaceholder>(plan.Actions[2]);
        Assert.IsType<Upload>(plan.Actions[3]);
        Assert.Equal(0, plan.Deletions);
    }

    [Fact]
    public void A_rebuild_brings_back_instead_of_deleting()
    {
        var plan = Plan([Known(1, "weg-am-pc.txt"), Known(2, "weg-in-cloud.txt")], [Cloud("weg-am-pc.txt")],
            [Placeholder(2, "weg-in-cloud.txt", onDisk: true)], rebuild: true);
        Assert.IsType<CreatePlaceholder>(plan.Actions[0]);
        Assert.IsType<Upload>(plan.Actions[1]);
        Assert.Equal(0, plan.Deletions);
        Assert.Equal(0, plan.KnownFiles);
    }

    [Fact]
    public void A_placeholder_without_data_whose_file_is_gone_is_dropped() =>
        Single<DropPlaceholder>(Plan([], [], [Placeholder(9, "alt.txt")]));

    [Fact]
    public void A_file_on_one_side_and_a_folder_on_the_other_is_left_alone()
    {
        var plan = Plan([], [Cloud("Name", dir: true)], [Normal("Name")]);
        Assert.Empty(plan.Actions);
        Assert.Single(plan.Skipped);
    }

    [Theory]
    [InlineData(10, 6, 50, true)]
    [InlineData(10, 5, 50, false)]
    [InlineData(4, 3, 50, true)]
    [InlineData(4, 2, 50, false)]
    [InlineData(10, 10, 100, false)]
    public void Too_many_deletions_are_counted_in_files(int known, int deleted, int maxPercent, bool tooMany)
    {
        var items = Enumerable.Range(1, known).Select(i => Known(i, $"f{i}.txt")).ToArray();
        var cloud = items.Select(i => Cloud(i.Path)).ToArray();
        var local = items.Skip(deleted).Select(i => Placeholder(i.Id, i.Path)).ToArray();
        var plan = Plan(items, cloud, local);
        Assert.Equal(deleted, plan.CloudDeletions);
        Assert.Equal(tooMany, plan.TooManyDeletions(maxPercent, Sync.DeleteGuard.MinimumDeletions));
    }
}
