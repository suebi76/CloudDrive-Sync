using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.Tests;

public class RunChangesTests
{
    private const string Cloud = "cd-iserv:Eigene Dateien";
    private const string Local = @"C:\pc\Schule";

    [Fact]
    public void Reads_which_files_a_normal_run_moved_where()
    {
        const string report = """
            2026/10/05 21:02:50 INFO  : - Path1             Queue copy to Path2                         - C:\pc\Schule\Geändert Server.txt
            2026/10/05 21:02:50 INFO  : - Path2             Queue delete                                - C:\pc\Schule\Weg vom Server.txt
            2026/10/05 21:02:50 INFO  : - Path2             Queue copy to Path1                         - cd-iserv:Eigene Dateien/Ordner/Beide.txt
            2026/10/05 21:02:50 INFO  : - Path1             Queue delete                                - cd-iserv:Eigene Dateien/Weg vom PC.txt
            2026/10/05 21:02:50 INFO  : - Path2             Do queued copies to                         - Path1
            2026/10/05 21:02:50 INFO  : Ordner/Beide.txt: Copied (replaced existing)
            2026/10/05 21:02:50 INFO  : Weg vom PC.txt: Deleted
            """;
        Assert.Equal(new[]
        {
            new FileChange(ChangeKind.Downloaded, "Geändert Server.txt"),
            new FileChange(ChangeKind.DeletedOnPc, "Weg vom Server.txt"),
            new FileChange(ChangeKind.Uploaded, "Ordner/Beide.txt"),
            new FileChange(ChangeKind.DeletedInCloud, "Weg vom PC.txt"),
        }, RunChanges.FromReport(report, Cloud, Local));
    }

    [Fact]
    public void Reads_what_a_first_run_copied_in_each_direction()
    {
        const string report = """
            2026/10/05 21:02:49 INFO  : - Path2             Resync is copying files to                  - Path1
            2026/10/05 21:02:49 INFO  : Nur PC.txt: Copied (new)
            2026/10/05 21:02:49 INFO  : - Path1             Resync is copying files to                  - Path2
            2026/10/05 21:02:49 INFO  : Nur Cloud.txt: Copied (new)
            2026/10/05 21:02:49 INFO  : Ordner/Beide.txt: Copied (new)
            2026/10/05 21:02:49 INFO  : .clouddrive-sync: Copied (new)
            2026/10/05 21:02:49 INFO  : Resync updating listings
            2026/10/05 21:02:49 INFO  : Danach.txt: Copied (new)
            """;
        Assert.Equal(new[]
        {
            new FileChange(ChangeKind.Uploaded, "Nur PC.txt"),
            new FileChange(ChangeKind.Downloaded, "Nur Cloud.txt"),
            new FileChange(ChangeKind.Downloaded, "Ordner/Beide.txt"),
        }, RunChanges.FromReport(report, Cloud, Local));
    }

    [Fact]
    public void Knows_the_cloud_side_of_a_whole_account() =>
        Assert.Equal(new[] { new FileChange(ChangeKind.Uploaded, "Files/a.txt") },
            RunChanges.FromReport("INFO  : - Path2  Queue copy to Path1  - cd-iserv:Files/a.txt", "cd-iserv:", Local));
}
