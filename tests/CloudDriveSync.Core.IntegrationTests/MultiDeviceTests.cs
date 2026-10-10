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
    }

    private static async Task RunAsync(SyncWorld pc)
    {
        var outcome = await pc.RunAsync();
        Assert.True(outcome.Success, $"{outcome.ErrorCode}: {outcome.ErrorDetail}");
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
    public async Task Ending_on_one_PC_never_stops_the_other(SyncMode mode)
    {
        await using var school = await SyncWorld.CreateAsync();
        school.WriteCloud("Plan.txt", "Stundenplan");
        await SetUpAsync(school, mode);
        await using var home = await school.SecondPcAsync();
        await SetUpAsync(home, mode);

        await school.Host.Sync.RemoveAsync(school.Pair.Id, KeepOnPc.OnPc);

        home.WritePc("Noch zu Hause.txt", "weiter");
        await RunAsync(home);
        Assert.Equal("weiter", home.ReadCloud("Noch zu Hause.txt"));
        Assert.Equal("Stundenplan", home.ReadCloud("Plan.txt"));
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
