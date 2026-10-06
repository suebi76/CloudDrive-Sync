using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.Tests;

public class CloudFolderTests
{
    [Fact]
    public void The_protection_file_stays_out_where_only_the_PC_has_it()
    {
        Assert.Contains("+ /.clouddrive-sync", SyncFilters.Build(new SyncSelection()));
        var without = SyncFilters.Build(new SyncSelection(), cloudCheckFile: false);
        Assert.Contains("- /.clouddrive-sync", without);
        Assert.DoesNotContain("+ /.clouddrive-sync", without);
    }

    [Fact]
    public void Bisync_checks_the_protection_file_only_where_it_lies_on_both_sides()
    {
        var account = new AccountSettings { Id = "iserv", Kind = WebDavKind.IServ };
        var pair = new SyncPairSettings { Id = "p", AccountId = "iserv", RemotePath = "Groups", LocalPath = @"C:\Gruppen" };
        Assert.True((bool?)BisyncCommand.Build(pair, account, "w", "f", BisyncMode.Normal)["checkAccess"]);
        pair.CloudCheckFile = false;
        Assert.False((bool?)BisyncCommand.Build(pair, account, "w", "f", BisyncMode.Normal)["checkAccess"]);
    }

    [Theory]
    [InlineData(WebDavKind.IServ, "Groups", "Gruppen")]
    [InlineData(WebDavKind.IServ, "Files", "Eigene Dateien")]
    [InlineData(WebDavKind.IServ, "Groups/7a/Files", "Gruppen › 7a › Files")]
    [InlineData(WebDavKind.IServ, "", "Alles")]
    [InlineData(WebDavKind.Nextcloud, "Groups", "Groups")]
    [InlineData(WebDavKind.Other, "Files/Unterricht", "Files › Unterricht")]
    public void Folders_carry_the_names_people_know(WebDavKind kind, string path, string shown) =>
        Assert.Equal(shown, CloudFolderNames.ShowPath(kind, path));

    [Fact]
    public void Knows_how_many_entries_the_cloud_side_had_at_the_last_good_run()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"cd-sync-cloud-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, CloudFolderCheck.KnownEntries(folder));
            Directory.CreateDirectory(Path.Combine(folder, "last-good"));
            File.WriteAllLines(Path.Combine(folder, "last-good", "a..b.path1.lst"),
            [
                "# bisync listing v1 from 2026-10-05 20:00:00.000000000 +0000 UTC",
                "- 4 - - 2026-10-05T20:00:00.000000000+0000 \"Gruppe A/Plan.txt\"",
                "- 5 - - 2026-10-05T20:00:00.000000000+0000 \"Gruppe B/Liste.txt\"",
            ]);
            Assert.Equal(2, CloudFolderCheck.KnownEntries(folder));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
