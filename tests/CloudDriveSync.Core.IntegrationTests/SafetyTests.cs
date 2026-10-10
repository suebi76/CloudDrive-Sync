using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>The safety nets: nothing is deleted on a hunch, and every stop can be resolved by the user.</summary>
public class SafetyTests
{
    /// <summary>Like IServ's "Groups": the folder takes no files, the groups below it bring theirs.</summary>
    private static async Task<SyncWorld> FolderWithoutFilesAsync()
    {
        var world = await SyncWorld.CreateAsync(readOnlyServer: true);
        world.WriteCloud("Gruppe A/Plan.txt", "Plan");
        world.WriteCloud("Gruppe A/Material/Blatt 1.txt", "Blatt 1");
        world.WriteCloud("Gruppe B/Liste.txt", "Liste");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        return world;
    }

    [Fact]
    public async Task A_cloud_folder_that_takes_no_files_is_synchronised_without_a_protection_file_there()
    {
        await using var world = await FolderWithoutFilesAsync();
        Assert.False(world.Pair.CloudCheckFile);
        Assert.False(File.Exists(world.Cloud(SyncFilters.SentinelFile)));
        Assert.True(File.Exists(world.Pc(SyncFilters.SentinelFile)));
        var first = await world.RunAsync(BisyncMode.Resync);
        Assert.True(first.Success, $"{first.ErrorCode}: {first.ErrorDetail}");
        Assert.Equal("Blatt 1", world.ReadPc("Gruppe A/Material/Blatt 1.txt"));
        Assert.Equal("Liste", world.ReadPc("Gruppe B/Liste.txt"));
        // News from the server arrive as usual.
        world.WriteCloud("Gruppe B/Neu.txt", "Neu", later: true);
        var second = await world.RunAsync();
        Assert.True(second.Success, $"{second.ErrorCode}: {second.ErrorDetail}");
        Assert.Equal("Neu", world.ReadPc("Gruppe B/Neu.txt"));
        var quiet = await world.RunAsync();
        Assert.True(quiet.Success, $"{quiet.ErrorCode}: {quiet.ErrorDetail}");
        Assert.Equal(0, quiet.Final.Transfers);
    }

