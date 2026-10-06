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
    public void Warns_but_allows_overlaps_and_full_folders()
    {
        var folder = Path.Combine(_root, "Sync");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        var other = new SyncPairSettings { Id = "x", LocalPath = Path.Combine(folder, "Inner") };
        var warnings = LocalFolderCheck.Check(folder, [other]);
        Assert.Contains(warnings, w => w.Kind == "overlap");
        Assert.Contains(warnings, w => w.Kind == "not-empty");
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
