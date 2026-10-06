using System.Runtime.Versioning;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>
/// Synchronisations with files on demand, end to end: real rclone, the WebDAV test server and a folder registered with
/// Windows. CloudDrive-Sync's own process never fetches a file by reading it, so the tests fetch on purpose
/// (<see cref="Placeholders.Hydrate"/>) where a user would open a file.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public class OnDemandTests
{
    private static async Task<SyncWorld> WorldAsync(Action<SyncWorld>? cloud = null, Action<SyncWorld>? pc = null, bool refusingProxy = false)
    {
        var world = await SyncWorld.CreateAsync(refusingProxy: refusingProxy);
        cloud?.Invoke(world);
        pc?.Invoke(world);
        await world.AddAccountAsync();
        await world.AddPairAsync(p => p.Mode = SyncMode.OnDemand);
        var first = await world.RunAsync(BisyncMode.Resync);
        Assert.True(first.Success, $"{first.ErrorCode}: {first.ErrorDetail}");
        return world;
    }

    private static async Task<SyncRunOutcome> RunAsync(SyncWorld world, BisyncMode mode = BisyncMode.Normal)
    {
        var outcome = await world.RunAsync(mode);
        Assert.True(outcome.Success, $"{outcome.ErrorCode}: {outcome.ErrorDetail}");
        return outcome;
    }

    /// <summary>Both sides have the same files; placeholders without data have the cloud's size, those with data its content.</summary>
    private static void AssertInStep(SyncWorld world)
    {
        Assert.Equal(world.CloudFolders(), world.PcFolders());
        Assert.Equal(world.CloudFiles(), world.PcFiles());
        foreach (var file in world.PcFiles())
        {
            var info = Placeholders.Read(world.Pc(file));
            Assert.NotNull(info);
            Assert.True(info.InSync, $"not in sync: {file}");
            Assert.Equal(new FileInfo(world.Cloud(file)).Length, info.Size);
            if (info.IsFullyOnDisk && info.Size > 0) Assert.Equal(File.ReadAllBytes(world.Cloud(file)), File.ReadAllBytes(world.Pc(file)));
        }
    }

    private static string Fetch(SyncWorld world, string file)
    {
        Placeholders.Hydrate(world.Pc(file));
        return world.ReadPc(file);
    }

    [Fact]
    public async Task The_cloud_appears_as_placeholders_and_nothing_is_downloaded()
    {
        await using var world = await WorldAsync(cloud: w =>
        {
            w.WriteCloud("Bericht.txt", "Inhalt aus der Cloud");
            w.WriteCloud("Ordner mit Ä/Übung 1.txt", "Übung");
            w.WriteCloud("Ordner mit Ä/Tief/innen.txt", "ganz innen");
        });
        AssertInStep(world);
        Assert.All(world.PcFiles(), f => Assert.Equal(0, Placeholders.Read(world.Pc(f))!.OnDiskSize));
        Assert.Equal("Übung", Fetch(world, "Ordner mit Ä/Übung 1.txt"));
        var again = await RunAsync(world);
        Assert.Equal(0, again.Final.Transfers);
        Assert.Equal(0, again.Deletes);
    }

    [Fact]
    public async Task A_folder_with_the_same_files_becomes_files_on_demand_without_any_transfer()
    {
        await using var world = await WorldAsync(
            cloud: w => { w.WriteCloud("a.txt", "eins"); w.WriteCloud("Ordner/b.txt", "zwei"); },
            pc: w => { w.WritePc("a.txt", "eins"); w.WritePc("Ordner/b.txt", "zwei"); });
        AssertInStep(world);
        Assert.True(Placeholders.Read(world.Pc("a.txt"))!.IsFullyOnDisk);
        Assert.Equal(0, (await RunAsync(world)).Final.Transfers);
    }

    [Fact]
    public async Task A_change_on_the_PC_goes_up_and_the_file_stays_in_sync()
    {
        await using var world = await WorldAsync(cloud: w => w.WriteCloud("Plan.txt", "alt"));
        Fetch(world, "Plan.txt");
        world.WritePc("Plan.txt", "neu und länger");
        Assert.Equal(1, (await RunAsync(world)).Final.Transfers);
        Assert.Equal("neu und länger", world.ReadCloud("Plan.txt"));
        AssertInStep(world);
        Assert.Equal(0, (await RunAsync(world)).Final.Transfers);
    }

    [Fact]
    public async Task A_new_file_on_the_PC_goes_up_and_becomes_a_placeholder()
    {
        await using var world = await WorldAsync();
        world.WritePc("Neu/Notiz.txt", "vom PC");
        await RunAsync(world);
        Assert.Equal("vom PC", world.ReadCloud("Neu/Notiz.txt"));
        Assert.True(Placeholders.Read(world.Pc("Neu/Notiz.txt"))!.InSync);
        AssertInStep(world);
    }

    [Fact]
    public async Task A_change_in_the_cloud_reaches_the_PC()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("online.txt", "1"); w.WriteCloud("geholt.txt", "1"); });
        Fetch(world, "geholt.txt");
        world.WriteCloud("online.txt", "zweite Fassung", later: true);
        world.WriteCloud("geholt.txt", "zweite Fassung", later: true);
        await RunAsync(world);
        // Online only stays online only, with the new size; a file that was on the PC is fetched again.
        Assert.Equal(0, Placeholders.Read(world.Pc("online.txt"))!.OnDiskSize);
        Assert.True(Placeholders.Read(world.Pc("geholt.txt"))!.IsFullyOnDisk);
        Assert.Equal("zweite Fassung", world.ReadPc("geholt.txt"));
        Assert.Equal("zweite Fassung", Fetch(world, "online.txt"));
        AssertInStep(world);
    }

    [Fact]
    public async Task A_placeholder_never_delivers_another_version_than_its_own()
    {
        await using var world = await WorldAsync(cloud: w => w.WriteCloud("Liste.txt", "kurz"));
        world.WriteCloud("Liste.txt", "jetzt eine deutlich längere Fassung", later: true);
        Assert.ThrowsAny<IOException>(() => Placeholders.Hydrate(world.Pc("Liste.txt")));
        Assert.Equal(0, Placeholders.Read(world.Pc("Liste.txt"))!.OnDiskSize);
        await RunAsync(world);
        Assert.Equal("jetzt eine deutlich längere Fassung", Fetch(world, "Liste.txt"));
    }

    [Fact]
    public async Task Deleting_on_the_PC_deletes_in_the_cloud()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("weg.txt", "x"); w.WriteCloud("bleibt.txt", "y"); w.WriteCloud("auch.txt", "z"); });
        File.Delete(world.Pc("weg.txt"));
        var outcome = await RunAsync(world);
        Assert.Equal(1, outcome.Deletes);
        Assert.False(File.Exists(world.Cloud("weg.txt")));
        AssertInStep(world);
    }

    [Fact]
    public async Task Deleting_in_the_cloud_moves_fetched_files_into_the_recycle_bin()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("geholt.txt", "gesichert"); w.WriteCloud("online.txt", "x"); w.WriteCloud("bleibt.txt", "y"); w.WriteCloud("auch.txt", "z"); });
        Fetch(world, "geholt.txt");
        File.Delete(world.Cloud("geholt.txt"));
        File.Delete(world.Cloud("online.txt"));
        var outcome = await RunAsync(world);
        Assert.Equal(2, outcome.Deletes);
        Assert.False(File.Exists(world.Pc("geholt.txt")));
        Assert.False(File.Exists(world.Pc("online.txt")));
        Assert.Equal(["gesichert"], world.PcTrash());
        AssertInStep(world);
    }

    [Fact]
    public async Task Renaming_and_moving_on_the_PC_moves_in_the_cloud_without_uploading()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("alt.txt", "Inhalt"); w.WriteCloud("Ordner/datei.txt", "drin"); w.WriteCloud("Ordner/zweite.txt", "auch"); });
        File.Move(world.Pc("alt.txt"), world.Pc("neu.txt"));
        Directory.Move(world.Pc("Ordner"), world.Pc("Umbenannt"));
        var outcome = await RunAsync(world);
        Assert.Equal(0, outcome.Final.Transfers);
        Assert.Equal(0, outcome.Deletes);
        Assert.Equal(["Umbenannt/datei.txt", "Umbenannt/zweite.txt", "neu.txt"], world.CloudFiles());
        Assert.Equal("Inhalt", world.ReadCloud("neu.txt"));
        AssertInStep(world);
    }

    [Fact]
    public async Task Saving_like_Office_is_a_change_not_a_deletion()
    {
        await using var world = await WorldAsync(cloud: w => w.WriteCloud("Bericht.docx", "Fassung 1"));
        Fetch(world, "Bericht.docx");
        // Word: write a temporary file, rename the original, rename the new one, delete the original.
        File.WriteAllText(world.Pc("~WRD0001.tmp"), "Fassung 2");
        File.Move(world.Pc("Bericht.docx"), world.Pc("~WRL0002.tmp"));
        File.Move(world.Pc("~WRD0001.tmp"), world.Pc("Bericht.docx"));
        File.Delete(world.Pc("~WRL0002.tmp"));
        var outcome = await RunAsync(world);
        Assert.Equal(0, outcome.Deletes);
        Assert.Equal("Fassung 2", world.ReadCloud("Bericht.docx"));
        AssertInStep(world);
    }

    [Fact]
    public async Task Changes_on_both_sides_keep_both_versions()
    {
        // The test server keeps no times of its own (like IServ): "the newer one wins" keeps both.
        await using var world = await WorldAsync(cloud: w => w.WriteCloud("Plan.txt", "gemeinsam"));
        Fetch(world, "Plan.txt");
        world.WritePc("Plan.txt", "am PC geändert");
        world.WriteCloud("Plan.txt", "in der Cloud geändert, länger", later: true);
        await RunAsync(world);
        Assert.Equal(["Plan.Konflikt-Cloud1.txt", "Plan.Konflikt-PC1.txt"], world.CloudFiles());
        Assert.Equal("am PC geändert", world.ReadCloud("Plan.Konflikt-PC1.txt"));
        Assert.Equal("in der Cloud geändert, länger", world.ReadCloud("Plan.Konflikt-Cloud1.txt"));
        AssertInStep(world);
    }

    [Fact]
    public async Task Too_many_deletions_on_the_PC_stop_before_anything_is_deleted()
    {
        await using var world = await WorldAsync(cloud: w =>
        {
            for (var i = 1; i <= 6; i++) w.WriteCloud($"datei{i}.txt", $"{i}");
        });
        for (var i = 1; i <= 4; i++) File.Delete(world.Pc($"datei{i}.txt"));
        var outcome = await world.RunAsync();
        Assert.False(outcome.Success);
        Assert.Equal("CD-4502", outcome.ErrorCode);
        Assert.Equal(SyncDecision.Deletions, outcome.Decision);
        Assert.Equal(6, world.CloudFiles().Count);
        // Confirmed by the user: now they go.
        Assert.True((await world.RunAsync(BisyncMode.Force)).Success);
        Assert.Equal(2, world.CloudFiles().Count);
    }

    [Fact]
    public async Task A_missing_protection_file_in_the_cloud_stops_everything()
    {
        await using var world = await WorldAsync(cloud: w => w.WriteCloud("wichtig.txt", "x"));
        File.Delete(world.Cloud(SyncFilters.SentinelFile));
        File.Delete(world.Cloud("wichtig.txt"));
        var outcome = await world.RunAsync();
        Assert.Equal("CD-4503", outcome.ErrorCode);
        Assert.True(File.Exists(world.Pc("wichtig.txt")));
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 20)
    {
        for (var waited = 0; waited < seconds * 10; waited++)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    /// <summary>Explorer shows no status for a folder that is not in sync.</summary>
    private static void AssertFoldersInSync(SyncWorld world)
    {
        foreach (var folder in world.PcFolders()) Assert.True(Placeholders.Read(world.Pc(folder))!.InSync, $"not in sync: {folder}");
    }

    [Fact]
    public async Task Folders_are_in_sync_after_a_run_so_Explorer_shows_their_status()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("A/B/c.txt", "c"); w.WriteCloud("A/d.txt", "d"); w.WriteCloud("E/f.txt", "f"); });
        AssertFoldersInSync(world);
        world.WritePc("A/B/neu.txt", "neu");
        File.Move(world.Pc("A/d.txt"), world.Pc("A/e.txt"));
        File.Delete(world.Pc("E/f.txt"));
        world.WriteCloud("A/B/aus der Cloud.txt", "neu");
        world.WriteCloud("G/h.txt", "h");
        await RunAsync(world);
        AssertInStep(world);
        AssertFoldersInSync(world);
    }

    [Fact]
    public async Task A_folder_the_server_does_not_let_be_read_is_left_alone_and_the_rest_goes_on()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("Ablage/a.txt", "a"); w.WriteCloud("Offen/b.txt", "b"); }, refusingProxy: true);
        world.WriteCloud("Offen/neu.txt", "neu");
        world.WritePc("Ablage/vom PC.txt", "vom PC");
        // Like a share to upload only in Nextcloud: the server refuses to list the folder.
        world.Proxy!.RefuseListing($"{SyncWorld.CloudFolder}/Ablage");
        var outcome = await RunAsync(world);
        world.Proxy.RefuseListing(null);
        Assert.Equal(0, outcome.Deletes);
        Assert.True(File.Exists(world.Pc("Ablage/a.txt")));
        Assert.False(File.Exists(world.Cloud("Ablage/vom PC.txt")));
        Assert.Equal("neu", Fetch(world, "Offen/neu.txt"));
        // Readable again: everything comes in step.
        await RunAsync(world);
        AssertInStep(world);
    }

    [Fact]
    public async Task Keeping_on_this_device_fetches_and_freeing_space_gives_it_back()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("Plan.txt", "behalten"); w.WriteCloud("Ordner/a.txt", "eins"); w.WriteCloud("Ordner/b.txt", "zwei"); });
        var file = world.Pc("Plan.txt");
        // What Explorer does for "Always keep on this device" and "Free up space".
        Placeholders.SetPinState(file, PinState.Pinned, recurse: false);
        Assert.True(await WaitAsync(() => Placeholders.Read(file)!.IsFullyOnDisk), "not fetched");
        Assert.Equal("behalten", world.ReadPc("Plan.txt"));
        Placeholders.SetPinState(file, PinState.Unpinned, recurse: false);
        Assert.True(await WaitAsync(() => Placeholders.Read(file)!.OnDiskSize == 0), "not freed");

        // A folder kept on this device: everything in it - and what comes later.
        Placeholders.SetPinState(world.Pc("Ordner"), PinState.Pinned, recurse: false);
        Assert.True(await WaitAsync(() => Placeholders.Read(world.Pc("Ordner/a.txt"))!.IsFullyOnDisk && Placeholders.Read(world.Pc("Ordner/b.txt"))!.IsFullyOnDisk), "folder not fetched");
        world.WriteCloud("Ordner/neu.txt", "später dazu");
        await RunAsync(world);
        Assert.True(Placeholders.Read(world.Pc("Ordner/neu.txt"))!.IsFullyOnDisk);
        Assert.Equal(PinState.Pinned, Placeholders.Read(world.Pc("Ordner/neu.txt"))!.Pin);
    }

    [Fact]
    public async Task Freeing_space_never_throws_away_a_change_not_uploaded_yet()
    {
        await using var world = await WorldAsync(cloud: w => w.WriteCloud("Entwurf.txt", "alt"));
        Fetch(world, "Entwurf.txt");
        world.WritePc("Entwurf.txt", "noch nicht hochgeladen");
        Placeholders.SetPinState(world.Pc("Entwurf.txt"), PinState.Unpinned, recurse: false);
        await Task.Delay(2000);
        Assert.Equal("noch nicht hochgeladen", world.ReadPc("Entwurf.txt"));
        // After the upload the run gives the space back, as the user wanted.
        await RunAsync(world);
        Assert.Equal("noch nicht hochgeladen", world.ReadCloud("Entwurf.txt"));
        Assert.True(await WaitAsync(() => Placeholders.Read(world.Pc("Entwurf.txt"))!.OnDiskSize == 0), "not freed after the upload");
    }

    [Fact]
    public async Task What_was_chosen_while_the_program_was_not_running_is_carried_out_by_the_next_run()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("Ordner/a.txt", "eins"); w.WriteCloud("Ordner/Tief/b.txt", "zwei"); w.WriteCloud("frei.txt", "drei"); });
        Fetch(world, "frei.txt");
        await world.RestartAsync(whileStopped: () =>
        {
            // Explorer sets the state of the chosen folder alone.
            Placeholders.SetPinState(world.Pc("Ordner"), PinState.Pinned, recurse: false);
            Placeholders.SetPinState(world.Pc("frei.txt"), PinState.Unpinned, recurse: false);
        });
        await RunAsync(world);
        Assert.True(Placeholders.Read(world.Pc("Ordner/a.txt"))!.IsFullyOnDisk);
        Assert.True(Placeholders.Read(world.Pc("Ordner/Tief/b.txt"))!.IsFullyOnDisk);
        Assert.Equal(0, Placeholders.Read(world.Pc("frei.txt"))!.OnDiskSize);
        AssertInStep(world);
    }

    [Fact]
    public async Task A_lost_registration_never_deletes_anything_in_the_cloud()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("nur online.txt", "eins"); w.WriteCloud("Ordner/auch online.txt", "zwei"); w.WriteCloud("geladen.txt", "drei"); });
        Fetch(world, "geladen.txt");
        var id = SyncRoots.IdFor(world.Host.Paths, world.Pair.Id);
        // As when uninstalling and installing again: with the registration, Windows removes the online-only files from the PC.
        await world.RestartAsync(whileStopped: () => Assert.Equal([id], SyncRoots.UnregisterAll(world.Host.Paths)));
        Assert.False(File.Exists(world.Pc("nur online.txt")));
        var outcome = await RunAsync(world);
        Assert.Equal(0, outcome.Deletes);
        Assert.Equal("eins", world.ReadCloud("nur online.txt"));
        Assert.Equal("zwei", world.ReadCloud("Ordner/auch online.txt"));
        AssertInStep(world);
        Assert.Equal("drei", world.ReadPc("geladen.txt"));
        // From now on, deletions on the PC count again.
        File.Delete(world.Pc("nur online.txt"));
        await RunAsync(world);
        Assert.False(File.Exists(world.Cloud("nur online.txt")));
    }

    [Fact]
    public async Task Ending_the_synchronisation_keeps_fetched_files_and_removes_online_only_ones()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("geholt.txt", "da"); w.WriteCloud("online.txt", "x"); });
        Fetch(world, "geholt.txt");
        await world.Host.Sync.RemoveAsync(world.Pair.Id);
        Assert.Equal("da", world.ReadPc("geholt.txt"));
        Assert.Null(Placeholders.Read(world.Pc("geholt.txt")));
        Assert.False(File.Exists(world.Pc("online.txt")));
        // In the cloud everything stays.
        Assert.Equal(["geholt.txt", "online.txt"], world.CloudFiles());
    }
}
