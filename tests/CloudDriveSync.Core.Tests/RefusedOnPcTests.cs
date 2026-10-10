using System.Runtime.Versioning;
using CloudDriveSync.Core.OnDemand;

namespace CloudDriveSync.Core.Tests;

[SupportedOSPlatform("windows10.0.17763")]
public class RefusedOnPcTests
{
    private static LocalEntry Listed(string path) => new(path, false, 1, 1, null);

    [Fact]
    public void Only_what_is_neither_listed_nor_refused_again_counts_as_gone()
    {
        IReadOnlyList<string> before = ["Weg", "Weg/innen.txt", "Wieder da", "Noch gesperrt", "Darunter/Ordner"];
        var vanished = RefusedOnPc.Vanished(before, [Listed("Wieder da")], ["Noch gesperrt", "Darunter"]);
        Assert.Equal(["Weg", "Weg/innen.txt"], vanished);
    }

    [Fact]
    public void Saved_entries_come_back_as_cloud_paths_and_nothing_refused_leaves_no_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cd-sync-refused-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            RefusedOnPc.Save(folder, ["Kaputt/Datei.txt", "Gesperrt"], ["Kaputt/Datei.txt"]);
            Assert.Equal(["refused\tGesperrt", "broken\tKaputt\\Datei.txt"], File.ReadAllLines(RefusedOnPc.FileOf(folder)));
            Assert.Equal(["Gesperrt", "Kaputt/Datei.txt"], RefusedOnPc.Load(folder));

            RefusedOnPc.Save(folder, [], []);
            Assert.False(File.Exists(RefusedOnPc.FileOf(folder)));
            Assert.Empty(RefusedOnPc.Load(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
