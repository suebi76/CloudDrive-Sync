using CloudDriveSync.Core.Diagnostics;

namespace CloudDriveSync.Core.Tests;

public class LogOnceTests
{
    [Fact]
    public void A_line_that_comes_again_unchanged_is_news_once()
    {
        var once = new LogOnce();
        Assert.True(once.IsNew("Ordner A", "1 still held"));
        Assert.False(once.IsNew("Ordner A", "1 still held"));
        // Another matter, the same words: news.
        Assert.True(once.IsNew("Ordner B", "1 still held"));
        // Changed: news, and then not again.
        Assert.True(once.IsNew("Ordner A", "2 still held"));
        Assert.False(once.IsNew("Ordner A", "2 still held"));
        // Settled and back: news again.
        once.Settled("Ordner A");
        Assert.True(once.IsNew("Ordner A", "2 still held"));
    }
}
