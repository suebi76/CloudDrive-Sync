using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.Tests;

public class CleanUpPersistenceTests
{
    [Fact]
    public void Saving_an_empty_list_does_not_require_or_create_a_home_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "cd-sync-cleanup-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "cleanup.json");

        SyncService.SaveCleanUps(file, []);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Saving_replaces_the_complete_list_and_leaves_no_temporary_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "cd-sync-cleanup-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "cleanup.json");
        try
        {
            var first = new List<SyncService.PendingCleanUp> { new("registration-1", @"C:\Folder 1", "pair-1") };
            var second = new List<SyncService.PendingCleanUp> { new("registration-2", @"C:\Folder 2", "pair-2") };

            SyncService.SaveCleanUps(file, first);
            Assert.Equal(first, SyncService.LoadCleanUps(file));
            SyncService.SaveCleanUps(file, second);

            Assert.Equal(second, SyncService.LoadCleanUps(file));
            Assert.Equal(["cleanup.json"], Directory.GetFileSystemEntries(root).Select(Path.GetFileName));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[{}]")]
    public void A_damaged_list_is_never_treated_as_empty(string contents)
    {
        var root = Path.Combine(Path.GetTempPath(), "cd-sync-cleanup-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "cleanup.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(file, contents);
            Assert.Throws<IOException>(() => SyncService.LoadCleanUps(file));
            Assert.Equal(contents, File.ReadAllText(file));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
