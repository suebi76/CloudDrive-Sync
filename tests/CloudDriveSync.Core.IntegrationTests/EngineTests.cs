using System.Diagnostics;
using System.Runtime.InteropServices;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>The engine (rclone rcd) as the program relies on it: when it no longer answers, it is started again.</summary>
public partial class EngineTests
{
    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(nint process);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(nint process);

    [Fact]
    public async Task An_engine_that_no_longer_answers_is_started_again()
    {
        await using var world = await SyncWorld.CreateAsync();
        var engine = world.Host.Engine;
        var hungId = engine.ProcessId!.Value;
        // The test's own engine hangs (frozen): it is still there, but answers nothing.
        using var hung = Process.GetProcessById(hungId);
        Assert.Equal(0, NtSuspendProcess(hung.Handle));
        try
        {
            var rc = await engine.EnsureRunningAsync();
            Assert.NotEqual(hungId, engine.ProcessId);
            await rc.CallAsync("rc/noop");
            Assert.True(hung.WaitForExit(10_000), "the engine that hung was not ended");
        }
        finally
        {
            if (!hung.HasExited) NtResumeProcess(hung.Handle);
        }
    }

    [Theory]
    [InlineData(SyncMode.Classic)]
    [InlineData(SyncMode.OnDemand)]
    public async Task Runs_leave_no_error_about_statistics_in_the_engine_log(SyncMode mode)
    {
        // Every run wrote 'core/stats-reset: group "sync/..." not found' into rclone's log as an ERROR: CloudDrive-Sync
        // reset statistics that were not there (yet). Harmless - but an error in the log misleads everyone who reads it.
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("Plan.txt", "Plan");
        await world.AddAccountAsync();
        await world.AddPairAsync(p => p.Mode = mode);
        Assert.True((await world.RunAsync(BisyncMode.Resync)).Success);
        // Large enough for an upload with progress (its own group of statistics) in files on demand.
        world.WritePc("Gross.bin", new string('x', 5 << 20));
        Assert.True((await world.RunAsync()).Success);
        world.WritePc("Gross.bin", new string('y', 5 << 20));
        Assert.True((await world.RunAsync()).Success);
        using var log = new StreamReader(new FileStream(world.Host.Paths.RcloneLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        Assert.DoesNotContain("core/stats", await log.ReadToEndAsync());
    }
}
