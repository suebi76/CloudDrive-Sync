using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.Tests;

public class PcListingTimesTests : IDisposable
{
    private const string ServerSecond = "2026-10-06T09:16:26.000000000+0000";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cd-sync-listing-{Guid.NewGuid():N}");
    private readonly string _work;
    private readonly string _pc;

    public PcListingTimesTests()
    {
        _work = Path.Combine(_root, "bisync");
        _pc = Path.Combine(_root, "pc");
        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(_pc);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Gives_a_PC_entry_with_the_server_time_the_real_older_time_of_the_file()
    {
        var real = PcFile("Plan.txt", "12345678", new DateTime(2026, 10, 6, 9, 16, 25, 979, DateTimeKind.Utc));
        WriteListings(cloud: [Entry(8, ServerSecond, "Plan.txt")], pc: [Entry(8, ServerSecond, "Plan.txt")]);

        Assert.Equal(1, PcListingTimes.Align(_work, _pc));
        Assert.Contains(Entry(8, PcListingTimes.FormatTime(real), "Plan.txt"), PcListing());
        Assert.Equal(0, PcListingTimes.Align(_work, _pc));
    }

    [Fact]
    public void Leaves_every_other_entry_alone()
    {
        var older = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);
        // Changed on the PC after the run: newer than the noted time - bisync must see the change.
        PcFile("Neuer.txt", "1234", new DateTime(2026, 10, 6, 9, 20, 0, DateTimeKind.Utc));
        // The PC entry has a time of its own, not the server's.
        PcFile("Eigen.txt", "1234", older);
        // Another size: a real change.
        PcFile("Groesse.txt", "123456", older);
        // A name with an escape bisync writes (a tab - Windows allows none in names): left alone, the file is not even looked for.
        WriteListings(
            cloud: [Entry(4, ServerSecond, "Neuer.txt"), Entry(4, ServerSecond, "Eigen.txt"), Entry(4, ServerSecond, "Groesse.txt"), Entry(4, ServerSecond, "Tab\\tName.txt"), Entry(4, ServerSecond, "Fehlt.txt")],
            pc: [Entry(4, ServerSecond, "Neuer.txt"), Entry(4, "2026-10-06T08:00:00.000000000+0000", "Eigen.txt"), Entry(4, ServerSecond, "Groesse.txt"), Entry(4, ServerSecond, "Tab\\tName.txt"), Entry(4, ServerSecond, "Fehlt.txt")]);
        var before = File.ReadAllText(PcListingFile());

        Assert.Equal(0, PcListingTimes.Align(_work, _pc));
        Assert.Equal(before, File.ReadAllText(PcListingFile()));
    }

    [Fact]
    public void Keeps_bisyncs_own_line_ends_and_header()
    {
        PcFile("Ordner/Übung 1.txt", "12", new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc));
        WriteListings(cloud: [Entry(2, ServerSecond, "Ordner/Übung 1.txt")], pc: [Entry(2, ServerSecond, "Ordner/Übung 1.txt")]);

        Assert.Equal(1, PcListingTimes.Align(_work, _pc));
        var text = File.ReadAllText(PcListingFile());
        Assert.DoesNotContain("\r", text);
        Assert.StartsWith("# bisync listing v1 from", text);
        Assert.EndsWith("\n", text);
    }

    [Theory]
    [InlineData("2026-10-06T09:16:26.123456700+0000", "2026-10-06T09:16:26.123456700+0000")]
    [InlineData("2026-10-06T11:16:26.500000000+0200", "2026-10-06T09:16:26.500000000+0000")]
    [InlineData("2026-10-06T09:16:26+0000", "2026-10-06T09:16:26.000000000+0000")]
    public void Reads_and_writes_times_as_bisync_does(string read, string written) =>
        Assert.Equal(written, PcListingTimes.FormatTime(PcListingTimes.ParseTime(read)!.Value));

    [Theory]
    [InlineData("\"Plan.txt\"", "Plan.txt")]
    [InlineData("\"a \\\"b\\\" \\\\ c.txt\"", "a \"b\" \\ c.txt")]
    [InlineData("\"Tab\\tName.txt\"", null)]
    [InlineData("Plan.txt", null)]
    public void Reads_quoted_paths(string quoted, string? path) => Assert.Equal(path, PcListingTimes.Unquote(quoted));

    private DateTime PcFile(string relative, string content, DateTime time)
    {
        var file = Path.Combine(_pc, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        File.SetLastWriteTimeUtc(file, time);
        return time;
    }

    private static string Entry(long size, string time, string quotedPath) => $"-{size,9} - - {time} \"{quotedPath}\"";

    private void WriteListings(string[] cloud, string[] pc)
    {
        const string header = "# bisync listing v1 from 2026-10-06T09:16:26.586609300+0000\n";
        File.WriteAllText(Path.Combine(_work, "cloud..pc.path1.lst"), header + string.Join('\n', cloud) + "\n");
        File.WriteAllText(PcListingFile(), header + string.Join('\n', pc) + "\n");
    }

    private string PcListingFile() => Path.Combine(_work, "cloud..pc.path2.lst");

    private string[] PcListing() => File.ReadAllText(PcListingFile()).Split('\n');
}
