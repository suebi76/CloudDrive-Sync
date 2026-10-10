using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.Tests;

public class LocalFolderCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cd2-check-" + Guid.NewGuid().ToString("N")[..8]);

    public LocalFolderCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Warns_about_full_folders()
    {
        var folder = Path.Combine(_root, "Sync");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        var warnings = LocalFolderCheck.Check(folder, []);
        Assert.Contains(warnings, w => w.Kind == "not-empty");
    }

    [Fact]
    public void Refuses_same_parent_and_child_of_another_pair()
    {
        var existing = Path.Combine(_root, "Sync");
        var pair = new SyncPairSettings { Id = "x", LocalPath = existing };
        foreach (var candidate in new[] { existing, Path.Combine(existing, "Inner"), _root })
            Assert.Equal("CD-4506", Assert.Throws<CdException>(() => LocalFolderCheck.Check(candidate, [pair])).Code);

        // An adjacent folder is independent, despite sharing the same name prefix.
        LocalFolderCheck.EnsureNoPairOverlap(existing + " 2", [pair]);
    }

    [Fact]
    public void A_drive_root_contains_its_subfolders()
    {
        var driveRoot = Path.GetPathRoot(_root)!;
        Assert.True(LocalFolderCheck.IsSameOrInside(_root, driveRoot));
    }

    [Fact]
    public void Default_path_skips_existing_directories_and_other_pairs()
    {
        var baseFolder = Path.Combine(_root, "CloudDrive-Sync", "Nextcloud");
        Directory.CreateDirectory(baseFolder);
        var second = baseFolder + " (2)";
        var pair = new SyncPairSettings { Id = "other", LocalPath = second };

        Assert.Equal(baseFolder + " (3)", LocalFolderCheck.SuggestDefaultPath(_root, "Nextcloud", null, [pair]));
        Assert.Equal(Path.Combine(_root, "CloudDrive-Sync", "Nextcloud - Dokumente"),
            LocalFolderCheck.SuggestDefaultPath(_root, "Nextcloud", "Dokumente", [pair]));
    }

    [Fact]
    public void Default_path_never_picks_a_child_of_an_existing_pair()
    {
        var pair = new SyncPairSettings { Id = "whole", LocalPath = Path.Combine(_root, "CloudDrive-Sync") };
        Assert.Null(LocalFolderCheck.SuggestDefaultPath(_root, "Nextcloud", null, [pair]));
    }

    [Fact]
    public void Default_path_avoids_reserved_Windows_names()
    {
        Assert.Equal(Path.Combine(_root, "CloudDrive-Sync", "_CON"), LocalFolderCheck.SuggestDefaultPath(_root, "CON", null, []));
    }

    [Fact]
    public void Rejects_a_cloud_marked_file_deep_inside_an_existing_folder()
    {
        var folder = Path.Combine(_root, "scan");
        var deep = Path.Combine(folder, "a", "b");
        Directory.CreateDirectory(deep);
        var file = Path.Combine(deep, "online-only.txt");
        File.WriteAllText(file, "test");
        File.SetAttributes(file, FileAttributes.Offline);
        try
        {
            Assert.Equal("CD-4513", Assert.Throws<CdException>(() => LocalFolderCheck.EnsureExistingTreeSafe(folder)).Code);
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public void An_empty_new_folder_is_fine() =>
        Assert.Empty(LocalFolderCheck.Check(Path.Combine(_root, "Neu"), []));

    [Fact]
    public void Only_impossible_paths_are_errors() =>
        Assert.Equal("CD-4501", Assert.Throws<CdException>(() => LocalFolderCheck.Check("relativ\\pfad", [])).Code);

    [Fact]
    public void System_folders_get_a_warning() =>
        Assert.Contains(LocalFolderCheck.Check(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "CloudDrives-Test"), []), w => w.Kind == "system");
}
