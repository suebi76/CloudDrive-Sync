using System.Runtime.Versioning;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.OnDemand;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>Switching a synchronisation between "all files on this PC" (classic) and files on demand, both ways.</summary>
[SupportedOSPlatform("windows10.0.17763")]
public class ConversionTests
{
    private static async Task<SyncWorld> ClassicWorldAsync()
    {
        var world = await SyncWorld.CreateAsync();
        world.WriteCloud("Bericht.txt", "aus der Cloud");
        world.WriteCloud("Ordner/b.txt", "zwei");
        world.WritePc("Vom PC.txt", "vom PC");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        var first = await world.RunAsync(BisyncMode.Resync);
        Assert.True(first.Success, $"{first.ErrorCode}: {first.ErrorDetail}");
        return world;
    }

    [Fact]
    public async Task A_classic_synchronisation_becomes_files_on_demand_without_any_transfer()
    {
        await using var world = await ClassicWorldAsync();
        // Created anew, a file would get a new creation time: the same times show it was converted where it lies.
        var before = world.PcFiles().ToDictionary(f => f, f => File.GetCreationTimeUtc(world.Pc(f)));

        await world.Host.Sync.ConvertToOnDemandAsync(world.Pair.Id);

        Assert.Equal(SyncMode.OnDemand, world.Host.Sync.FindPair(world.Pair.Id)!.Mode);
        Assert.Equal(before.Keys, world.PcFiles());
        foreach (var file in world.PcFiles())
        {
            var info = Placeholders.Read(world.Pc(file));
            Assert.NotNull(info);
            Assert.True(info.InSync && info.IsFullyOnDisk, $"not converted in place: {file}");
            Assert.Equal(before[file], File.GetCreationTimeUtc(world.Pc(file)));
        }
        Assert.Equal("aus der Cloud", world.ReadPc("Bericht.txt"));
        Assert.False(Directory.Exists(Path.Combine(world.Host.Paths.SyncPairDir(world.Pair.Id), "bisync")));

        // From now on it is a synchronisation with files on demand.
        world.WriteCloud("Ordner/b.txt", "in der Cloud geändert", later: true);
        Placeholders.SetPinState(world.Pc("Bericht.txt"), PinState.Unpinned, recurse: false);
        var run = await world.RunAsync();
        Assert.True(run.Success, $"{run.ErrorCode}: {run.ErrorDetail}");
        Assert.Equal("in der Cloud geändert", world.ReadPc("Ordner/b.txt"));
        Assert.Equal(0, Placeholders.Read(world.Pc("Bericht.txt"))!.OnDiskSize);
        Assert.Equal("aus der Cloud", world.ReadCloud("Bericht.txt"));
    }

    [Fact]
    public async Task Files_on_demand_go_back_to_all_files_on_this_PC_and_nothing_is_deleted()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("a.txt", "eins");
        world.WriteCloud("Ordner/b.txt", "zwei");
        await world.AddAccountAsync();
        await world.AddPairAsync(p => p.Mode = SyncMode.OnDemand);
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);
        var id = SyncRoots.IdFor(world.Host.Paths, world.Pair.Id);
        world.WritePc("Neu am PC.txt", "neu");

        await world.Host.Sync.ConvertToClassicAsync(world.Pair.Id);

        Assert.Equal(SyncMode.Classic, world.Host.Sync.FindPair(world.Pair.Id)!.Mode);
        Assert.False(SyncRoots.IsRegistered(id));
        foreach (var file in world.PcFiles()) Assert.Null(Placeholders.Read(world.Pc(file)));
        Assert.Equal("zwei", world.ReadPc("Ordner/b.txt"));
        // What was changed on the PC went up before.
        Assert.Equal("neu", world.ReadCloud("Neu am PC.txt"));
        Assert.False(File.Exists(Path.Combine(world.Host.Paths.SyncPairDir(world.Pair.Id), "items.db")));
        Assert.True(PersistedSyncState.Load(Path.Combine(world.Host.Paths.SyncPairDir(world.Pair.Id), "state.json")).ResyncPending);

        // The first classic run merges both sides.
        var first = await world.RunAsync(BisyncMode.Resync);
        Assert.True(first.Success, $"{first.ErrorCode}: {first.ErrorDetail}");
        Assert.Equal(0, first.Deletes);
        world.AssertInStep();
    }

    // --- Which files count as the same when switching (OnDemandRunner.InStep).
    private static readonly DateTime Noted = new(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);

    private static HashSet<string> InStep(long pcSize, DateTime pcTime, long cloudSize, DateTime cloudTime)
    {
        var before = new BisyncRecord(
            new Dictionary<string, NotedFile> { ["a.txt"] = new(10, Noted) },
            new Dictionary<string, NotedFile> { ["a.txt"] = new(10, Noted) });
        var cloud = new Dictionary<string, CloudEntry> { ["a.txt"] = new("a.txt", false, cloudSize, cloudTime.Ticks, null) };
        return OnDemandRunner.InStep(before, cloud, [new LocalEntry("a.txt", false, pcSize, pcTime.Ticks, null)]);
    }

    [Fact]
    public void A_file_neither_side_changed_counts_as_the_same() =>
        Assert.Contains("a.txt", InStep(10, Noted.AddMilliseconds(400), 10, Noted));

    [Fact]
    public void A_file_changed_on_the_PC_since_the_last_run_does_not() =>
        Assert.Empty(InStep(10, Noted.AddMinutes(1), 10, Noted));

    [Fact]
    public void A_file_changed_in_the_cloud_with_the_same_size_does_not() =>
        Assert.Empty(InStep(10, Noted, 10, Noted.AddMinutes(1)));

    [Fact]
    public void A_file_of_another_size_does_not() =>
        Assert.Empty(InStep(10, Noted, 11, Noted));
}
