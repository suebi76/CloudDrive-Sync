using System.Diagnostics;
using System.Runtime.Versioning;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;
using Xunit.Abstractions;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>
/// Large cloud folders and a server that answers slowly, like a Nextcloud far away: how much of the cloud a run reads
/// again. Counted in folder listings, so the verdict does not depend on how fast the PC is; the time is reported, too.
/// The test server passes changes on to the folder times above them, as Nextcloud does (see <see cref="SyncWorld"/>).
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public class LargeTreeTests(ITestOutputHelper output)
{
    private const int Folders = 200;

    /// <summary>The clock of a later moment, for the service's own clock.</summary>
    private sealed class Later(TimeSpan ahead) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + ahead;
    }

    /// <summary>
    /// A cloud folder with 200 folders and 600 files, synchronised with files on demand, run twice more a minute apart
    /// each: the folders of the newest change are settled, every later run sees the cloud as it is.
    /// </summary>
    private static async Task<SyncWorld> LargeAsync()
    {
        var world = await SyncWorld.CreateAsync(WebDavKind.Nextcloud);
        for (var i = 0; i < Folders; i++)
            for (var j = 0; j < 3; j++) world.WriteCloud($"Ordner {i / 20}/Unterordner {i}/Datei {j}.txt", $"{i}-{j}");
        await world.AddAccountAsync();
        await world.AddPairAsync(p => p.Mode = SyncMode.OnDemand);
        var first = await world.RunAsync(BisyncMode.Resync);
        Assert.True(first.Success, $"{first.ErrorCode}: {first.ErrorDetail}");
        for (var minute = 1; minute <= 2; minute++)
        {
            world.Host.Sync.Time = new Later(TimeSpan.FromMinutes(minute));
            var run = await world.RunAsync();
            Assert.True(run.Success, $"{run.ErrorCode}: {run.ErrorDetail}");
        }
        world.Host.Sync.Time = new Later(TimeSpan.FromMinutes(3));
        world.Proxy!.Delay = TimeSpan.FromMilliseconds(30);
        world.Proxy.ResetCount();
        return world;
    }

    private async Task<int> MeasuredRunAsync(SyncWorld world, string what)
    {
        var clock = Stopwatch.StartNew();
        var run = await world.RunAsync();
        clock.Stop();
        Assert.True(run.Success, $"{run.ErrorCode}: {run.ErrorDetail}");
        var listings = world.Proxy!.Listings;
        output.WriteLine($"[measure] {what}: {listings} folder listings, {clock.Elapsed.TotalSeconds:0.00} s ({Folders + Folders / 20 + 1} folders, 30 ms per answer)");
        return listings;
    }

    /// <summary>
    /// What a run may read after changes in some of the 10 top folders: the parent (for the folder's own time) and the
    /// folder itself, the top folders, and in each changed top folder its 20 sub-folders. A folder beside the way is read
    /// once to make sure it is the same - its time alone cannot tell when other folders have the same time (folders made
    /// in the same second) - but nothing below it.
    /// </summary>
    private static int WayAndBeside(int changedTopFolders) => 2 + Folders / 20 + changedTopFolders * 20;

    /// <summary>The same names on the PC as in the cloud - placeholders, nothing fetched.</summary>
    private static void AssertSameNames(SyncWorld world)
    {
        Assert.Equal(world.CloudFolders(), world.PcFolders());
        Assert.Equal(world.CloudFiles(), world.PcFiles());
    }

    [Fact]
    public async Task A_run_without_changes_does_not_read_every_folder_again()
    {
        await using var world = await LargeAsync();
        var listings = await MeasuredRunAsync(world, "run without changes");
        // The parent, for the folder's own time - and the folder itself at most.
        Assert.True(listings <= 2, $"a run without changes read {listings} folders");
        AssertSameNames(world);
    }

    [Fact]
    public async Task A_change_deep_down_is_found_and_only_its_way_and_the_folders_beside_it_are_read()
    {
        await using var world = await LargeAsync();
        world.WriteCloud("Ordner 3/Unterordner 70/neu.txt", "neu");
        var listings = await MeasuredRunAsync(world, "new file deep down");
        Assert.True(File.Exists(world.Pc("Ordner 3/Unterordner 70/neu.txt")));
        Assert.True(listings <= WayAndBeside(1), $"a change deep down read {listings} folders");
        AssertSameNames(world);
    }

    [Fact]
    public async Task A_deletion_and_a_renamed_folder_deep_down_are_found()
    {
        await using var world = await LargeAsync();
        world.DeleteCloud("Ordner 5/Unterordner 101/Datei 1.txt");
        Directory.Move(world.Cloud("Ordner 8/Unterordner 170"), world.Cloud("Ordner 8/Umbenannt"));
        world.WriteCloud("Ordner 8/Umbenannt/Datei 0.txt", "170-0");
        var listings = await MeasuredRunAsync(world, "deletion and renamed folder");
        Assert.False(File.Exists(world.Pc("Ordner 5/Unterordner 101/Datei 1.txt")));
        Assert.True(File.Exists(world.Pc("Ordner 8/Umbenannt/Datei 2.txt")));
        Assert.False(Directory.Exists(world.Pc("Ordner 8/Unterordner 170")));
        Assert.True(listings <= WayAndBeside(2), $"a deletion and a renamed folder read {listings} folders");
        AssertSameNames(world);
    }

    [Fact]
    public async Task Once_an_hour_the_whole_cloud_folder_is_read()
    {
        await using var world = await LargeAsync();
        world.Host.Sync.Time = new Later(TimeSpan.FromMinutes(3) + OnDemand.OnDemandRunner.FullListingEvery);
        var listings = await MeasuredRunAsync(world, "run an hour later");
        Assert.True(listings >= Folders, $"an hour later only {listings} folders were read");
    }
}
