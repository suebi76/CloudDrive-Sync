namespace CloudDriveSync.Core.Tests;

public class AppPathsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"cd-sync-paths-{Guid.NewGuid():N}");

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public void The_empty_folder_of_an_old_version_goes_one_with_content_stays()
    {
        // Versions before 0.2.0 made "backup" in the data folder; nothing uses it any more.
        var legacy = Path.Combine(_home, "backup");
        Directory.CreateDirectory(legacy);
        new AppPaths(_home).EnsureCreated();
        Assert.False(Directory.Exists(legacy));

        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "etwas.txt"), "bleibt");
        new AppPaths(_home).EnsureCreated();
        Assert.True(File.Exists(Path.Combine(legacy, "etwas.txt")));
    }
}
