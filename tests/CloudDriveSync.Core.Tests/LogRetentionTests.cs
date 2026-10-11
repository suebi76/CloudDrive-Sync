using System.Globalization;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;

namespace CloudDriveSync.Core.Tests;

/// <summary>The log is one for the whole program: these tests run alone.</summary>
[CollectionDefinition(nameof(LogRetentionTests), DisableParallelization = true)]
public class LogRetentionCollection;

[Collection(nameof(LogRetentionTests))]
public class LogRetentionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"cd-sync-logs-{Guid.NewGuid():N}");

    public LogRetentionTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        Log.Clock = TimeProvider.System;
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Logs_older_than_two_weeks_go_also_rclones_and_also_while_running_for_weeks()
    {
        foreach (var name in new[] { "clouddrive-sync-2026-01-01.log", "rclone-2026-01-01T10-00-00.000.log", "rclone.log" })
        {
            File.WriteAllText(Path.Combine(_folder, name), "alt");
            File.SetLastWriteTime(Path.Combine(_folder, name), DateTime.Now.AddDays(-15));
        }
        Log.Initialize(_folder);
        // rclone's open part stays (the engine holds it, see the next test); every closed part goes.
        Assert.Equal(["rclone.log"], Directory.GetFiles(_folder).Select(Path.GetFileName));

        // Running on for three weeks: the first line of a new day takes what is older than two weeks.
        Log.Info("Test", "heute");
        Log.Clock = new Later(TimeSpan.FromDays(21));
        Log.Info("Test", "in drei Wochen");
        Assert.Equal([$"clouddrive-sync-{DateTime.Now.AddDays(21):yyyy-MM-dd}.log", "rclone.log"], Directory.GetFiles(_folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Rclones_open_part_is_closed_at_the_start_once_its_first_entry_is_older_than_two_weeks()
    {
        var log = Path.Combine(_folder, "rclone.log");
        // rclone writes its times the same in every language: "2026/10/11 02:08:55".
        static string At(int daysAgo) => DateTime.Now.AddDays(-daysAgo).ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        File.WriteAllText(log, $"{At(3)} INFO  : neu\n");
        RcloneEngine.CloseOldLog(log, Log.Keep);
        Assert.True(File.Exists(log));

        File.WriteAllText(log, $"{At(20)} INFO  : alt\n{At(0)} INFO  : neu\n");
        RcloneEngine.CloseOldLog(log, Log.Keep);
        Assert.False(File.Exists(log));
        Assert.Single(Directory.GetFiles(_folder, "rclone-*.log"));
    }

    private sealed class Later(TimeSpan by) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + by;
    }
}
