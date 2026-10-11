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
    public void Finds_the_uploads_the_server_refused_also_in_a_rebuild()
    {
        // A rebuild (the first run, too) names no queue: what fails while it copies the PC's files to the cloud is an upload,
        // what fails while it copies the cloud's files to the PC is not. Before, a refused file there asked for a rebuild -
        // which failed on it again.
        const string report = """
            2026/10/11 16:03:56 INFO  : Copying Path2 files to Path1
            2026/10/11 16:03:56 INFO  : - Path2             Resync is copying files to                  - Path1
            2026/10/11 16:03:56 INFO  : Liste.txt: Copied (new)
            2026/10/11 16:03:57 ERROR : .notizen.txt: Failed to copy: unchunked simple update failed: 404 Not Found
            2026/10/11 16:03:57 ERROR : Programm/werkzeug.dll: Failed to copy: unchunked simple update failed: <!DOCTYPE html>
            <html><h2>Seite nicht gefunden</h2></html>: 404 Not Found
            2026/10/11 16:03:57 INFO  : Copying Path1 files to Path2
            2026/10/11 16:03:57 INFO  : - Path1             Resync is copying files to                  - Path2
            2026/10/11 16:03:58 ERROR : Plan.txt: Failed to copy: 403 Forbidden
            2026/10/11 16:03:58 ERROR : Bisync aborted. Must run --resync to recover.
            """;
        Assert.Equal([".notizen.txt", "Programm/werkzeug.dll"], RunSafety.RefusedUploads(report, "cd-nc:Ablage"));
    }

    [Fact]
    public void An_error_page_over_several_lines_counts_by_the_status_at_its_end()
    {
        // A filter in front of Nextcloud blocks some types of files with an error page of its own.
        const string report = """
            2026/10/10 22:46:24 INFO  : - Path2             Queue copy to Path1                         - cd-nc:Ablage/Programm/a.dll
            2026/10/10 22:46:24 INFO  : - Path2             Queue copy to Path1                         - cd-nc:Ablage/Programm/b.txt
            2026/10/10 22:46:28 ERROR : Programm/a.dll: Failed to copy: unchunked simple update failed: <!DOCTYPE html>
            <html lang="de">
            <h2>Seite nicht gefunden</h2>
            </html>: 404 Not Found
            2026/10/10 22:46:29 ERROR : Programm/b.txt: Failed to copy: unchunked simple update failed: <!DOCTYPE html>
            <html><p>Gateway: permission denied upstream, read-only</p>
            </html>: 502 Bad Gateway
            """;
        // The page's words do not count - only the status: a 502 is passing trouble, tried again.
        Assert.Equal(["Programm/a.dll"], RunSafety.RefusedUploads(report, "cd-nc:Ablage"));
        Assert.True(RunSafety.IsRefusal("unchunked simple update failed: <!DOCTYPE html>\n<p>x</p>\n</html>: 403 Forbidden"));
        Assert.False(RunSafety.IsRefusal("unchunked simple update failed: <!DOCTYPE html>\n<p>403 Forbidden</p>\n</html>: 503 Service Unavailable"));
        // A name the server does not take (400 to the upload) - but not a 400 to a question after a finished upload.
        Assert.True(RunSafety.IsRefusal("operations/copyfile: unchunked simple update failed: <?xml version=\"1.0\"?>\n<s:message>.htaccess is a forbidden name</s:message>: 400 Bad Request"));
        Assert.False(RunSafety.IsRefusal("operations/copyfile: couldn't calculate checksum after upload: Bad Request: 400 Bad Request"));
    }

    [Fact]
    public void Files_that_stay_on_the_PC_are_left_out()
    {
        var pair = new SyncPairSettings { LocalOnly = ["Gruppe A/Meins [1].txt", "Gruppe A/Neuer Ordner"] };
        var filter = SyncFilters.Build(pair).ReplaceLineEndings("\n");
        Assert.Contains("- /Gruppe A/Meins \\[1\\].txt\n", filter);
        // A folder the server did not let be made stays on the PC with everything in it: "- /Ordner" alone leaves it in.
        Assert.Contains("- /Gruppe A/Neuer Ordner\n- /Gruppe A/Neuer Ordner/**\n", filter);
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
