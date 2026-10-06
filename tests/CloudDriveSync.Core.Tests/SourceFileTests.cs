using System.Runtime.CompilerServices;
using System.Text;

namespace CloudDriveSync.Core.Tests;

/// <summary>
/// The rules for source files (CONTRIBUTING.md): UTF-8 without byte order mark - PowerShell scripts with one, so Windows
/// PowerShell 5.1 reads umlauts right -, Windows line ends (YAML: Unix line ends), a line end at the end of every file.
/// tools\Format-SourceFiles.ps1 puts a file right.
/// </summary>
public class SourceFileTests
{
    private static readonly string[] Extensions = [".cs", ".xaml", ".csproj", ".props", ".slnx", ".json", ".md", ".txt", ".ps1", ".yml", ".yaml"];
    private static readonly string[] SkippedFolders = [".git", ".vs", ".claude", "bin", "obj", "TestResults"];

    [Fact]
    public void Every_source_file_follows_the_rules()
    {
        var root = RepositoryRoot();
        var problems = new List<string>();
        foreach (var file in SourceFiles(root))
        {
            var name = Path.GetRelativePath(root, file);
            var extension = Path.GetExtension(file).ToLowerInvariant();
            var bytes = File.ReadAllBytes(file);
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var wantsBom = extension == ".ps1";
            if (hasBom != wantsBom) problems.Add($"{name}: {(wantsBom ? "needs a" : "without")} byte order mark");
            string text;
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
            }
            catch (DecoderFallbackException)
            {
                problems.Add($"{name}: not UTF-8");
                continue;
            }
            var unixLineEnds = extension is ".yml" or ".yaml";
            var lineEnd = unixLineEnds ? "\n" : "\r\n";
            if (unixLineEnds ? text.Contains('\r') : text.Replace("\r\n", "").Contains('\n')) problems.Add($"{name}: line ends must be {(unixLineEnds ? "LF" : "CRLF")}");
            if (text.Length > 0 && !text.EndsWith(lineEnd, StringComparison.Ordinal)) problems.Add($"{name}: no line end at the end");
            if (text.EndsWith(lineEnd + lineEnd, StringComparison.Ordinal)) problems.Add($"{name}: empty lines at the end");
        }
        Assert.True(problems.Count == 0, "Run tools\\Format-SourceFiles.ps1:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
            .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar).SkipLast(1).Any(folder => SkippedFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)));

    /// <summary>The folder of the repository: two levels above this source file (the build output lies elsewhere).</summary>
    private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