    [Fact]
    public async Task A_change_the_server_does_not_take_stays_on_the_PC_and_the_rest_goes_on()
    {
        await using var world = await FolderWithoutFilesAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);
        world.WritePc("Gruppe A/Meins.txt", "Meins");
        world.WriteCloud("Gruppe B/Neu.txt", "Neu", later: true);
        // The server does not take the file: it stays on the PC, the rest of the run goes on - no rebuild.
        var refused = await world.RunAsync();
        Assert.True(refused.Success, $"{refused.ErrorCode}: {refused.ErrorDetail}");
        Assert.Equal(["Gruppe A/Meins.txt"], refused.LocalOnlyAdded);
        Assert.Equal("Neu", world.ReadPc("Gruppe B/Neu.txt"));
        Assert.True(File.Exists(world.Pc("Gruppe A/Meins.txt")));
        Assert.False(File.Exists(world.Cloud("Gruppe A/Meins.txt")));
        world.Host.Sync.KeepLocalOnly(world.Pair.Id, refused.LocalOnlyAdded!);
        // The following runs are quiet; news from the server still arrive.
        world.WriteCloud("Gruppe B/Später.txt", "Später", later: true);
        var next = await world.RunAsync();
        Assert.True(next.Success, $"{next.ErrorCode}: {next.ErrorDetail}");
        Assert.Equal("Später", world.ReadPc("Gruppe B/Später.txt"));
        var quiet = await world.RunAsync();
        Assert.True(quiet.Success, $"{quiet.ErrorCode}: {quiet.ErrorDetail}");
        Assert.Equal(0, quiet.Final.Transfers);
        Assert.Equal("Meins", world.ReadPc("Gruppe A/Meins.txt"));
        Assert.Equal("Plan", world.ReadPc("Gruppe A/Plan.txt"));
        // Trying again: still refused, so it stays on the PC again - without a rebuild.
        world.Host.Sync.RetryLocalOnly(world.Pair.Id);
        var retried = await world.RunAsync();
        Assert.True(retried.Success, $"{retried.ErrorCode}: {retried.ErrorDetail}");
        Assert.Equal(["Gruppe A/Meins.txt"], retried.LocalOnlyAdded);
    }

    [Fact]
    public async Task A_cloud_folder_without_a_protection_file_that_suddenly_looks_empty_stops_the_run()
    {
        await using var world = await FolderWithoutFilesAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);
        Assert.True((await world.RunAsync()).Success);
        // On the server everything is gone at once (renamed, access taken away, a wrong answer).
        foreach (var entry in Directory.EnumerateFileSystemEntries(world.Cloud("")))
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
        var stopped = await world.RunAsync();
        Assert.False(stopped.Success);
        Assert.Equal("CD-4512", stopped.ErrorCode);
        Assert.Equal(SyncDecision.Folder, stopped.Decision);
        Assert.Equal("Plan", world.ReadPc("Gruppe A/Plan.txt"));
        Assert.Equal("Liste", world.ReadPc("Gruppe B/Liste.txt"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_folder_on_the_PC_that_Windows_refuses_never_counts_as_deleted(bool held)
    {
        await using var world = await SyncWorld.CreateAsync();
        for (var i = 1; i <= 10; i++) world.WriteCloud($"Offen/Datei {i:00}.txt", $"Inhalt {i}");
        world.WriteCloud("Gesperrt/a.txt", "a");
        world.WriteCloud("Gesperrt/Tiefer/b.txt", "b");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);

        // A folder the PC suddenly does not let be opened (no right to it, or broken).
        using (held ? RefusedFolder.Hold(world.Pc("Gesperrt")) : RefusedFolder.Deny(world.Pc("Gesperrt")))
            await world.RunAsync();
        Assert.Equal("a", world.ReadCloud("Gesperrt/a.txt"));
        Assert.Equal("b", world.ReadCloud("Gesperrt/Tiefer/b.txt"));
    }

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

    [Fact]
    public async Task The_protection_file_never_looks_changed_when_the_server_noted_a_later_time()
    {
        // Servers like IServ note the time they received a file, in whole seconds - here a little after the PC wrote
        // the protection file. When the only other file then changes, the protection file must still count as
        // unchanged; otherwise bisync stops with "all files were changed".
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("Plan.txt", "Mo 08:00");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        var serverSecond = TruncateToSecond(File.GetLastWriteTimeUtc(world.Cloud(SyncFilters.SentinelFile)));
        File.SetLastWriteTimeUtc(world.Pc(SyncFilters.SentinelFile), serverSecond.AddMilliseconds(-300));
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);
        Assert.True((await world.RunAsync()).Success);

        await Task.Delay(1100);
        world.WritePc("Plan.txt", "Mo 09:00");
        var run = await world.RunAsync();
        Assert.True(run.Success, $"{run.ErrorCode}: {run.ErrorDetail}");
        Assert.Equal("Mo 09:00", world.ReadCloud("Plan.txt"));
    }

    [Fact]
    public async Task Files_that_were_on_both_sides_before_the_first_run_do_not_look_changed_afterwards()
    {
        // A folder set up with copies of the cloud files that are older on the PC (the server noted when they arrived).
        await using var world = await SyncWorld.CreateAsync();
        foreach (var name in new[] { "Plan.txt", "Liste.txt", "Brief.txt" })
        {
            world.WriteCloud(name, $"Inhalt {name}");
            world.WritePc(name, $"Inhalt {name}");
            File.SetLastWriteTimeUtc(world.Pc(name), DateTime.UtcNow.AddHours(-3));
        }
        await world.AddAccountAsync();
        await world.AddPairAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);

        var calm = await world.RunAsync();
        Assert.True(calm.Success, $"{calm.ErrorCode}: {calm.ErrorDetail}");
        Assert.Empty(calm.Changes ?? []);
        world.WritePc("Plan.txt", "Inhalt Plan neu");
        var changed = await world.RunAsync();
        Assert.True(changed.Success, $"{changed.ErrorCode}: {changed.ErrorDetail}");
        Assert.Equal("Inhalt Plan neu", world.ReadCloud("Plan.txt"));
    }

    private static DateTime TruncateToSecond(DateTime time) => new(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, time.Kind);
}
