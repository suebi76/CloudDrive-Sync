using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.Tests;

public class SyncTrashTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"cd-sync-trash-{Guid.NewGuid():N}");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Put(string stamp, string relative, string text)
    {
        var file = Path.Combine(_folder, SyncFilters.TrashFolder, stamp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    [Fact]
    public void Lists_files_newest_first_with_where_they_were()
    {
        Put("2026-10-05_10-00-00", @"Mathe\A.txt", "alt");
        Put("2026-10-05_12-30-00", "B.txt", "neu");
        File.WriteAllText(Path.Combine(_folder, SyncFilters.TrashFolder, "not-a-run.txt"), "x");
        var entries = SyncTrash.List("p", _folder);
        Assert.Equal([@"B.txt", @"Mathe\A.txt"], entries.Select(e => e.RelativePath));
        Assert.Equal(new DateTime(2026, 10, 5, 12, 30, 0), entries[0].Removed.LocalDateTime);
        Assert.Equal(3, entries[1].Size);
    }

    [Fact]
    public void Restores_to_the_old_place_without_overwriting_and_leaves_no_empty_folders()
    {
        Put("2026-10-05_10-00-00", @"Mathe\A.txt", "aus dem Papierkorb");
        Directory.CreateDirectory(Path.Combine(_folder, "Mathe"));
        File.WriteAllText(Path.Combine(_folder, "Mathe", "A.txt"), "aktuell");
        var entry = SyncTrash.List("p", _folder).Single();

        var restored = SyncTrash.Restore(_folder, entry);

        Assert.Equal(Path.Combine(_folder, "Mathe", "A (wiederhergestellt).txt"), restored);
        Assert.Equal("aus dem Papierkorb", File.ReadAllText(restored));
        Assert.Equal("aktuell", File.ReadAllText(Path.Combine(_folder, "Mathe", "A.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_folder, SyncFilters.TrashFolder)));
    }

    [Fact]
    public void Cleans_up_only_what_is_older_than_kept_and_empties_completely()
    {
        Put("2026-09-01_08-00-00", "alt.txt", "a");
        Put("2026-10-05_08-00-00", "neu.txt", "n");
        SyncTrash.CleanUp(_folder, TimeSpan.FromDays(30), new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 5))));
        Assert.Equal(["neu.txt"], SyncTrash.List("p", _folder).Select(e => e.RelativePath));
        SyncTrash.Empty(_folder);
        Assert.Empty(SyncTrash.List("p", _folder));
    }
}
