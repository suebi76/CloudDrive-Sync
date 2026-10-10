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

    [Fact]
    public void The_file_being_transferred_is_named_with_its_direction()
    {
        static string? Current(string json) => RcClient.Transferring(JsonNode.Parse(json)!.AsObject());
        Assert.Equal("Lädt hoch: Bericht.docx",
            Current("""{ "transferring": [{ "name": "Ordner/Bericht.docx", "srcFs": "E:/Cloud/Schule", "dstFs": "cd-nc:Schule" }] }"""));
        Assert.Equal("Lädt herunter: Plan.pdf (+2)",
            Current("""{ "transferring": [{ "name": "Plan.pdf", "srcFs": "cd-iserv:Files", "dstFs": "//?/E:/Cloud/Schule" }, { "name": "a" }, { "name": "b" }] }"""));
        Assert.Equal("Überträgt: x.txt", Current("""{ "transferring": [{ "name": "x.txt" }] }"""));
        Assert.Null(Current("{}"));
        Assert.Null(Current(""" { "transferring": [{ "name": 42, "dstFs": [] }] } """));
        Assert.Null(RcClient.ParseProgress(JsonNode.Parse("""{ "bytes": 1 }""")!.AsObject()).Current);
    }
}
