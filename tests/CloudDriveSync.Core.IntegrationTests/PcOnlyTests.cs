using System.Runtime.Versioning;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>
/// Synchronisations with files on demand on servers without folder times that pass changes on (IServ, WebDAV): a run
/// only a change on the PC asked for takes the cloud as it was last read instead of reading every folder - and stays as
/// safe as a run that reads it.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public class PcOnlyTests
{
    private const int Folders = 12;

    private static async Task<SyncWorld> WorldAsync(Action<SyncWorld>? cloud = null)
    {
        var world = await SyncWorld.CreateAsync(refusingProxy: true);
        for (var i = 1; i <= Folders; i++) world.WriteCloud($"Ordner {i}/a.txt", $"{i}");
        cloud?.Invoke(world);
        await world.AddAccountAsync();
        await world.AddPairAsync(p => p.Mode = SyncMode.OnDemand);
        var first = await world.RunAsync(BisyncMode.Resync);
        Assert.True(first.Success, $"{first.ErrorCode}: {first.ErrorDetail}");
        world.Proxy!.ResetCount();
        return world;
    }

    private static async Task<SyncRunOutcome> RunAsync(SyncWorld world, bool pcOnly)
    {
        var outcome = await world.RunAsync(pcOnly: pcOnly);
        Assert.True(outcome.Success, $"{outcome.ErrorCode}: {outcome.ErrorDetail}");
        return outcome;
    }

    private static string Fetch(SyncWorld world, string file)
    {
        Placeholders.Hydrate(world.Pc(file));
        return world.ReadPc(file);
    }

    private static List<string> CloudTexts(SyncWorld world, string prefix) =>
        world.CloudFiles().Where(f => f.StartsWith(prefix, StringComparison.Ordinal)).Select(world.ReadCloud).Order(StringComparer.Ordinal).ToList();

    /// <summary>The clock a while later.</summary>
    private sealed class LaterTime(TimeSpan ahead) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + ahead;
    }

    [Fact]
    public async Task A_change_on_the_PC_goes_up_without_reading_the_cloud()
    {
        await using var world = await WorldAsync();
        world.WritePc("Ordner 3/neu.txt", "vom PC");
        world.WriteCloud("Ordner 5/aus der Cloud.txt", "neu in der Cloud");

        await RunAsync(world, pcOnly: true);
        // Only the folder the new file goes into: the look before the upload, so nothing there is overwritten.
        Assert.All(world.Proxy!.Listed, folder => Assert.Equal($"{SyncWorld.CloudFolder}/Ordner 3", folder));
        Assert.Equal("vom PC", world.ReadCloud("Ordner 3/neu.txt"));
        Assert.True(Placeholders.Read(world.Pc("Ordner 3/neu.txt"))!.InSync);
        // What changed only in the cloud comes with the next run that reads it.
        Assert.False(File.Exists(world.Pc("Ordner 5/aus der Cloud.txt")));

        await RunAsync(world, pcOnly: false);
        Assert.True(world.Proxy.Listings > Folders, $"{world.Proxy.Listings} folders read");
        Assert.Equal("neu in der Cloud", Fetch(world, "Ordner 5/aus der Cloud.txt"));
    }

    [Fact]
    public async Task The_cloud_is_read_when_it_was_last_read_an_interval_ago()
    {
        await using var world = await WorldAsync();
        world.WritePc("neu.txt", "vom PC");
        world.WriteCloud("Ordner 5/aus der Cloud.txt", "neu in der Cloud");
        world.Host.Sync.Time = new LaterTime(TimeSpan.FromMinutes(world.Host.Sync.FindPair(world.Pair.Id)!.IntervalMinutes));

        await RunAsync(world, pcOnly: true);
        Assert.True(world.Proxy!.Listings > Folders, $"{world.Proxy.Listings} folders read");
        Assert.Equal("vom PC", world.ReadCloud("neu.txt"));
        Assert.True(File.Exists(world.Pc("Ordner 5/aus der Cloud.txt")));
    }

    [Fact]
    public async Task What_changed_in_the_cloud_meanwhile_is_neither_overwritten_nor_deleted()
    {
        await using var world = await WorldAsync(cloud: w =>
        {
            w.WriteCloud("Bericht.txt", "Fassung 1");
            w.WriteCloud("Liste.txt", "alt");
        });
        Fetch(world, "Bericht.txt");
        world.WritePc("Bericht.txt", "vom PC geändert", later: true);
        File.Delete(world.Pc("Liste.txt"));
        world.WriteCloud("Bericht.txt", "in der Cloud geändert", later: true);
        world.WriteCloud("Liste.txt", "in der Cloud ergänzt", later: true);

        await RunAsync(world, pcOnly: true);
        // The run found the cloud changed and read it at once: both versions kept, the list back on the PC.
        Assert.True(world.Proxy!.Listings > Folders, $"{world.Proxy.Listings} folders read");
        Assert.Equal(["in der Cloud geändert", "vom PC geändert"], CloudTexts(world, "Bericht"));
        Assert.Equal("in der Cloud ergänzt", world.ReadCloud("Liste.txt"));
        Assert.Equal("in der Cloud ergänzt", Fetch(world, "Liste.txt"));
    }

    [Fact]
    public async Task A_file_renamed_on_the_PC_never_replaces_one_that_took_its_new_name_in_the_cloud()
    {
        await using var world = await WorldAsync(cloud: w => w.WriteCloud("Entwurf.txt", "vom PC umbenannt"));
        Fetch(world, "Entwurf.txt");
        File.Move(world.Pc("Entwurf.txt"), world.Pc("Endfassung.txt"));
        world.WriteCloud("Endfassung.txt", "von jemand anderem");

        await RunAsync(world, pcOnly: true);
        var texts = CloudTexts(world, "E");
        Assert.Contains("von jemand anderem", texts);
        Assert.Contains("vom PC umbenannt", texts);
    }

    [Fact]
    public async Task Without_the_protection_file_in_the_cloud_nothing_runs_also_when_only_the_PC_changed()
    {
        await using var world = await WorldAsync();
        world.DeleteCloud(SyncFilters.SentinelFile);
        world.WritePc("neu.txt", "vom PC");

        var stopped = await world.RunAsync(pcOnly: true);
        Assert.Equal("CD-4503", stopped.ErrorCode);
        Assert.False(File.Exists(world.Cloud("neu.txt")));
    }

    [Fact]
    public async Task A_folder_left_out_of_the_selection_since_is_never_taken_as_deleted()
    {
        await using var world = await WorldAsync();
        // The selection changed (the rebuild it asks for has not run yet), then the folder went from the PC.
        world.Host.Sync.Update(world.Pair.Id, p => p.Selection.Exclude.Add("/Ordner 1/**"));
        Directory.Delete(world.Pc("Ordner 1"), recursive: true);
        world.WritePc("neu.txt", "vom PC");

        await RunAsync(world, pcOnly: true);
        Assert.Equal("1", world.ReadCloud("Ordner 1/a.txt"));
        Assert.Equal("vom PC", world.ReadCloud("neu.txt"));
    }
}
