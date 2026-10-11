using System.Diagnostics;
using System.Runtime.InteropServices;

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
}
