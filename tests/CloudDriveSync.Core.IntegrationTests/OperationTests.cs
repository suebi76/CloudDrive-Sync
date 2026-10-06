using System.Security.Cryptography;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>
/// Everything people do with files, one by one, against a server that behaves like IServ: after one run both sides
/// hold exactly the same folders and files, and the next run has nothing left to do.
/// </summary>
public class OperationTests
{
    private static async Task<SyncWorld> ReadyAsync(params (string Path, string Text)[] cloud)
    {
        var world = await SyncWorld.CreateAsync();
        foreach (var (path, text) in cloud) world.WriteCloud(path, text);
        await world.AddAccountAsync();
        await world.AddPairAsync();
        var first = await world.RunAsync(BisyncMode.Resync);
        Assert.True(first.Success, first.ErrorDetail);
        world.AssertInStep();
        return world;
    }

    [Fact]
    public async Task Copying_a_file_in_the_same_folder_keeps_its_time_and_still_goes_up()
    {
        await using var world = await ReadyAsync(("Bericht.docx", "Inhalt des Berichts"));
        // Explorer keeps the modification time of the original when copying.
        File.Copy(world.Pc("Bericht.docx"), world.Pc("Bericht - Kopie.docx"));
        var outcome = await world.SyncAndAssertInStepAsync();
        Assert.Equal(1, outcome.Final.Transfers);
        Assert.Equal("Inhalt des Berichts", world.ReadCloud("Bericht - Kopie.docx"));
    }

    [Fact]
    public async Task Copying_into_a_subfolder()
    {
        await using var world = await ReadyAsync(("Bericht.docx", "Inhalt"), ("Archiv/alt.txt", "alt"));
        File.Copy(world.Pc("Bericht.docx"), world.Pc("Archiv/Bericht.docx"));
        await world.SyncAndAssertInStepAsync();
    }

    [Fact]
    public async Task Renaming_and_moving_files()
    {
        await using var world = await ReadyAsync(("A.txt", "a"), ("B.txt", "b"), ("C.txt", "c"), ("D.txt", "d"), ("Ziel/x.txt", "x"));
        File.Move(world.Pc("A.txt"), world.Pc("A umbenannt.txt"));
        File.Move(world.Pc("B.txt"), world.Pc("Ziel/B.txt"));
        await world.SyncAndAssertInStepAsync();
        Assert.False(File.Exists(world.Cloud("A.txt")));
        Assert.False(File.Exists(world.Cloud("B.txt")));
    }

    [Fact]
    public async Task Renaming_a_folder_with_files()
    {
        await using var world = await ReadyAsync(("Projekt/a.txt", "a"), ("Projekt/Unter/b.txt", "b"), ("x.txt", "x"), ("y.txt", "y"), ("z.txt", "z"), ("w.txt", "w"));
        Directory.Move(world.Pc("Projekt"), world.Pc("Projekt 2026"));
        await world.SyncAndAssertInStepAsync();
        Assert.False(Directory.Exists(world.Cloud("Projekt")));
    }

    [Fact]
    public async Task New_folders_with_files_and_an_empty_folder()
    {
        await using var world = await ReadyAsync(("x.txt", "x"));
        world.WritePc("Neu/Ebene 2/Ebene 3/tief.txt", "tief");
        world.WritePc("Neu/oben.txt", "oben");
        Directory.CreateDirectory(world.Pc("Leer"));
        await world.SyncAndAssertInStepAsync();
        Assert.True(Directory.Exists(world.Cloud("Leer")));
    }

    [Fact]
    public async Task Deleting_a_folder_with_files()
    {
        await using var world = await ReadyAsync(("Weg/a.txt", "a"), ("Weg/b.txt", "b"), ("1.txt", "1"), ("2.txt", "2"), ("3.txt", "3"), ("4.txt", "4"), ("5.txt", "5"));
        Directory.Delete(world.Pc("Weg"), recursive: true);
        await world.SyncAndAssertInStepAsync();
        Assert.False(Directory.Exists(world.Cloud("Weg")));
    }

