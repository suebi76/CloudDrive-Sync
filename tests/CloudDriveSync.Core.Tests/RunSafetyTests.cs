using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.Tests;

public class RunSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cd-sync-safety-{Guid.NewGuid():N}");

    public RunSafetyTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Finds_files_another_program_holds_exclusively()
    {
        File.WriteAllText(Path.Combine(_root, "frei.txt"), "f");
        File.WriteAllText(Path.Combine(_root, "offen.txt"), "o");
        File.WriteAllText(Path.Combine(_root, "gelesen.txt"), "g");
        using var exclusive = new FileStream(Path.Combine(_root, "offen.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        // Word and Excel let others read: such files are not in the way.
        using var shared = new FileStream(Path.Combine(_root, "gelesen.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Assert.Equal(["offen.txt"], RunSafety.LockedFiles(_root, ["frei.txt", "offen.txt", "gelesen.txt", "weg.txt"]));
    }

    [Fact]
    public void Puts_the_listings_of_the_last_good_run_back()
    {
        var work = Path.Combine(_root, "bisync");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "a..b.path1.lst"), "gut 1");
        File.WriteAllText(Path.Combine(work, "a..b.path2.lst"), "gut 2");
        RunSafety.RememberGoodState(work, _root);
        // A run that broke off leaves its listings renamed and half-written ones behind.
        File.Move(Path.Combine(work, "a..b.path1.lst"), Path.Combine(work, "a..b.path1.lst-err"));
        File.Move(Path.Combine(work, "a..b.path2.lst"), Path.Combine(work, "a..b.path2.lst-err"));
        File.WriteAllText(Path.Combine(work, "a..b.path1.lst-new"), "halb");
        Assert.True(RunSafety.RestoreGoodState(work, _root));
        Assert.Equal(["a..b.path1.lst", "a..b.path2.lst"], Directory.GetFiles(work).Select(Path.GetFileName).Order());
        Assert.Equal("gut 2", File.ReadAllText(Path.Combine(work, "a..b.path2.lst")));
    }

    [Fact]
    public void Without_a_good_state_nothing_is_put_back()
    {
        var work = Path.Combine(_root, "bisync");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "a..b.path1.lst-err"), "x");
        Assert.False(RunSafety.RestoreGoodState(work, _root));
        Assert.True(File.Exists(Path.Combine(work, "a..b.path1.lst-err")));
    }

    [Fact]
    public void Tells_passing_trouble_from_real_trouble()
    {
        const string locked = """
            ERROR : Offen.docx: Failed to copy: failed to open source object: open C:\x\Offen.docx: The process cannot access the file because it is being used by another process.
            ERROR : Bisync critical error: bisync aborted
            ERROR : Bisync aborted. Must run --resync to recover.
            """;
        Assert.True(RunSafety.IsCritical(locked));
        Assert.True(RunSafety.IsPassing(locked));
        Assert.True(RunSafety.IsFileInUse(locked));
        const string network = "ERROR : Bisync critical error: dial tcp: lookup webdav.example.org: i/o timeout";
        Assert.True(RunSafety.IsPassing(network));
        Assert.False(RunSafety.IsFileInUse(network));
        const string broken = "ERROR : Bisync critical error: path1 listing is corrupt";
        Assert.True(RunSafety.IsCritical(broken));
        Assert.False(RunSafety.IsPassing(broken));
    }
}
