using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>What the user can see: which files a run moved where, and a check of both sides on request.</summary>
public class TransparencyTests
{
    [Fact]
    public async Task A_run_tells_which_files_went_where()
    {
        await using var world = await SyncWorld.CreateAsync();
        foreach (var name in new[] { "Bleibt.txt", "Weg vom PC.txt", "Weg vom Server.txt", "x.txt", "y.txt", "z.txt" }) world.WriteCloud(name, name);
        await world.AddAccountAsync();
        await world.AddPairAsync();
        var first = await world.RunAsync(BisyncMode.Resync);
        Assert.True(first.Success, $"{first.ErrorCode}: {first.ErrorDetail}");
        Assert.Contains(new FileChange(ChangeKind.Downloaded, "Bleibt.txt"), first.Changes!);

        world.WritePc("Neu am PC.txt", "neu");
        File.Delete(world.Pc("Weg vom PC.txt"));
        File.Delete(world.Cloud("Weg vom Server.txt"));
        world.WriteCloud("Neu vom Server.txt", "neu", later: true);
        var run = await world.RunAsync();
        Assert.True(run.Success, $"{run.ErrorCode}: {run.ErrorDetail}");
        Assert.Contains(new FileChange(ChangeKind.Uploaded, "Neu am PC.txt"), run.Changes!);
        Assert.Contains(new FileChange(ChangeKind.Downloaded, "Neu vom Server.txt"), run.Changes!);
        Assert.Contains(new FileChange(ChangeKind.DeletedInCloud, "Weg vom PC.txt"), run.Changes!);
        Assert.Contains(new FileChange(ChangeKind.DeletedOnPc, "Weg vom Server.txt"), run.Changes!);
        Assert.DoesNotContain(run.Changes!, change => change.Path.StartsWith(".clouddrive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_same_size_change_carried_over_by_CloudDrive_Sync_is_named_too()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("Plan.txt", "Mo 08:00");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);
        Assert.True((await world.RunAsync()).Success);
        await Task.Delay(1100);
        world.WritePc("Plan.txt", "Mo 09:00");
        var run = await world.RunAsync();
        Assert.True(run.Success, $"{run.ErrorCode}: {run.ErrorDetail}");
        Assert.Contains(new FileChange(ChangeKind.Uploaded, "Plan.txt"), run.Changes!);
    }

    [Fact]
    public async Task Checking_finds_every_kind_of_difference_and_changes_nothing()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("a.txt", "Inhalt A");
        world.WriteCloud("Ordner/b.txt", "Inhalt B");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);

        var clean = await world.Host.Sync.VerifyAsync(world.Pair.Id, compareContent: false);
        Assert.True(clean.InStep);
        Assert.Equal(2, clean.Matching);

        world.WritePc("Nur PC.txt", "pc");
        world.WriteCloud("Nur Cloud.txt", "cloud");
        world.WriteCloud("a.txt", "Inhalt A, jetzt länger");
        world.WriteCloud("Ordner/b.txt", "Inhalt X");
        var quick = await world.Host.Sync.VerifyAsync(world.Pair.Id, compareContent: false);
        Assert.Equal(["Nur PC.txt"], quick.OnlyOnPc);
        Assert.Equal(["Nur Cloud.txt"], quick.OnlyInCloud);
        Assert.Contains("a.txt", quick.Different);
        Assert.False(quick.InStep);

        // Comparing the content also finds a change that kept the size.
        var thorough = await world.Host.Sync.VerifyAsync(world.Pair.Id, compareContent: true);
        Assert.True(thorough.ComparedContent);
        Assert.Contains("Ordner/b.txt", thorough.Different);
        Assert.Contains("a.txt", thorough.Different);

        // Checking changed nothing on either side.
        Assert.Equal("pc", world.ReadPc("Nur PC.txt"));
        Assert.False(File.Exists(world.Pc("Nur Cloud.txt")));
        Assert.Equal("Inhalt B", world.ReadPc("Ordner/b.txt"));
        Assert.Equal("Inhalt X", world.ReadCloud("Ordner/b.txt"));
    }
}
