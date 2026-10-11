using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
    // Someone has the file open in Nextcloud Office: it passes - never "the server does not take it".
    [InlineData("operations/copyfile: unchunked simple update failed: \"Plan.docx\" is locked: OCA\\DAV\\Connector\\Sabre\\Exception\\FileLocked: 423 Locked", "CD-4514")]
    [InlineData("ERROR : Bilder/Ausflug.jpg: Failed to copy: write C:\\Schule\\Bilder\\Ausflug.jpg: There is not enough space on the disk.", "CD-4515")]
    [InlineData("something else entirely", "CD-9000")]
    public void Recognises_errors(string text, string code) => Assert.Equal(code, ErrorCatalog.Classify(text));

    [Fact]
    public void Every_code_has_one_entry_and_every_code_used_has_one()
    {
        // The catalog is a dictionary initializer: a second entry of the same code is taken silently, and the later text
        // then stands for both errors (happened once: "CD-4513").
        var root = RepositoryRoot();
        var catalog = File.ReadAllText(Path.Combine(root, "src", "CloudDriveSync.Core", "Errors", "ErrorCatalog.cs"));
        var entries = Regex.Matches(catalog, @"\[""(CD-\d{4})""\]\s*=").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(entries);
        Assert.Empty(entries.GroupBy(code => code).Where(group => group.Count() > 1).Select(group => group.Key));
        var used = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"""(CD-\d{4})""").Select(m => m.Groups[1].Value))
            .Distinct();
        Assert.Empty(used.Except(entries).Order());
    }

    private static string RepositoryRoot([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

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
