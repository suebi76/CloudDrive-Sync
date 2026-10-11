using System.Runtime.Versioning;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>
/// Several PCs with the same account and cloud folder - a school laptop and a PC at home: each keeps its own state, the
/// server is what they share. Whatever one PC does, the other one must neither stop nor lose anything.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public class MultiDeviceTests
{
    private static async Task SetUpAsync(SyncWorld pc, SyncMode mode)
    {
        await pc.AddAccountAsync();
        await pc.AddPairAsync(p => p.Mode = mode);
        var first = await pc.RunAsync(BisyncMode.Resync);
        Assert.True(first.Success, $"{first.ErrorCode}: {first.ErrorDetail}");
        // Tests delete what is to leave the PC instead of filling the recycle bin of the PC they run on.
        pc.Host.Sync.Recycle = files =>
        {
            foreach (var file in files) File.Delete(file);
            return files.Count;
        };
    }

    private static async Task<SyncRunOutcome> RunAsync(SyncWorld pc)
    {
        var outcome = await pc.RunAsync();
        Assert.True(outcome.Success, $"{outcome.ErrorCode}: {outcome.ErrorDetail}");
        return outcome;
    }

    /// <summary>The content of a file on a PC - fetched first when it is online only.</summary>
    private static string Read(SyncWorld pc, string file)
    {
        if (Placeholders.Read(pc.Pc(file)) is { IsFullyOnDisk: false }) Placeholders.Hydrate(pc.Pc(file));
        return pc.ReadPc(file);
    }

    /// <summary>Changes a file on a PC as a user would - an online-only one is fetched first.</summary>
    private static void Change(SyncWorld pc, string file, string text)
    {
        if (File.Exists(pc.Pc(file)) && Placeholders.Read(pc.Pc(file)) is { IsFullyOnDisk: false }) Placeholders.Hydrate(pc.Pc(file));
        pc.WritePc(file, text, later: true);
    }

    private static IEnumerable<string> Synchronised(IEnumerable<string> files) =>
        files.Where(f => !f.StartsWith(".clouddrive", StringComparison.Ordinal));

    /// <summary>
    /// The only file of CloudDrive-Sync's own in the cloud is the protection file both PCs share - never a recycle bin, a
    /// state, a lock or a note of one PC - and it is still the same as when the first PC put it there.
    /// </summary>
    private static void AssertOnlyTheSharedProtectionFile(SyncWorld world, byte[] protection)
    {
        var root = Path.Combine(world.CloudRoot, SyncWorld.CloudFolder);
        var own = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(entry => Path.GetRelativePath(root, entry).Replace('\\', '/'))
            .Where(entry => entry.Split('/').Any(part => part.StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal([SyncFilters.SentinelFile], own);
        Assert.Equal(protection, File.ReadAllBytes(world.Cloud(SyncFilters.SentinelFile)));
    }

    /// <summary>Both PCs have exactly what the cloud has, and a further run on each changes nothing.</summary>
    private static async Task AssertSettledAsync(SyncWorld school, SyncWorld home)
    {
        // The protection file as every PC writes it (always the same text).
        var protection = File.ReadAllBytes(school.Pc(SyncFilters.SentinelFile));
        foreach (var pc in new[] { school, home })
        {
            var again = await RunAsync(pc);
            Assert.Empty(again.Changes ?? []);
        }
        var cloud = Synchronised(school.CloudFiles()).ToList();
        Assert.Equal(cloud, Synchronised(school.PcFiles()));
        Assert.Equal(cloud, Synchronised(home.PcFiles()));
        Assert.DoesNotContain(cloud, f => f.Contains(".Konflikt-", StringComparison.Ordinal));
        AssertOnlyTheSharedProtectionFile(school, protection);
    }

    [Theory]
    [InlineData(SyncMode.Classic, SyncMode.Classic)]
    [InlineData(SyncMode.OnDemand, SyncMode.OnDemand)]
    [InlineData(SyncMode.Classic, SyncMode.OnDemand)]
    public async Task Two_PCs_keep_the_same_folder_in_step(SyncMode first, SyncMode second)
    {
        await using var school = await SyncWorld.CreateAsync();
        school.WriteCloud("Plan.txt", "Stundenplan");
        await SetUpAsync(school, first);
        await using var home = await school.SecondPcAsync();
        await SetUpAsync(home, second);
        Assert.Equal("Stundenplan", Read(home, "Plan.txt"));

        // New at school: at home after both ran.
        school.WritePc("Von der Schule.txt", "Arbeitsblatt");
        await RunAsync(school);
        await RunAsync(home);
        Assert.Equal("Arbeitsblatt", Read(home, "Von der Schule.txt"));

        // Changed at home: at school.
        Change(home, "Plan.txt", "Stundenplan neu");
        await RunAsync(home);
        await RunAsync(school);
        Assert.Equal("Stundenplan neu", Read(school, "Plan.txt"));

        // Deleted at home: gone at school, too - and in the cloud.
        File.Delete(home.Pc("Von der Schule.txt"));
        await RunAsync(home);
        await RunAsync(school);
        Assert.False(File.Exists(school.Pc("Von der Schule.txt")));
        Assert.Equal(["Plan.txt"], Synchronised(school.CloudFiles()));
        Assert.Equal(["Plan.txt"], Synchronised(school.PcFiles()));
        Assert.Equal(["Plan.txt"], Synchronised(home.PcFiles()));
    }

    [Theory]
    [InlineData(SyncMode.Classic)]
    [InlineData(SyncMode.OnDemand)]
    public async Task Two_PCs_with_Nextcloud_keep_the_same_folder_in_step(SyncMode mode)
    {
        await using var school = await SyncWorld.CreateAsync(WebDavKind.Nextcloud);
        school.WriteCloud("Plan.txt", "Stundenplan");
        await SetUpAsync(school, mode);
        await using var home = await school.SecondPcAsync();
        await SetUpAsync(home, mode);

        school.WritePc("Neu/Von der Schule.txt", "Arbeitsblatt");
        await RunAsync(school);
        await RunAsync(home);
        Assert.Equal("Arbeitsblatt", Read(home, "Neu/Von der Schule.txt"));
        Change(home, "Plan.txt", "Stundenplan neu");
        await RunAsync(home);
        await RunAsync(school);
        Assert.Equal("Stundenplan neu", Read(school, "Plan.txt"));
        await AssertSettledAsync(school, home);
    }

    [Theory]
    [InlineData(SyncMode.Classic, KeepOnPc.OnPc)]
    [InlineData(SyncMode.Classic, KeepOnPc.Nothing)]
    [InlineData(SyncMode.OnDemand, KeepOnPc.OnPc)]
    [InlineData(SyncMode.OnDemand, KeepOnPc.Everything)]
    [InlineData(SyncMode.OnDemand, KeepOnPc.Nothing)]
    public async Task Ending_on_one_PC_in_any_way_never_touches_the_cloud_or_the_other(SyncMode mode, KeepOnPc keep)
    {
        await using var school = await SyncWorld.CreateAsync();
        school.WriteCloud("Plan.txt", "Stundenplan");
        school.WriteCloud("Ordner/Liste.txt", "Liste");
        await SetUpAsync(school, mode);
        await using var home = await school.SecondPcAsync();
        await SetUpAsync(home, mode);

        await school.Host.Sync.RemoveAsync(school.Pair.Id, keep);
        Assert.Equal(["Ordner/Liste.txt", "Plan.txt"], Synchronised(school.CloudFiles()));
        Assert.True(File.Exists(school.Cloud(SyncFilters.SentinelFile)));

        home.WritePc("Noch zu Hause.txt", "weiter");
        var outcome = await RunAsync(home);
        Assert.DoesNotContain(outcome.Changes ?? [], c => c.Kind != ChangeKind.Uploaded);
        Assert.Equal("weiter", home.ReadCloud("Noch zu Hause.txt"));
        Assert.Equal("Liste", Read(home, "Ordner/Liste.txt"));
    }

    [Theory]
    [InlineData(SyncMode.Classic)]
    [InlineData(SyncMode.OnDemand)]
    public async Task Removing_the_account_on_one_PC_never_stops_the_other(SyncMode mode)
    {
        await using var school = await SyncWorld.CreateAsync();
        school.WriteCloud("Plan.txt", "Stundenplan");
        await SetUpAsync(school, mode);
        await using var home = await school.SecondPcAsync();
        await SetUpAsync(home, mode);

        await school.Host.Sync.RemoveAsync(school.Pair.Id, KeepOnPc.OnPc);
        await school.Host.Accounts.RemoveAsync(school.Account.Id);

        Change(home, "Plan.txt", "weiter zu Hause");
        await RunAsync(home);
        Assert.Equal("weiter zu Hause", home.ReadCloud("Plan.txt"));
    }

    [Theory]
    [InlineData(SyncMode.Classic)]
    [InlineData(SyncMode.OnDemand)]
    public async Task A_rebuild_or_a_switch_on_one_PC_changes_nothing_for_the_other(SyncMode homeMode)
    {
        await using var school = await SyncWorld.CreateAsync();
        school.WriteCloud("Plan.txt", "Stundenplan");
        school.WriteCloud("Ordner/Liste.txt", "Liste");
        await SetUpAsync(school, SyncMode.Classic);
        await using var home = await school.SecondPcAsync();
        await SetUpAsync(home, homeMode);

        Assert.True((await school.RunAsync(BisyncMode.Resync)).Success);
        await school.Host.Sync.ConvertToOnDemandAsync(school.Pair.Id);
        Assert.True((await school.RunAsync()).Success);
        await school.Host.Sync.ConvertToClassicAsync(school.Pair.Id);
        // The first classic run after switching back merges both sides (the service asks for it, ResyncPending).
        Assert.True((await school.RunAsync(BisyncMode.Resync)).Success);

        Assert.Equal(["Ordner/Liste.txt", "Plan.txt"], Synchronised(school.CloudFiles()));
        await AssertSettledAsync(school, home);
    }

    /// <summary>
    /// Classic synchronisations cannot tell this apart: bisync does not look whether the cloud got a file meanwhile before it
    /// uploads. Two PCs saving a new file of the same name at the same moment: the one uploaded last is in the cloud, the
    /// other one in the recycle bin of its PC (CloudDrive-Sync's own). With files on demand both stay side by side.
    /// </summary>
    [Theory]
    [InlineData(SyncMode.Classic)]
    [InlineData(SyncMode.OnDemand)]
    public async Task Both_PCs_running_at_the_same_moment_lose_nothing(SyncMode mode)
    {
        await using var school = await SyncWorld.CreateAsync();
        school.WriteCloud("Plan.txt", "Stundenplan");
        await SetUpAsync(school, mode);
        await using var home = await school.SecondPcAsync();
        await SetUpAsync(home, mode);

        school.WritePc("Aus der Schule.txt", "Schule");
        home.WritePc("Von zu Hause.txt", "Zuhause");
        school.WritePc("Gleicher Name.txt", "in der Schule geschrieben");
        home.WritePc("Gleicher Name.txt", "zu Hause geschrieben, etwas länger");
        await Task.WhenAll(school.RunAsync(), home.RunAsync());
        // Whatever the two runs at the same moment did: a few more and everything is everywhere, nothing lost.
        for (var round = 0; round < 2; round++)
        {
            await school.RunAsync();
            await home.RunAsync();
        }
        var texts = Synchronised(school.CloudFiles()).Select(f => school.ReadCloud(f)).Order().ToList();
        Assert.Contains("Schule", texts);
        Assert.Contains("Zuhause", texts);
        if (mode == SyncMode.OnDemand)
        {
            Assert.Contains("in der Schule geschrieben", texts);
            Assert.Contains("zu Hause geschrieben, etwas länger", texts);
        }
        else
        {
            var kept = texts.Concat(school.PcTrash()).Concat(home.PcTrash()).ToList();
            Assert.Contains("in der Schule geschrieben", kept);
            Assert.Contains("zu Hause geschrieben, etwas länger", kept);
        }
        Assert.Equal(Synchronised(school.CloudFiles()), Synchronised(school.PcFiles()));
        Assert.Equal(Synchronised(school.CloudFiles()), Synchronised(home.PcFiles()));
    }

    [Theory]
    [InlineData(SyncMode.Classic)]
    [InlineData(SyncMode.OnDemand)]
    public async Task A_second_PC_whose_folder_has_copies_already_takes_them_without_conflicts(SyncMode mode)
    {
        await using var school = await SyncWorld.CreateAsync();
        school.WriteCloud("Plan.txt", "Stundenplan");
        school.WriteCloud("Ordner/Liste.txt", "Liste");
        await SetUpAsync(school, mode);
        await using var home = await school.SecondPcAsync();
        // Copied over by hand before (e.g. with a USB stick): the same files.
        home.WritePc("Plan.txt", "Stundenplan");
        home.WritePc("Ordner/Liste.txt", "Liste");
        File.SetLastWriteTimeUtc(home.Pc("Plan.txt"), File.GetLastWriteTimeUtc(school.Cloud("Plan.txt")));
        File.SetLastWriteTimeUtc(home.Pc("Ordner/Liste.txt"), File.GetLastWriteTimeUtc(school.Cloud("Ordner/Liste.txt")));
        await SetUpAsync(home, mode);
        await RunAsync(school);

        Assert.Equal("Stundenplan", Read(home, "Plan.txt"));
        await AssertSettledAsync(school, home);
    }

    [Theory]
    [InlineData(SyncMode.Classic)]
    [InlineData(SyncMode.OnDemand)]
    public async Task A_folder_renamed_on_one_PC_arrives_renamed_on_the_other(SyncMode mode)
    {
        await using var school = await SyncWorld.CreateAsync();
        for (var i = 1; i <= 5; i++) school.WriteCloud($"Unterricht/Stunde {i}.txt", $"Stunde {i}");
        for (var i = 1; i <= 10; i++) school.WriteCloud($"Sonstiges/{i}.txt", $"{i}");
        await SetUpAsync(school, mode);
        await using var home = await school.SecondPcAsync();
        await SetUpAsync(home, mode);
        Assert.Equal("Stunde 1", Read(home, "Unterricht/Stunde 1.txt"));

        Directory.Move(school.Pc("Unterricht"), school.Pc("Unterricht 7b"));
        await RunAsync(school);
        await RunAsync(home);
        Assert.False(Directory.Exists(home.Pc("Unterricht")));
        Assert.Equal("Stunde 1", Read(home, "Unterricht 7b/Stunde 1.txt"));
        await AssertSettledAsync(school, home);
    }

    [Fact]
    public async Task A_large_folder_renamed_on_one_PC_is_no_mass_deletion_on_the_other()
    {
        // Files on demand on both: 150 files renamed at school are moved at home - no question about deletions, and what
        // home had on the PC stays there (nothing fetched again, nothing in the recycle bin).
        await using var school = await SyncWorld.CreateAsync();
        for (var i = 1; i <= 150; i++) school.WriteCloud($"Unterricht/Stunde {i:000}.txt", $"Stunde {i}");
        await SetUpAsync(school, SyncMode.OnDemand);
        await using var home = await school.SecondPcAsync();
        await SetUpAsync(home, SyncMode.OnDemand);
        Assert.Equal("Stunde 1", Read(home, "Unterricht/Stunde 001.txt"));

        Directory.Move(school.Pc("Unterricht"), school.Pc("Unterricht 7b"));
        await RunAsync(school);
        await RunAsync(home);
        Assert.False(Directory.Exists(home.Pc("Unterricht")));
        Assert.True(Placeholders.Read(home.Pc("Unterricht 7b/Stunde 001.txt"))!.IsFullyOnDisk);
        Assert.True(Placeholders.Read(home.Pc("Unterricht 7b/Stunde 001.txt"))!.InSync);
        Assert.True(Placeholders.Read(home.Pc("Unterricht 7b/Stunde 150.txt"))!.InSync);
        Assert.Equal("Stunde 1", home.ReadPc("Unterricht 7b/Stunde 001.txt"));
        Assert.Empty(home.PcTrash());
        await AssertSettledAsync(school, home);
    }

    [Theory]
    [InlineData(SyncMode.Classic)]
    [InlineData(SyncMode.OnDemand)]
    public async Task Changes_on_both_PCs_before_either_ran_keep_both_versions(SyncMode mode)
    {
        await using var school = await SyncWorld.CreateAsync();
        school.WriteCloud("Plan.txt", "Stundenplan");
        await SetUpAsync(school, mode);
        await using var home = await school.SecondPcAsync();
        await SetUpAsync(home, mode);

        Change(school, "Plan.txt", "aus der Schule");
        Change(home, "Plan.txt", "von zu Hause, länger");
        await RunAsync(school);
        await RunAsync(home);
        await RunAsync(school);

        // Nothing is lost: both versions are there, on both PCs and in the cloud.
        var texts = Synchronised(school.CloudFiles()).Select(f => school.ReadCloud(f)).Order().ToList();
        Assert.Equal(["aus der Schule", "von zu Hause, länger"], texts);
        Assert.Equal(Synchronised(school.CloudFiles()), Synchronised(school.PcFiles()));
        Assert.Equal(Synchronised(school.CloudFiles()), Synchronised(home.PcFiles()));
    }
}
