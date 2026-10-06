using CloudDriveSync.Core.Errors;

namespace CloudDriveSync.Core.Tests;

public class ErrorCatalogTests
{
    [Theory]
    [InlineData("Bisync critical error: too many deletes", "CD-4502")]
    [InlineData("Safety abort: all files were changed on Path1 \"x\". Run with --force if desired.", "CD-4509")]
    [InlineData("Bisync critical error: check file check failed\nBisync aborted. Error is retryable without --resync due to --resilient mode.", "CD-4503")]
    [InlineData("Bisync critical error: something\nBisync aborted. Must run --resync to recover.", "CD-4504")]
    [InlineData("prior lock file found: C:\\w\\x.lck", "CD-4508")]
    [InlineData("couldn't list files: No public access to this resource.: Sabre\\DAV\\Exception\\NotAuthenticated: 401 Unauthorized", "CD-3012")]
    [InlineData("error listing: : 401 Unauthorized", "CD-3012")]
    [InlineData("Post \"https://x/\": dial tcp: lookup x: no such host", "CD-5001")]
    [InlineData("operations/copyfile: unchunked simple update failed: Failed to write file.: IServ\\Library\\Sudo\\Exception\\SudoException: 500 Internal Server Error", "CD-4511")]
    [InlineData("Put \"https://webdav.example.org/Groups/.clouddrive-sync\": 403 Forbidden", "CD-4511")]
    [InlineData("something else entirely", "CD-9000")]
    public void Recognises_errors(string text, string code) => Assert.Equal(code, ErrorCatalog.Classify(text));

    [Fact]
    public void Every_code_has_texts()
    {
        foreach (var code in ErrorCatalog.Codes)
        {
            var entry = ErrorCatalog.Get(code);
            Assert.False(string.IsNullOrWhiteSpace(entry.Title), code);
            Assert.False(string.IsNullOrWhiteSpace(entry.Fix), code);
        }
    }
}
