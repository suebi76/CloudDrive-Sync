using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.Tests;

public class IgnoreAndRetryTests
{
    [Fact]
    public void Failed_runs_are_tried_again_ever_later_never_in_a_loop()
    {
        static double Minutes(string? code, int failures, int interval = 60) => SyncService.RetryDelay(code, failures, interval).TotalMinutes;
        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60], Enumerable.Range(1, 8).Select(n => Minutes("CD-4605", n)));
        // A file open in another program: three quick tries, then the same growing waits.
        Assert.Equal([1, 1, 1, 2, 4, 8], Enumerable.Range(1, 6).Select(n => Minutes("CD-4510", n)));
        // Never longer than the interval.
        Assert.Equal(5, Minutes("CD-5001", 10, interval: 5));
    }

    [Fact]
    public void Rules_to_ignore_describe_themselves_and_find_every_case()
    {
        Assert.Equal("*.[dD][lL][lL]", SyncFilters.TypeRule(".dll"));
        Assert.Equal("*.[hH][tT][aA][cC][cC][eE][sS][sS]", SyncFilters.TypeRule(".htaccess"));
        Assert.Equal("*.[tT][aA][rR].[gG][zZ]", SyncFilters.TypeRule(".tar.gz"));
        Assert.Equal("*.7[zZ]", SyncFilters.TypeRule(".7z"));
        Assert.Equal("/Projekt/\\[alt\\]/**", SyncFilters.PathRule("Projekt/[alt]", folder: true));
        Assert.Equal("/Programm/Makefile", SyncFilters.PathRule("Programm/Makefile", folder: false));

        Assert.Equal("Dateien vom Typ .dll", SyncFilters.Describe("*.[dD][lL][lL]"));
        Assert.Equal("Dateien vom Typ .7z", SyncFilters.Describe("*.7[zZ]"));
        Assert.Equal("Ordner „Projekt/[alt]“", SyncFilters.Describe("/Projekt/\\[alt\\]/**"));
        Assert.Equal("Datei „Programm/Makefile“", SyncFilters.Describe("/Programm/Makefile"));
        Assert.Equal("Dateien vom Typ .bak", SyncFilters.Describe("*.bak"));
        // A rule written another way is shown as it is.
        Assert.Equal("**/Cache/**", SyncFilters.Describe("**/Cache/**"));
    }
}
