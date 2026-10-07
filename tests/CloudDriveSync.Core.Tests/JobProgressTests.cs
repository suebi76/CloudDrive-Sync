using System.Text.Json.Nodes;
using CloudDriveSync.Core.Engine;

namespace CloudDriveSync.Core.Tests;

public class JobProgressTests
{
    [Fact]
    public void Entries_listed_so_far_come_from_rclones_stats()
    {
        var stats = JsonNode.Parse("""{ "bytes": 10, "totalBytes": 20, "transfers": 1, "totalTransfers": 2, "speed": 5.5, "checks": 3, "totalChecks": 4, "errors": 0, "listed": 1234 }""")!.AsObject();
        var progress = RcClient.ParseProgress(stats);
        Assert.Equal(1234, progress.Listed);
        Assert.Equal(3, progress.Checks);
        Assert.Equal(2, progress.TotalTransfers);
    }
}
