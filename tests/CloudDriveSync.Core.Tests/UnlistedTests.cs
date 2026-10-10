using System.Runtime.Versioning;
using CloudDriveSync.Core.OnDemand;

namespace CloudDriveSync.Core.Tests;

[SupportedOSPlatform("windows10.0.17763")]
public class UnlistedTests
{
    [Fact]
    public void What_is_still_on_the_PC_but_not_listed_is_left_out_never_taken_as_deleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "cd-sync-unlisted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Ordner"));
        File.WriteAllText(Path.Combine(root, "Da.txt"), "da");
        File.WriteAllText(Path.Combine(root, "Ordner", "Auch da.txt"), "da");
        File.WriteAllText(Path.Combine(root, "Gelistet.txt"), "da");
        try
        {
            var pc = new Listings.LocalSide([new LocalEntry("Gelistet.txt", false, 2, 0, null)], ["Gesperrt"], []);
            var checkedSide = Listings.WithUnlisted(pc, root, ["Da.txt", "Ordner/Auch da.txt", "Weg.txt", "Gelistet.txt", "Gesperrt/innen.txt"]);
            Assert.Equal(["Gesperrt", "Da.txt", "Ordner/Auch da.txt"], checkedSide.Refused);
            Assert.Empty(checkedSide.Broken);
            Assert.Same(pc, Listings.WithUnlisted(pc, root, ["Weg.txt", "Gelistet.txt"]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
