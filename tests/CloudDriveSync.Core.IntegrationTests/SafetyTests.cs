using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>The safety nets: nothing is deleted on a hunch, and every stop can be resolved by the user.</summary>
public class SafetyTests
{
    [Fact]
    public async Task Too_many_deletions_stop_until_the_user_restores_or_confirms_them()
    {
        await using var world = await SyncWorld.CreateAsync();
        for (var i = 1; i <= 10; i++) world.WriteCloud($"Datei {i:00}.txt", $"Inhalt {i}");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);

        for (var i = 1; i <= 8; i++) File.Delete(world.Pc($"Datei {i:00}.txt"));
        var stopped = await world.RunAsync();

        Assert.False(stopped.Success);
        Assert.Equal("CD-4502", stopped.ErrorCode);
        Assert.Equal(SyncDecision.Deletions, stopped.Decision);
        Assert.Equal(10, world.CloudFiles().Count);

        // "Restore": the rebuild brings the files back to the PC.
        var restored = await world.RunAsync(BisyncMode.Resync);
        Assert.True(restored.Success, restored.ErrorDetail);
        Assert.Equal(10, world.PcFiles().Count);
        Assert.Equal("Inhalt 3", world.ReadPc("Datei 03.txt"));

        // "Apply": the deletions are confirmed and carried out on the server.
        for (var i = 1; i <= 8; i++) File.Delete(world.Pc($"Datei {i:00}.txt"));
        Assert.Equal("CD-4502", (await world.RunAsync()).ErrorCode);
        var applied = await world.RunAsync(BisyncMode.Force);
        Assert.True(applied.Success, applied.ErrorDetail);
        Assert.Equal(["Datei 09.txt", "Datei 10.txt"], world.CloudFiles());
        Assert.False(Directory.Exists(world.Cloud(SyncFilters.TrashFolder)));
    }

    [Fact]
    public async Task Deleted_files_count_even_when_folders_outnumber_them()
    {
        await using var world = await SyncWorld.CreateAsync();
        foreach (var (folder, name) in new[] { ("Mathe", "A"), ("Deutsch", "B"), ("Kunst", "C"), ("Musik", "D") }) world.WriteCloud($"{folder}/{name}.txt", name);
        await world.AddAccountAsync();
        await world.AddPairAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);

        // 3 of 4 files - but only 3 of 9 entries with folders and sentinel file, which rclone alone would let pass.
        foreach (var file in new[] { "Mathe/A.txt", "Deutsch/B.txt", "Kunst/C.txt" }) File.Delete(world.Pc(file));
        var stopped = await world.RunAsync();
        Assert.Equal("CD-4502", stopped.ErrorCode);
        Assert.Equal(SyncDecision.Deletions, stopped.Decision);
        Assert.Contains("3 of 4", stopped.ErrorDetail);
        Assert.Equal(4, world.CloudFiles().Count);

        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);
        Assert.Equal(4, world.PcFiles().Count);

        // Two deleted files never stop a synchronisation.
        File.Delete(world.Pc("Mathe/A.txt"));
        File.Delete(world.Pc("Deutsch/B.txt"));
        var passed = await world.RunAsync();
        Assert.True(passed.Success, passed.ErrorDetail);
        Assert.Equal(["Kunst/C.txt", "Musik/D.txt"], world.CloudFiles());
    }

    [Fact]
    public async Task A_missing_sentinel_in_the_cloud_stops_without_changes_and_can_be_repaired()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("A.txt", "a");
        world.WriteCloud("B.txt", "b");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);

        File.Delete(world.Cloud(SyncFilters.SentinelFile));
        File.Delete(world.Pc("A.txt"));
        var stopped = await world.RunAsync();

        Assert.False(stopped.Success);
        Assert.Equal("CD-4503", stopped.ErrorCode);
        Assert.Equal(SyncDecision.Folder, stopped.Decision);
        Assert.True(File.Exists(world.Cloud("A.txt")));

        await world.Host.Sync.RepairAsync(world.Pair.Id);
        Assert.True(File.Exists(world.Cloud(SyncFilters.SentinelFile)));
        var rebuilt = await world.RunAsync(BisyncMode.Resync);
        Assert.True(rebuilt.Success, rebuilt.ErrorDetail);
        Assert.Equal("a", world.ReadPc("A.txt"));
    }

    [Fact]
    public async Task A_missing_local_folder_or_sentinel_stops_before_anything_runs()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("A.txt", "a");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);

        File.SetAttributes(world.Pc(SyncFilters.SentinelFile), FileAttributes.Normal);
        File.Delete(world.Pc(SyncFilters.SentinelFile));
        var noSentinel = await world.RunAsync();
        Assert.Equal("CD-4503", noSentinel.ErrorCode);
        Assert.Equal(SyncDecision.Folder, noSentinel.Decision);

        // The folder is gone (e.g. a USB stick that is not plugged in): an empty folder is never taken as "all deleted".
        Directory.Move(world.Local, world.Local + " (weg)");
        var noFolder = await world.RunAsync();
        Assert.Equal("CD-4501", noFolder.ErrorCode);
        Assert.Equal(SyncDecision.Folder, noFolder.Decision);
        Assert.True(File.Exists(world.Cloud("A.txt")));
    }

    [Fact]
    public async Task A_changed_selection_without_a_rebuild_is_refused_by_the_engine()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("A.txt", "a");
        world.WriteCloud("B.bak", "b");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);

        // Hidden files must never look deleted - bisync insists on a rebuild, which the service then starts.
        world.Host.Settings.Update(s => s.Syncs[0].Selection.Exclude.Add("*.bak"));
        var refused = await world.RunAsync();
        Assert.Equal("CD-4504", refused.ErrorCode);
        Assert.Equal(SyncDecision.Rebuild, refused.Decision);
        Assert.True(File.Exists(world.Cloud("B.bak")));

        var rebuilt = await world.RunAsync(BisyncMode.Resync);
        Assert.True(rebuilt.Success, rebuilt.ErrorDetail);
        Assert.True(File.Exists(world.Cloud("B.bak")));
        Assert.True(File.Exists(world.Pc("B.bak")));
    }
}
