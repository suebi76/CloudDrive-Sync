using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>The synchronisation itself, against a server that behaves like IServ.</summary>
public class SyncTests
{
    [Fact]
    public async Task The_first_synchronisation_merges_both_sides_and_loses_nothing()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("Plan.txt", "Stundenplan");
        world.WriteCloud("Mathe/Aufgaben.txt", "1+1");
        Directory.CreateDirectory(world.Cloud("Leerer Ordner"));
        world.WriteCloud("Beide.txt", "Cloud-Fassung");
        world.WritePc("Beide.txt", "PC-Fassung, etwas länger");
        world.WritePc("Notizen.txt", "nur am PC");
        await world.AddAccountAsync();
        await world.AddPairAsync();

        var outcome = await world.RunAsync(BisyncMode.Resync);

        Assert.True(outcome.Success, outcome.ErrorDetail);
        Assert.Equal("Stundenplan", world.ReadPc("Plan.txt"));
        Assert.Equal("1+1", world.ReadPc("Mathe/Aufgaben.txt"));
        Assert.True(Directory.Exists(world.Pc("Leerer Ordner")));
        Assert.Equal("nur am PC", world.ReadCloud("Notizen.txt"));
        Assert.True(File.Exists(world.Pc(SyncFilters.SentinelFile)));
        Assert.True(File.Exists(world.Cloud(SyncFilters.SentinelFile)));
        Assert.True(File.GetAttributes(world.Pc(SyncFilters.SentinelFile)).HasFlag(FileAttributes.Hidden));
        // The file that differed: both sides agree now, and the other version still exists.
        Assert.Equal(world.ReadCloud("Beide.txt"), world.ReadPc("Beide.txt"));
        var everything = world.PcFiles().Select(world.ReadPc).Concat(world.PcTrash()).ToList();
        Assert.Contains("Cloud-Fassung", everything);
        Assert.Contains("PC-Fassung, etwas länger", everything);
        Assert.Equal(world.CloudFiles(), world.PcFiles());
    }

    [Fact]
    public async Task Changes_and_deletions_go_both_ways_and_replaced_files_wait_in_the_recycle_bins()
    {
        await using var world = await SyncWorld.CreateAsync();
        foreach (var name in new[] { "A", "B", "C", "D", "E", "F" }) world.WriteCloud($"{name}.txt", name.ToLowerInvariant());
        world.WriteCloud("Ordner/G.txt", "g");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);

        world.WritePc("A.txt", "a - am PC geändert");
        world.WritePc("Neu vom PC.txt", "neu");
        world.WriteCloud("B.txt", "b - in der Cloud geändert", later: true);
        world.WriteCloud("Ordner/Neu aus der Cloud.txt", "wolke", later: true);
        File.Delete(world.Cloud("C.txt"));
        File.Delete(world.Pc("D.txt"));

        var outcome = await world.RunAsync();

        Assert.True(outcome.Success, outcome.ErrorDetail);
        Assert.Equal("a - am PC geändert", world.ReadCloud("A.txt"));
        Assert.Equal("neu", world.ReadCloud("Neu vom PC.txt"));
        Assert.Equal("b - in der Cloud geändert", world.ReadPc("B.txt"));
        Assert.Equal("wolke", world.ReadPc("Ordner/Neu aus der Cloud.txt"));
        Assert.False(File.Exists(world.Pc("C.txt")));
        Assert.False(File.Exists(world.Cloud("D.txt")));
        Assert.Equal(world.CloudFiles(), world.PcFiles());
        Assert.Empty(outcome.Conflicts);
        // What the synchronisation deleted or replaced on the PC waits in its recycle bin there; deleted on the PC means
        // deleted on the server - no recycle bin folder appears there.
        Assert.Equal(["b", "c"], world.PcTrash().Order());
        Assert.False(Directory.Exists(world.Cloud(SyncFilters.TrashFolder)));

        // Nothing to do: a further run changes nothing.
        var quiet = await world.RunAsync();
        Assert.True(quiet.Success, quiet.ErrorDetail);
        Assert.Equal(0, quiet.Final.Transfers);
        Assert.Equal(0, quiet.Deletes);
    }

    [Fact]
    public async Task With_the_recycle_bin_switched_off_nothing_is_kept()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("A.txt", "a");
        world.WriteCloud("B.txt", "b");
        world.WriteCloud("C.txt", "c");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync, keepTrash: false)).Success);

        File.Delete(world.Cloud("A.txt"));
        world.WriteCloud("B.txt", "b - in der Cloud geändert", later: true);
        var outcome = await world.RunAsync(keepTrash: false);

        Assert.True(outcome.Success, outcome.ErrorDetail);
        Assert.False(File.Exists(world.Pc("A.txt")));
        Assert.Equal("b - in der Cloud geändert", world.ReadPc("B.txt"));
        Assert.Empty(world.PcTrash());
    }

    [Fact]
    public async Task A_change_on_the_server_with_the_same_size_arrives()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("Gleich.txt", "Version 1");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);

        world.WriteCloud("Gleich.txt", "Version 2", later: true);
        var outcome = await world.RunAsync();

        Assert.True(outcome.Success, outcome.ErrorDetail);
        Assert.Equal("Version 2", world.ReadPc("Gleich.txt"));
        Assert.Equal("Version 2", world.ReadCloud("Gleich.txt"));
        Assert.Equal(["Version 1"], world.PcTrash());
        Assert.Empty(outcome.Conflicts);

        // Settled: the next runs neither fetch nor upload anything again.
        Assert.True((await world.RunAsync()).Success);
        var quiet = await world.RunAsync();
        Assert.True(quiet.Success, quiet.ErrorDetail);
        Assert.Equal(0, quiet.Final.Transfers);
        Assert.Equal("Version 2", world.ReadPc("Gleich.txt"));
    }

    [Fact]
    public async Task A_same_size_change_on_the_server_and_a_change_on_the_PC_keep_both_versions()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("Plan.txt", "Mo 08:00");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);

        world.WriteCloud("Plan.txt", "Mo 09:00", later: true);
        world.WritePc("Plan.txt", "Mo 08:00 - Raum 12");
        var outcome = await world.RunAsync();

        Assert.True(outcome.Success, outcome.ErrorDetail);
        Assert.Equal(["Plan.Konflikt-Cloud1.txt"], outcome.Conflicts);
        Assert.Equal("Mo 09:00", world.ReadPc("Plan.Konflikt-Cloud1.txt"));
        Assert.Equal("Mo 08:00 - Raum 12", world.ReadPc("Plan.txt"));
        Assert.Equal(world.CloudFiles(), world.PcFiles());
        Assert.Equal("Mo 08:00 - Raum 12", world.ReadCloud("Plan.txt"));
    }

    [Fact]
    public async Task A_file_changed_on_both_sides_keeps_both_versions_with_their_extension()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("Bericht.docx", "Ausgangsfassung");
        world.WriteCloud("Anderes.txt", "bleibt");
        await world.AddAccountAsync();
        await world.AddPairAsync();
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);

        world.WritePc("Bericht.docx", "PC-Fassung");
        world.WriteCloud("Bericht.docx", "Cloud-Fassung, länger", later: true);
        var outcome = await world.RunAsync();

        Assert.True(outcome.Success, outcome.ErrorDetail);
        Assert.NotEmpty(outcome.Conflicts);
        Assert.All(outcome.Conflicts, c => Assert.Matches(@"^Bericht\.Konflikt-(Cloud|PC)\d+\.docx$", c));
        var versions = world.PcFiles().Where(f => f.StartsWith("Bericht", StringComparison.Ordinal)).Select(world.ReadPc).ToList();
        Assert.Contains("PC-Fassung", versions);
        Assert.Contains("Cloud-Fassung, länger", versions);
        Assert.Equal(world.CloudFiles(), world.PcFiles());
        Assert.Equal(outcome.Conflicts, SyncRunner.FindConflicts(world.Local));
    }

    [Fact]
    public async Task Only_the_chosen_folders_and_files_are_synchronised()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("Mathe/Aufgaben.txt", "1+1");
        world.WriteCloud("Deutsch/Gedicht.txt", "Erlkönig");
        world.WriteCloud("Plan.txt", "Stundenplan");
        world.WriteCloud("Sonstiges.txt", "nicht gewählt");
        await world.AddAccountAsync();
        await world.AddPairAsync(p => p.Selection = new SyncSelection { Mode = SelectionMode.Selected, Include = ["Mathe/", "Plan.txt"] });

        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);
        Assert.Equal(["Mathe/Aufgaben.txt", "Plan.txt"], world.PcFiles());

        world.WritePc("Mathe/Lösungen.txt", "2");
        world.WritePc("Nur lokal.txt", "bleibt am PC");
        var outcome = await world.RunAsync();

        Assert.True(outcome.Success, outcome.ErrorDetail);
        Assert.Equal("2", world.ReadCloud("Mathe/Lösungen.txt"));
        Assert.False(File.Exists(world.Cloud("Nur lokal.txt")));
        Assert.Equal("Erlkönig", world.ReadCloud("Deutsch/Gedicht.txt"));
    }

    [Fact]
    public async Task Excluded_patterns_and_office_lock_files_stay_local()
    {
        await using var world = await SyncWorld.CreateAsync();
        await world.AddAccountAsync();
        await world.AddPairAsync(p => p.Selection.Exclude.Add("*.bak"));
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);

        world.WritePc("Brief.docx", "Text");
        world.WritePc("~$Brief.docx", "Sperrdatei von Word");
        world.WritePc("Brief.bak", "Sicherung");
        world.WritePc("Thumbs.db", "Vorschaubilder");
        var outcome = await world.RunAsync();

        Assert.True(outcome.Success, outcome.ErrorDetail);
        Assert.Equal(["Brief.docx"], world.CloudFiles());
    }
}
