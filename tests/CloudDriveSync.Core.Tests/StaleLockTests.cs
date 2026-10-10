using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.Tests;

public class StaleLockTests
{
    [Fact]
    public void Only_locks_of_an_earlier_engine_are_removed()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cd-sync-locks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "früher.lck"), """{"Session":"a","PID":"4711"}""");
            File.WriteAllText(Path.Combine(folder, "jetzt.lck"), """{"Session":"b","PID":"815"}""");
            File.WriteAllText(Path.Combine(folder, "kaputt.lck"), "kein JSON");
            RunSafety.RemoveStaleLocks(folder, 815);
            Assert.Equal(["jetzt.lck", "kaputt.lck"], Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order());

            // Without a running engine nothing is known - nothing goes.
            File.WriteAllText(Path.Combine(folder, "früher.lck"), """{"Session":"a","PID":"4711"}""");
            RunSafety.RemoveStaleLocks(folder, null);
            Assert.Equal(3, Directory.EnumerateFiles(folder).Count());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
