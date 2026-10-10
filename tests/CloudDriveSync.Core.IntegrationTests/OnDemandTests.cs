using System.Runtime.Versioning;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.OnDemand;
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

    /// <summary>Collects reports at once (Progress&lt;T&gt; would post them later).</summary>
    private sealed class Collect<T>(List<T> into) : IProgress<T>
    {
        public void Report(T value)
        {
            lock (into) into.Add(value);
        }
    }

    [Fact]
    public async Task The_preview_counts_the_cloud_tells_how_far_it_got_and_leaves_a_refused_folder_out()
    {
        await using var world = await SyncWorld.CreateAsync(refusingProxy: true);
        world.WriteCloud("a.txt", "eins");
        world.WriteCloud("Ordner/b.txt", "zwei");
        world.WriteCloud("Ablage/c.txt", "drei");
        await world.AddAccountAsync();
        world.Proxy!.RefuseListing($"{SyncWorld.CloudFolder}/Ablage");
        var reports = new List<ListingProgress>();
        var draft = new SyncPairSettings { AccountId = world.Account.Id, RemotePath = SyncWorld.CloudFolder, LocalPath = world.Local, Mode = SyncMode.OnDemand };

        var preview = await world.Host.Sync.PreviewAsync(draft, new Collect<ListingProgress>(reports));

        Assert.Equal(2, preview.CloudFiles);
        Assert.Equal(1, preview.UnreadableFolders);
        Assert.Equal(new ListingProgress(2, 2, 8), reports[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_folder_on_the_PC_that_Windows_refuses_is_left_out_and_nothing_of_it_is_deleted(bool held)
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("Gesperrt/a.txt", "a"); w.WriteCloud("Gesperrt/Tiefer/b.txt", "b"); w.WriteCloud("Offen/c.txt", "c"); });
        world.WriteCloud("Offen/neu.txt", "neu");
        // Like a placeholder Windows calls broken: the folder cannot be opened.
        SyncRunOutcome run;
        using (held ? RefusedFolder.Hold(world.Pc("Gesperrt")) : RefusedFolder.Deny(world.Pc("Gesperrt")))
            run = await RunAsync(world);
        Assert.True(run.Success, $"{run.ErrorCode}: {run.ErrorDetail}");
        Assert.True(run.Deletes == 0, string.Join("; ", (run.Changes ?? []).Select(c => $"{c.Kind} {c.Path}")));
        Assert.Equal("a", world.ReadCloud("Gesperrt/a.txt"));
        Assert.Equal("b", world.ReadCloud("Gesperrt/Tiefer/b.txt"));
        Assert.True(File.Exists(world.Pc("Offen/neu.txt")));
        // Open again: everything in step.
        var again = await RunAsync(world);
        Assert.True(again.Success, $"{again.ErrorCode}: {again.ErrorDetail}");
        Assert.Equal(world.CloudFiles(), world.PcFiles());
    }

    [Fact]
    public async Task What_the_listing_passes_over_while_it_is_still_on_the_PC_is_never_deleted_in_the_cloud()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("Ordner/a.txt", "a"); w.WriteCloud("Offen/b.txt", "b"); });
        // The folder turned into something rclone passes over without a word - here a junction, as with a placeholder
        // Windows calls broken.
        var elsewhere = Path.Combine(world.Root, "Woanders");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "a.txt"), "anders");
        Directory.Delete(world.Pc("Ordner"), recursive: true);
        await Junctions.CreateAsync(world.Pc("Ordner"), elsewhere);

        var run = await RunAsync(world);
        Assert.Equal(0L, run.Deletes);
        Assert.Equal("a", world.ReadCloud("Ordner/a.txt"));
        Assert.Equal("b", world.ReadCloud("Offen/b.txt"));
        Assert.Equal("anders", File.ReadAllText(Path.Combine(elsewhere, "a.txt")));
    }

    [Fact]
    public async Task What_Windows_refused_and_is_gone_afterwards_comes_again_from_the_cloud()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("Gesperrt/a.txt", "a"); w.WriteCloud("Gesperrt/Tiefer/b.txt", "b"); w.WriteCloud("Offen/c.txt", "c"); });
        using (RefusedFolder.Hold(world.Pc("Gesperrt")))
            await RunAsync(world);
        var noted = File.ReadAllLines(RefusedOnPc.FileOf(world.Host.Paths.SyncPairDir(world.Pair.Id)));
        Assert.Contains("refused	Gesperrt", noted);

        // Removed while CloudDrive-Sync was not looking - like a broken placeholder cleaned up in Safe Mode. Nothing can
        // delete such an entry while Windows refuses it, so it is no deletion by the user.
        Directory.Delete(world.Pc("Gesperrt"), recursive: true);
        var run = await RunAsync(world);
        Assert.Equal(0L, run.Deletes);
        Assert.Equal("a", world.ReadCloud("Gesperrt/a.txt"));
        Assert.Equal("b", world.ReadCloud("Gesperrt/Tiefer/b.txt"));
        Assert.Equal("b", Fetch(world, "Gesperrt/Tiefer/b.txt"));
        AssertInStep(world);
        Assert.False(File.Exists(RefusedOnPc.FileOf(world.Host.Paths.SyncPairDir(world.Pair.Id))));

        // A folder deleted on the PC that was never refused is deleted in the cloud, as always.
        Directory.Delete(world.Pc("Offen"), recursive: true);
        Assert.True((await RunAsync(world)).Deletes > 0);
        Assert.False(File.Exists(world.Cloud("Offen/c.txt")));
    }

    [Fact]
    public async Task Should_Windows_write_a_placeholder_broken_no_more_are_made_and_nothing_is_deleted()
    {
        await using var world = await WorldAsync(cloud: w => w.WriteCloud("Alt.txt", "alt"));
        world.WriteCloud("A/1.txt", "1");
        world.WriteCloud("B/2.txt", "2");
        world.WriteCloud("C/3.txt", "3");
        // Windows stands in: the new folder "A" comes out broken.
        world.Host.Sync.PlaceholderCheck = path => Path.GetFileName(path) == "A";
        var stopped = await world.RunAsync();
        Assert.False(stopped.Success);
        Assert.Equal("CD-4610", stopped.ErrorCode);
        Assert.False(stopped.Retryable);
        // The folders of the same call are there; nothing below them was made.
        Assert.True(Directory.Exists(world.Pc("B")));
        Assert.False(File.Exists(world.Pc("B/2.txt")));
        Assert.False(File.Exists(world.Pc("C/3.txt")));

        // The next run - Windows fine again - still makes nothing new in this version, and deletes nothing.
        world.Host.Sync.PlaceholderCheck = null;
        world.WriteCloud("D/4.txt", "4");
        var still = await world.RunAsync();
        Assert.Equal("CD-4610", still.ErrorCode);
        Assert.Equal(0L, still.Deletes);
        Assert.False(Directory.Exists(world.Pc("D")));
        Assert.Equal(["A/1.txt", "Alt.txt", "B/2.txt", "C/3.txt", "D/4.txt"], world.CloudFiles().Where(f => !f.StartsWith(".clouddrive", StringComparison.Ordinal)));

        // A later version tries again.
        var alarm = PlaceholderAlarm.FileOf(world.Host.Paths.SyncPairDir(world.Pair.Id));
        File.WriteAllLines(alarm, ["0.0.0-earlier", .. File.ReadAllLines(alarm).Skip(1)]);
        await RunAsync(world);
        Assert.Equal("4", Fetch(world, "D/4.txt"));
        Assert.Equal("1", Fetch(world, "A/1.txt"));
        AssertInStep(world);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ending_with_folders_the_server_refuses_leaves_nothing_behind(bool refusedFromTheStart)
    {
        await using var world = await SyncWorld.CreateAsync(refusingProxy: true);
        world.WriteCloud("Ablage/a.txt", "a");
        world.WriteCloud("Ablage/Gesperrt/b.txt", "b");
        world.WriteCloud("Ablage/Gesperrt/Tiefer/c.txt", "c");
        world.WriteCloud("Offen/d.txt", "d");
        await world.AddAccountAsync();
        // Like a share to upload only in Nextcloud: the server refuses to list the folder.
        if (refusedFromTheStart) world.Proxy!.RefuseListing($"{SyncWorld.CloudFolder}/Ablage/Gesperrt");
        await world.AddPairAsync(p => p.Mode = SyncMode.OnDemand);
        var first = await world.RunAsync(BisyncMode.Resync);
        Assert.True(first.Success, $"{first.ErrorCode}: {first.ErrorDetail}");
        world.Proxy!.RefuseListing($"{SyncWorld.CloudFolder}/Ablage/Gesperrt");
        var run = await RunAsync(world);
        Assert.True(run.Success, $"{run.ErrorCode}: {run.ErrorDetail}");

        await world.Host.Sync.RemoveAsync(world.Pair.Id);
        Assert.Equal(0, Leftovers.Count(world.Local));
        Assert.False(File.Exists(Path.Combine(world.Host.Paths.Home, "cleanup.json")));
        // The folder lets itself be deleted like any other.
        Directory.Delete(world.Local, recursive: true);
        Assert.False(Directory.Exists(world.Local));
    }

    /// <summary>The clock of a later day.</summary>
    private sealed class LaterTime(TimeSpan ahead) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + ahead;
    }

    [Fact]
    public async Task Files_not_used_for_some_days_give_their_space_back_when_that_is_chosen()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("alt.txt", "lange nicht geöffnet"); w.WriteCloud("behalten.txt", "angeheftet"); w.WriteCloud("neu.txt", "gerade geholt"); });
        Fetch(world, "alt.txt");
        Fetch(world, "behalten.txt");
        Placeholders.SetPinState(world.Pc("behalten.txt"), PinState.Pinned, recurse: false);
        await RunAsync(world);
        // 100 days later. By default nothing goes.
        world.Host.Sync.Time = new LaterTime(TimeSpan.FromDays(100));
        Fetch(world, "neu.txt");
        await RunAsync(world);
        Assert.True(Placeholders.Read(world.Pc("alt.txt"))!.IsFullyOnDisk);
        // "Nach 7 Tagen": what was not used goes; a kept file and a file fetched just now stay.
        world.Host.Settings.Update(s => s.Preferences.FreeUpAfterDays = 7);
        var outcome = await RunAsync(world);
        Assert.Equal(0, Placeholders.Read(world.Pc("alt.txt"))!.OnDiskSize);
        Assert.True(Placeholders.Read(world.Pc("behalten.txt"))!.IsFullyOnDisk);
        Assert.True(Placeholders.Read(world.Pc("neu.txt"))!.IsFullyOnDisk);
        Assert.Equal("lange nicht geöffnet", world.ReadCloud("alt.txt"));
        // The card shows what lies on the PC.
        long Size(string file) => new FileInfo(world.Cloud(file)).Length;
        Assert.Equal(new SpaceUse(Size("behalten.txt") + Size("neu.txt"), Size("alt.txt") + Size("behalten.txt") + Size("neu.txt")), outcome.Space);
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
        var id = SyncRoots.IdFor(world.Host.Paths, world.Pair);
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
    public async Task Files_on_demand_are_not_offered_inside_or_above_a_registered_folder()
    {
        await using var world = await WorldAsync(cloud: w => w.WriteCloud("a.txt", "a"));
        Assert.NotNull(SyncService.OnDemandProblem(world.Pc("Unterordner")));
        Assert.NotNull(SyncService.OnDemandProblem(Path.GetDirectoryName(world.Local)!));
        Assert.Null(SyncService.OnDemandProblem(Path.Combine(world.Root, "anderswo")));
    }

    [Fact]
    public async Task The_name_in_Explorer_can_be_changed_while_the_folder_is_connected()
    {
        await using var world = await WorldAsync(cloud: w => w.WriteCloud("a.txt", "a"));
        var id = SyncRoots.IdFor(world.Host.Paths, world.Pair);
        world.Host.Sync.Update(world.Pair.Id, p => p.ExplorerName = "Unterricht 7b");
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\{id}");
        Assert.NotNull(key);
        Assert.Contains("Unterricht 7b", key.GetValueNames().Select(name => key.GetValue(name)?.ToString() ?? ""));
        // Files keep opening.
        Assert.Equal("a", Fetch(world, "a.txt"));
    }

    [Fact]
    public async Task Checking_compares_without_fetching_anything()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("nur online.txt", "online"); w.WriteCloud("geholt.txt", "geholt"); w.WriteCloud("Ordner/c.txt", "c"); });
        Fetch(world, "geholt.txt");
        var inStep = await world.Host.Sync.VerifyAsync(world.Pair.Id, compareContent: true);
        Assert.True(inStep.InStep, string.Join(", ", inStep.Different.Concat(inStep.OnlyInCloud).Concat(inStep.OnlyOnPc).Concat(inStep.Unreadable)));
        Assert.Equal(3, inStep.Matching);

        world.WriteCloud("Ordner/c.txt", "in der Cloud geändert");
        world.WriteCloud("neu in der Cloud.txt", "neu");
        world.WritePc("geholt.txt", "am PC geändert");
        var differences = await world.Host.Sync.VerifyAsync(world.Pair.Id, compareContent: true);
        Assert.Equal(["neu in der Cloud.txt"], differences.OnlyInCloud);
        Assert.Equal(["geholt.txt", "Ordner/c.txt"], differences.Different);
        Assert.Empty(differences.OnlyOnPc);
        // Nothing was fetched for it.
        Assert.Equal(0, Placeholders.Read(world.Pc("nur online.txt"))!.OnDiskSize);
        Assert.Equal(0, Placeholders.Read(world.Pc("Ordner/c.txt"))!.OnDiskSize);
    }

    [Fact]
    public async Task A_folder_no_longer_selected_keeps_its_fetched_files_and_loses_its_online_only_ones()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("Mathe/a.txt", "geholt"); w.WriteCloud("Mathe/b.txt", "nur online"); w.WriteCloud("Deutsch/c.txt", "c"); });
        Fetch(world, "Mathe/a.txt");
        world.Host.Sync.Update(world.Pair.Id, p => p.Selection = new SyncSelection { Mode = SelectionMode.Selected, Include = ["Deutsch/"] });
        var deselected = await RunAsync(world, BisyncMode.Resync);
        Assert.Equal(0, deselected.Deletes);
        Assert.Equal("geholt", world.ReadPc("Mathe/a.txt"));
        Assert.Null(Placeholders.Read(world.Pc("Mathe/a.txt")));
        Assert.False(File.Exists(world.Pc("Mathe/b.txt")));
        Assert.Equal("nur online", world.ReadCloud("Mathe/b.txt"));
        Assert.NotNull(Placeholders.Read(world.Pc("Deutsch/c.txt")));
        // Selected again: everything is there, the file kept on the PC is taken as it is.
        world.Host.Sync.Update(world.Pair.Id, p => p.Selection = new SyncSelection { Mode = SelectionMode.All });
        await RunAsync(world, BisyncMode.Resync);
        AssertInStep(world);
        Assert.DoesNotContain(world.PcFiles(), f => f.Contains("Konflikt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ending_the_synchronisation_keeps_fetched_files_and_removes_online_only_ones()
    {
        await using var world = await WorldAsync(cloud: w =>
        {
            w.WriteCloud("geholt.txt", "da");
            w.WriteCloud("online.txt", "x");
            w.WriteCloud("Nur online/a.txt", "a");
            w.WriteCloud("Gemischt/b.txt", "b");
            w.WriteCloud("Gemischt/c.txt", "c");
        });
        Fetch(world, "geholt.txt");
        Fetch(world, "Gemischt/b.txt");
        await world.Host.Sync.RemoveAsync(world.Pair.Id);
        // Nothing is left that Windows would call corrupt - at once, not only some time later.
        Assert.Equal(0, Leftovers.Count(world.Local));
        Assert.Equal("da", world.ReadPc("geholt.txt"));
        Assert.Null(Placeholders.Read(world.Pc("geholt.txt")));
        Assert.Equal("b", world.ReadPc("Gemischt/b.txt"));
        Assert.False(File.Exists(world.Pc("online.txt")));
        Assert.False(File.Exists(world.Pc("Gemischt/c.txt")));
        Assert.False(Directory.Exists(world.Pc("Nur online")));
        // In the cloud everything stays.
        Assert.Equal(["Gemischt/b.txt", "Gemischt/c.txt", "Nur online/a.txt", "geholt.txt", "online.txt"], world.CloudFiles());
    }

    [Fact]
    public async Task What_a_program_holds_while_ending_keeps_its_registration_until_it_is_free()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("a.txt", "a"); w.WriteCloud("gehalten.txt", "nur online"); });
        world.Host.Sync.CleanUpWait = TimeSpan.FromSeconds(2);
        var id = SyncRoots.IdFor(world.Host.Paths, world.Pair);
        var noted = Path.Combine(world.Host.Paths.Home, "cleanup.json");
        // A program holds an online-only file while the synchronisation ends (nothing is fetched by that).
        EndResult result;
        using (new FileStream(world.Pc("gehalten.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await world.Host.Sync.RemoveAsync(world.Pair.Id);
            // The registration stays: the held placeholder stays valid - never one that Windows calls damaged.
            Assert.True(result.StillHeld);
            Assert.True(SyncRoots.IsRegistered(id));
            Assert.True(File.Exists(noted));
            Assert.False(File.Exists(world.Pc("a.txt")));
        }
        Assert.NotNull(Placeholders.Read(world.Pc("gehalten.txt")));
        // Once the program lets go, the next attempt clears the folder and ends the registration.
        world.Host.Sync.FinishCleanUps();
        Assert.Equal(0, Leftovers.Count(world.Local));
        Assert.False(SyncRoots.IsRegistered(id));
        Assert.False(File.Exists(noted));
        Assert.False(File.Exists(world.Pc("gehalten.txt")));
        Assert.Equal("nur online", world.ReadCloud("gehalten.txt"));
        Directory.Delete(world.Local, recursive: true);
    }

    [Fact]
    public async Task A_registration_no_synchronisation_uses_is_cleared_even_without_a_note()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("nur online.txt", "eins"); w.WriteCloud("geladen.txt", "zwei"); });
        Fetch(world, "geladen.txt");
        var id = SyncRoots.IdFor(world.Host.Paths, world.Pair);
        // As if the list of folders to clear up had been lost: the synchronisation is gone, its registration is not.
        await world.RestartAsync(whileStopped: () => new SettingsStore(world.Host.Paths.SettingsFile).Update(s => s.Syncs.Clear()));
        Assert.True(SyncRoots.IsRegistered(id));
        world.Host.Sync.FinishCleanUps();
        Assert.False(SyncRoots.IsRegistered(id));
        Assert.Equal(0, Leftovers.Count(world.Local));
        Assert.False(File.Exists(world.Pc("nur online.txt")));
        Assert.Equal("zwei", world.ReadPc("geladen.txt"));
        Assert.Equal("eins", world.ReadCloud("nur online.txt"));
    }

    [Fact]
    public async Task Uninstalling_ends_registrations_only_with_their_folders_clear()
    {
        await using var world = await WorldAsync(cloud: w => { w.WriteCloud("nur online.txt", "eins"); w.WriteCloud("geladen.txt", "zwei"); });
        Fetch(world, "geladen.txt");
        var id = SyncRoots.IdFor(world.Host.Paths, world.Pair);
        var noted = Path.Combine(world.Host.Paths.Home, "cleanup.json");
        await world.RestartAsync(whileStopped: () =>
        {
            // First a program holds a file: that folder stays registered and is noted.
            using (new FileStream(world.Pc("nur online.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Empty(SyncService.EndAllForUninstall(world.Host.Paths, TimeSpan.FromSeconds(20)));
            }
            Assert.True(SyncRoots.IsRegistered(id));
            Assert.True(File.Exists(noted));
            Assert.NotNull(Placeholders.Read(world.Pc("nur online.txt")));
            // Free again: it ends, and nothing is left that could not be deleted.
            Assert.Equal([id], SyncService.EndAllForUninstall(world.Host.Paths, TimeSpan.FromSeconds(20)));
            Assert.False(SyncRoots.IsRegistered(id));
            Assert.Equal(0, Leftovers.Count(world.Local));
            Assert.False(File.Exists(world.Pc("nur online.txt")));
            Assert.Equal("zwei", world.ReadPc("geladen.txt"));
            Assert.Null(Placeholders.Read(world.Pc("geladen.txt")));
        });
    }

    [Fact]
    public async Task A_noted_clean_up_never_touches_a_registration_a_synchronisation_uses()
    {
        await using var world = await WorldAsync(cloud: w => w.WriteCloud("a.txt", "a"));
        var id = SyncRoots.IdFor(world.Host.Paths, world.Pair);
        // A note for the folder and the registration of the running synchronisation (as after reinstalling).
        var noted = Path.Combine(world.Host.Paths.Home, "cleanup.json");
        File.WriteAllText(noted, System.Text.Json.JsonSerializer.Serialize(new List<SyncService.PendingCleanUp> { new(id, world.Local, world.Pair.Id) }, SettingsStore.JsonOptions));
        world.Host.Sync.FinishCleanUps();
        Assert.True(SyncRoots.IsRegistered(id));
        Assert.NotNull(Placeholders.Read(world.Pc("a.txt")));
        Assert.True(File.Exists(noted));
        var run = await RunAsync(world);
        Assert.True(run.Success, $"{run.ErrorCode}: {run.ErrorDetail}");
        // Ending the synchronisation clears its folder; the note is done with it.
        await world.Host.Sync.RemoveAsync(world.Pair.Id);
        Assert.Equal(0, Leftovers.Count(world.Local));
        Assert.False(File.Exists(noted));
    }

    [Fact]
    public async Task Ending_again_in_another_folder_never_takes_over_the_registration_of_the_first()
    {
        await using var world = await WorldAsync(cloud: w => w.WriteCloud("a.txt", "a"));
        var first = SyncRoots.IdFor(world.Host.Paths, world.Pair);
        await world.Host.Sync.RemoveAsync(world.Pair.Id);
        var again = await world.AddPairAsync(p => { p.Mode = SyncMode.OnDemand; p.LocalPath = world.Local + " 2"; });
        Assert.Equal(world.Pair.Id, again.Id);
        Assert.NotEqual(first, SyncRoots.IdFor(world.Host.Paths, again));
    }
}
