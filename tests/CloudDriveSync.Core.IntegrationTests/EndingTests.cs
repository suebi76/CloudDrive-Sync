using System.Runtime.Versioning;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>Ending a synchronisation and what stays on the PC (<see cref="KeepOnPc"/>); in the cloud everything always stays.</summary>
[SupportedOSPlatform("windows10.0.17763")]
public class EndingTests
{
    private static async Task<SyncWorld> WorldAsync(SyncMode mode)
    {
        var world = await SyncWorld.CreateAsync();
        world.WriteCloud("geholt.txt", "geholt");
        world.WriteCloud("Ordner/online.txt", "nur online");
        await world.AddAccountAsync();
        await world.AddPairAsync(p => p.Mode = mode);
        var first = await world.RunAsync(BisyncMode.Resync);
        Assert.True(first.Success, $"{first.ErrorCode}: {first.ErrorDetail}");
        if (mode == SyncMode.OnDemand) Placeholders.Hydrate(world.Pc("geholt.txt"));
        // Tests delete what is to leave the PC instead of filling the recycle bin of the PC they run on.
        world.Host.Sync.Recycle = files =>
        {
            foreach (var file in files) File.Delete(file);
            return files.Count;
        };
        return world;
    }

    [Fact]
    public async Task Keeping_everything_fetches_every_file_first()
    {
        await using var world = await WorldAsync(SyncMode.OnDemand);
        await world.Host.Sync.RemoveAsync(world.Pair.Id, KeepOnPc.Everything);
        Assert.Equal(0, Leftovers.Count(world.Local));
        Assert.Equal("nur online", world.ReadPc("Ordner/online.txt"));
        Assert.Equal("geholt", world.ReadPc("geholt.txt"));
        Assert.Null(Placeholders.Read(world.Pc("Ordner/online.txt")));
    }

    [Theory]
    [InlineData(SyncMode.OnDemand)]
    [InlineData(SyncMode.Classic)]
    public async Task Keeping_nothing_uploads_first_and_never_removes_what_is_only_on_the_PC_unasked(SyncMode mode)
    {
        await using var world = await WorldAsync(mode);
        world.WritePc("Neu am PC.txt", "neu");
        // Office's lock file is never synchronised: it exists only on the PC.
        world.WritePc("~$Entwurf.docx", "Sperrdatei");
        // A copy an earlier run kept in the folder's trash.
        world.WritePc($"{SyncFilters.TrashFolder}/2026-10-01 12-00-00/alt.txt", "alt");
        var result = await world.Host.Sync.RemoveAsync(world.Pair.Id, KeepOnPc.Nothing);
        Assert.Equal("neu", world.ReadCloud("Neu am PC.txt"));
        Assert.Equal(["~$Entwurf.docx"], result.Stayed);
        Assert.Equal(["~$Entwurf.docx"], world.PcFiles());
        Assert.Equal(0, Leftovers.Count(world.Local));
        Assert.Equal(["Neu am PC.txt", "Ordner/online.txt", "geholt.txt"], world.CloudFiles().Where(f => !f.StartsWith(".clouddrive", StringComparison.Ordinal)));

        // Asked, what stayed goes, too - and the folder with it.
        var rest = await world.Host.Sync.RecycleRestAsync(world.Local);
        Assert.Equal(1, rest.Recycled);
        Assert.Empty(rest.Stayed);
        Assert.False(Directory.Exists(world.Local));
        Assert.Equal("neu", world.ReadCloud("Neu am PC.txt"));
    }

    [Fact]
    public async Task Keeping_everything_stops_at_a_folder_Windows_refuses_and_nothing_changes()
    {
        await using var world = await WorldAsync(SyncMode.OnDemand);
        using (RefusedFolder.Hold(world.Pc("Ordner")))
        {
            var error = await Assert.ThrowsAsync<CdException>(() => world.Host.Sync.RemoveAsync(world.Pair.Id, KeepOnPc.Everything));
            Assert.Equal("CD-4609", error.Code);
        }
        Assert.NotNull(world.Host.Sync.FindPair(world.Pair.Id));
        Placeholders.Hydrate(world.Pc("Ordner/online.txt"));
        Assert.Equal("nur online", world.ReadPc("Ordner/online.txt"));
    }

    [Fact]
    public async Task The_rest_goes_around_a_folder_Windows_refuses_and_that_folder_is_named()
    {
        await using var world = await WorldAsync(SyncMode.Classic);
        await world.Host.Sync.RemoveAsync(world.Pair.Id, KeepOnPc.OnPc);
        EndResult rest;
        using (RefusedFolder.Hold(world.Pc("Ordner")))
            rest = await world.Host.Sync.RecycleRestAsync(world.Local);
        Assert.Contains("Ordner" + Path.DirectorySeparatorChar, rest.Stayed);
        Assert.False(File.Exists(world.Pc("geholt.txt")));
        Assert.Equal("nur online", world.ReadPc("Ordner/online.txt"));

        // Free again: the rest goes, and the folder with it.
        await world.Host.Sync.RecycleRestAsync(world.Local);
        Assert.False(Directory.Exists(world.Local));
        Assert.Equal("nur online", world.ReadCloud("Ordner/online.txt"));
    }

    [Fact]
    public async Task The_rest_never_reaches_through_a_link_beyond_the_folder()
    {
        await using var world = await WorldAsync(SyncMode.Classic);
        await world.Host.Sync.RemoveAsync(world.Pair.Id, KeepOnPc.OnPc);
        // A junction the user made in the folder, to a folder elsewhere.
        var elsewhere = Path.Combine(world.Root, "Woanders");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "wichtig.txt"), "bleibt");
        await Junctions.CreateAsync(world.Pc("Verknüpfung"), elsewhere);

        var rest = await world.Host.Sync.RecycleRestAsync(world.Local);
        Assert.Equal("bleibt", File.ReadAllText(Path.Combine(elsewhere, "wichtig.txt")));
        Assert.Equal(["Verknüpfung" + Path.DirectorySeparatorChar], rest.Stayed);
        Assert.False(File.Exists(world.Pc("geholt.txt")));
    }

    [Fact]
    public async Task The_rest_of_a_folder_another_synchronisation_uses_never_goes()
    {
        await using var world = await WorldAsync(SyncMode.Classic);
        var error = await Assert.ThrowsAsync<CdException>(() => world.Host.Sync.RecycleRestAsync(world.Local));
        Assert.Equal("CD-9000", error.Code);
        await Assert.ThrowsAsync<CdException>(() => world.Host.Sync.RecycleRestAsync(Path.GetDirectoryName(world.Local)!));
        Assert.Equal("geholt", world.ReadPc("geholt.txt"));
    }

    [Fact]
    public async Task Keeping_nothing_stops_when_not_everything_could_go_up()
    {
        await using var world = await WorldAsync(SyncMode.OnDemand);
        world.WritePc("Neu am PC.txt", "neu");
        await world.Server.DisposeAsync();
        var error = await Assert.ThrowsAsync<CdException>(() => world.Host.Sync.RemoveAsync(world.Pair.Id, KeepOnPc.Nothing));
        Assert.Equal("CD-4608", error.Code);
        // Nothing changed: the synchronisation and every file are still there.
        Assert.NotNull(world.Host.Sync.FindPair(world.Pair.Id));
        Assert.Equal("neu", world.ReadPc("Neu am PC.txt"));
        Assert.Equal("geholt", world.ReadPc("geholt.txt"));
    }
}
