using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.Tests;

public class LocalOnlyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"cd-sync-localonly-{Guid.NewGuid():N}");

    public LocalOnlyTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Finds_the_uploads_the_server_refused()
    {
        const string report = """
            2026/10/05 20:36:24 INFO  : - Path2             Queue copy to Path1                         - cd-iserv:Groups/Gruppe A/Meins.txt
            2026/10/05 20:36:24 INFO  : - Path1             Queue copy to Path2                         - C:\pc\Gruppe B\Neu.txt
            2026/10/05 20:36:24 ERROR : Gruppe A/Meins.txt: Failed to copy: unchunked simple update failed: Failed to write file.: IServ\Library\Sudo\Exception\SudoException: 500 Internal Server Error
            2026/10/05 20:36:25 ERROR : Gruppe A/Meins.txt: Failed to copy: unchunked simple update failed: Failed to write file.: IServ\Library\Sudo\Exception\SudoException: 500 Internal Server Error
            2026/10/05 20:36:25 ERROR : Gruppe B/Neu.txt: Failed to copy: 403 Forbidden
            """;
        // Only uploads count: the failed download of Neu.txt is something else.
        Assert.Equal(["Gruppe A/Meins.txt"], RunSafety.RefusedUploads(report, "cd-iserv:Groups"));
        Assert.Empty(RunSafety.RefusedUploads("2026/10/05 ERROR : x.txt: Failed to copy: dial tcp: i/o timeout", "cd-iserv:"));
    }

    [Fact]
    public void Files_that_stay_on_the_PC_are_left_out()
    {
        var pair = new SyncPairSettings { LocalOnly = ["Gruppe A/Meins [1].txt"] };
        Assert.Contains("- /Gruppe A/Meins \\[1\\].txt", SyncFilters.Build(pair));
    }

    [Fact]
    public void Leaving_files_out_needs_no_rebuild_but_a_new_selection_does()
    {
        var filters = Path.Combine(_folder, "filter.txt");
        var pair = new SyncPairSettings();
        // A filter of an earlier version, with bisync's checksum of it.
        File.WriteAllText(filters, SyncFilters.Build(pair.Selection, pair.CloudCheckFile));
        File.WriteAllText(filters + ".md5", "checksum-of-bisync");
        pair.LocalOnly.Add("Gruppe A/Meins.txt");
        SyncFilters.Write(_folder, filters, pair);
        Assert.Contains("- /Gruppe A/Meins.txt", File.ReadAllText(filters));
        Assert.Equal(Md5(filters), File.ReadAllText(filters + ".md5"));
        // A new selection: bisync has to rebuild, its checksum stays as it is.
        File.WriteAllText(filters + ".md5", "checksum-of-bisync");
        pair.Selection = new SyncSelection { Mode = SelectionMode.Selected, Include = ["Gruppe B/"] };
        SyncFilters.Write(_folder, filters, pair);
        Assert.Equal("checksum-of-bisync", File.ReadAllText(filters + ".md5"));
    }

    private static string Md5(string file) => Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(file)));
}