    [Fact]
    public async Task Changing_a_file_on_the_PC_without_changing_its_size()
    {
        await using var world = await ReadyAsync(("Plan.txt", "Mo 08:00"));
        await Task.Delay(1100);
        world.WritePc("Plan.txt", "Mo 09:00");
        await world.SyncAndAssertInStepAsync();
        Assert.Equal("Mo 09:00", world.ReadCloud("Plan.txt"));
    }

    [Fact]
    public async Task Names_with_umlauts_spaces_and_special_characters()
    {
        string[] names =
        [
            "Prüfung & Test #1 (Kopie) 100%.txt", "Ä Ö Ü ä ö ü ß € ½.txt", "Notiz 😀.txt", "a+b=c; d,e.txt",
            "Datei'mit'Apostroph.txt", "Klammern [1] {2}.txt", "Ordner mit Leerzeichen/Datei ~ Tilde.txt", "Größe/Übersicht.txt",
        ];
        await using var world = await ReadyAsync(("start.txt", "s"));
        foreach (var name in names) world.WritePc(name, name);
        await world.SyncAndAssertInStepAsync();
        foreach (var name in names) Assert.Equal(name, world.ReadCloud(name));
    }

    [Fact]
    public async Task Empty_and_large_files()
    {
        await using var world = await ReadyAsync(("start.txt", "s"));
        File.WriteAllBytes(world.Pc("leer.txt"), []);
        var large = RandomNumberGenerator.GetBytes(24 * 1024 * 1024);
        Directory.CreateDirectory(world.Pc("Video"));
        File.WriteAllBytes(world.Pc("Video/groß.bin"), large);
        await world.SyncAndAssertInStepAsync();
        Assert.Equal(0, new FileInfo(world.Cloud("leer.txt")).Length);
        Assert.Equal(large.Length, new FileInfo(world.Cloud("Video/groß.bin")).Length);
    }

    [Fact]
    public async Task Many_files_at_once()
    {
        await using var world = await ReadyAsync(("start.txt", "s"));
        for (var folder = 1; folder <= 10; folder++)
            for (var file = 1; file <= 15; file++)
                world.WritePc($"Ordner {folder:00}/Datei {file:00}.txt", $"{folder}-{file}");
        var outcome = await world.SyncAndAssertInStepAsync();
        Assert.Equal(150, outcome.Final.Transfers);
    }

    [Fact]
    public async Task Changing_only_upper_and_lower_case_of_a_name_on_the_PC()
    {
        await using var world = await ReadyAsync(("bericht.txt", "b"), ("x.txt", "x"), ("y.txt", "y"));
        Assert.True(world.CaseSensitiveCloud, "the test server must tell upper and lower case apart");
        File.Move(world.Pc("bericht.txt"), world.Pc("Bericht.txt"));
        await world.SyncAndAssertInStepAsync();
        Assert.Equal(["Bericht.txt", "x.txt", "y.txt"], world.CloudFiles());
        Assert.Equal("b", world.ReadCloud("Bericht.txt"));
    }

    [Fact]
    public async Task Changing_only_upper_and_lower_case_of_a_name_on_the_server()
    {
        await using var world = await ReadyAsync(("bericht.txt", "b"), ("x.txt", "x"), ("y.txt", "y"));
        Assert.True(world.CaseSensitiveCloud, "the test server must tell upper and lower case apart");
        File.Move(world.Cloud("bericht.txt"), world.Cloud("Bericht.txt"));
        File.SetLastWriteTimeUtc(world.Cloud("Bericht.txt"), DateTime.UtcNow.AddMinutes(2));
        await world.SyncAndAssertInStepAsync();
        Assert.Equal(["Bericht.txt", "x.txt", "y.txt"], world.PcFiles());
        Assert.Equal("b", world.ReadPc("Bericht.txt"));
    }

