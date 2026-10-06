using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.Tests;

public class DeleteGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cd2-guard-{Guid.NewGuid():N}");

    public DeleteGuardTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "state"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SyncPairSettings Pair(SelectionMode mode = SelectionMode.All, params string[] include) => new()
    {
        Id = "p",
        LocalPath = Path.Combine(_root, "pc"),
        MaxDeletePercent = 50,
        Selection = new SyncSelection { Mode = mode, Include = include.ToList() },
    };

    private void Write(params string[] files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(_root, "pc", file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }
    }

    private void Delete(params string[] files)
    {
        foreach (var file in files) File.Delete(Path.Combine(_root, "pc", file));
    }

    [Fact]
    public void Counts_files_not_folders_and_needs_three_deletions()
    {
        var pair = Pair();
        Write("A/1.txt", "B/2.txt", "C/3.txt", "D/4.txt");
        DeleteGuard.Remember(Path.Combine(_root, "state"), pair);
        Delete("A/1.txt", "B/2.txt");
        Assert.Null(DeleteGuard.TooManyMissing(Path.Combine(_root, "state"), pair));
        Delete("C/3.txt");
        Assert.Equal((3, 4), DeleteGuard.TooManyMissing(Path.Combine(_root, "state"), pair));
    }

    [Fact]
    public void Ignores_temporary_files_own_files_and_files_outside_the_selection()
    {
        var pair = Pair(SelectionMode.Selected, "Mathe/", "Plan.txt");
        Write("Mathe/1.txt", "Plan.txt", "Privat/a.txt", "Privat/b.txt", "Privat/c.txt", "~$Brief.docx", "x.tmp", ".clouddrive-papierkorb/old.txt");
        Assert.Equal(["Mathe/1.txt", "Plan.txt"], DeleteGuard.LocalFiles(pair).Keys.Order());
    }

    [Fact]
    public void Without_a_limit_or_a_remembered_list_nothing_stops()
    {
        var pair = Pair();
        Write("1.txt", "2.txt", "3.txt");
        Assert.Null(DeleteGuard.TooManyMissing(Path.Combine(_root, "state"), pair));
        DeleteGuard.Remember(Path.Combine(_root, "state"), pair);
        Delete("1.txt", "2.txt", "3.txt");
        pair.MaxDeletePercent = 100;
        Assert.Null(DeleteGuard.TooManyMissing(Path.Combine(_root, "state"), pair));
    }

    [Fact]
    public void Finds_new_files_and_files_changed_without_a_new_size()
    {
        var pair = Pair();
        var state = Path.Combine(_root, "state");
        Write("Plan.txt", "Alt.txt");
        Assert.Empty(DeleteGuard.ChangedSinceLastRun(state, pair));
        DeleteGuard.Remember(state, pair);
        Assert.Empty(DeleteGuard.ChangedSinceLastRun(state, pair));
        var plan = Path.Combine(_root, "pc", "Plan.txt");
        File.WriteAllText(plan, "Plan.tx!"); // same size
        File.SetLastWriteTimeUtc(plan, File.GetLastWriteTimeUtc(plan).AddSeconds(5));
        Write("Neu.txt");
        Assert.Equal(["Neu.txt", "Plan.txt"], DeleteGuard.ChangedSinceLastRun(state, pair).Order());
    }

    [Fact]
    public void A_list_of_an_earlier_version_counts_every_file_as_changed()
    {
        var pair = Pair();
        var state = Path.Combine(_root, "state");
        Write("1.txt", "2.txt");
        File.WriteAllLines(Path.Combine(state, "local-files.txt"), ["1.txt", "2.txt"]);
        Assert.Equal(["1.txt", "2.txt"], DeleteGuard.ChangedSinceLastRun(state, pair).Order());
        Assert.Null(DeleteGuard.TooManyMissing(state, pair));
    }
}
