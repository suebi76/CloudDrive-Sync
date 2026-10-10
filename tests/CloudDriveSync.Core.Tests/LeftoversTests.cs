using CloudDriveSync.Core.CloudFiles;
using System.Runtime.Versioning;

namespace CloudDriveSync.Core.Tests;

[SupportedOSPlatform("windows10.0.17763")]
public class LeftoversTests
{
    [Fact]
    public void An_unreadable_subfolder_cannot_be_counted_as_clear()
    {
        var root = Path.Combine(Path.GetTempPath(), "cd-sync-leftovers-" + Guid.NewGuid().ToString("N"));
        var blocked = Path.Combine(root, "blocked");
        try
        {
            Directory.CreateDirectory(blocked);

            var failure = Assert.Throws<IOException>(() => Leftovers.Count(root, directory =>
                string.Equals(directory.FullName, blocked, StringComparison.OrdinalIgnoreCase)
                    ? throw new UnauthorizedAccessException("cannot list the folder")
                    : directory.EnumerateFileSystemInfos().ToList()));

            Assert.Contains(blocked, failure.Message);
            Assert.IsType<UnauthorizedAccessException>(failure.InnerException);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_folder_that_is_actually_gone_has_no_leftovers()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cd-sync-leftovers-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, Leftovers.Count(missing));
    }
}