    [Fact]
    public async Task Two_server_files_differing_only_in_case_are_both_kept()
    {
        await using var world = await ReadyAsync(("x.txt", "x"), ("y.txt", "y"), ("z.txt", "z"));
        Assert.True(world.CaseSensitiveCloud, "the test server must tell upper and lower case apart");
        // Possible on IServ and Nextcloud, impossible in one Windows folder.
        world.WriteCloud("Notiz.txt", "groß geschrieben", later: true);
        world.WriteCloud("notiz.txt", "klein geschrieben", later: true);
        for (var run = 0; run < 3; run++) await world.RunAsync();
        Assert.Equal("groß geschrieben", world.ReadCloud("Notiz.txt"));
        Assert.Equal("klein geschrieben", world.ReadCloud("notiz.txt"));
        Assert.Contains(File.ReadAllText(world.Pc("Notiz.txt")), new[] { "groß geschrieben", "klein geschrieben" });
        // A change of the other one (same size) must never end up in the one on the PC - nor travel back to the server.
        world.WriteCloud("notiz.txt", "klein GESCHRIEBEN", later: true);
        for (var run = 0; run < 2; run++) await world.RunAsync();
        Assert.Equal("groß geschrieben", world.ReadCloud("Notiz.txt"));
        Assert.Equal("klein GESCHRIEBEN", world.ReadCloud("notiz.txt"));
    }

    [Fact]
    public async Task Everything_done_on_the_server_arrives_on_the_PC()
    {
        await using var world = await ReadyAsync(("A.txt", "a"), ("B.txt", "b"), ("Alt/c.txt", "c"), ("1.txt", "1"), ("2.txt", "2"), ("3.txt", "3"));
        world.WriteCloud("Neu vom Server/n.txt", "n", later: true);
        File.Move(world.Cloud("A.txt"), world.Cloud("A umbenannt.txt"));
        File.SetLastWriteTimeUtc(world.Cloud("A umbenannt.txt"), DateTime.UtcNow.AddMinutes(2));
        world.WriteCloud("B.txt", "b - auf dem Server geändert", later: true);
        Directory.Move(world.Cloud("Alt"), world.Cloud("Neu benannt"));
        await world.SyncAndAssertInStepAsync();
        Assert.Equal("b - auf dem Server geändert", world.ReadPc("B.txt"));
        Assert.False(Directory.Exists(world.Pc("Alt")));
    }

    [Fact]
    public async Task A_file_that_is_open_elsewhere_goes_up_once_it_is_free()
    {
        await using var world = await ReadyAsync(("x.txt", "x"));
        world.WritePc("Offen.docx", "wird gerade bearbeitet");
        using (new FileStream(world.Pc("Offen.docx"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // While another program holds the file exclusively, CloudDrive-Sync waits for it: nothing breaks, nothing is lost.
            var locked = await world.RunAsync();
            Assert.False(locked.Success);
            Assert.Equal("CD-4510", locked.ErrorCode);
            Assert.True(locked.Retryable);
            Assert.Equal(SyncDecision.None, locked.Decision);
            Assert.Contains("Offen.docx", locked.ErrorDetail);
        }
        await world.SyncAndAssertInStepAsync();
        Assert.Equal("wird gerade bearbeitet", world.ReadCloud("Offen.docx"));
    }

    [Fact]
    public async Task A_server_change_to_a_file_open_on_the_PC_arrives_once_it_is_free()
    {
        await using var world = await ReadyAsync(("Plan.docx", "Fassung 1"), ("x.txt", "x"));
        world.WriteCloud("Plan.docx", "Fassung 2 vom Server", later: true);
        using (new FileStream(world.Pc("Plan.docx"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var locked = await world.RunAsync();
            // Never a decision for the user, and nothing lost.
            Assert.Equal(SyncDecision.None, locked.Decision);
            Assert.True(locked.Success || locked.Retryable, $"{locked.ErrorCode}: {locked.ErrorDetail}");
            Assert.Equal("Fassung 2 vom Server", world.ReadCloud("Plan.docx"));
        }
        await world.SyncAndAssertInStepAsync();
        Assert.Equal("Fassung 2 vom Server", world.ReadPc("Plan.docx"));
    }
}
