using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.Tests;

public class SyncFilterTests
{
    [Fact]
    public void Everything_keeps_only_the_standard_exclusions()
    {
        var text = SyncFilters.Build(new SyncSelection { Mode = SelectionMode.All });
        Assert.Contains("- /.clouddrive-papierkorb/**", text);
        Assert.Contains("- ~$*", text);
        Assert.Contains("+ /.clouddrive-sync", text);
        Assert.DoesNotContain("- **", text.Split('\n').Select(l => l.Trim()));
    }

    [Fact]
    public void A_selection_includes_folders_and_files_and_excludes_the_rest()
    {
        var text = SyncFilters.Build(new SyncSelection
        {
            Mode = SelectionMode.Selected,
            Include = ["Unterricht/2026/", "Verwaltung/Listen.xlsx", "Unterricht/2026/Mathe/", "Fotos [alt]/"],
            Exclude = ["**/node_modules/**"],
        });
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains("+ /Unterricht/2026/**", lines);
        Assert.Contains("+ /Verwaltung/Listen.xlsx", lines);
        Assert.DoesNotContain("+ /Unterricht/2026/Mathe/**", lines);
        Assert.Contains(@"+ /Fotos \[alt\]/**", lines);
        Assert.Contains("- **/node_modules/**", lines);
        Assert.Equal("- **", lines.Last(l => l.Length > 0));
        // The sentinel file is included before everything else is excluded.
        Assert.True(lines.IndexOf("+ /.clouddrive-sync") < lines.IndexOf("- **"));
    }
}
