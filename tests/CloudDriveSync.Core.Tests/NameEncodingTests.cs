using CloudDriveSync.Core.OnDemand;

namespace CloudDriveSync.Core.Tests;

public class NameEncodingTests
{
    [Theory]
    [InlineData("Bericht.docx", "Bericht.docx")]
    [InlineData("Übung 1 – äöüß.txt", "Übung 1 – äöüß.txt")]
    [InlineData("Was?.docx", "Was？.docx")]
    [InlineData("a:b*c|d<e>f\"g.txt", "a：b＊c｜d＜e＞f＂g.txt")]
    [InlineData("back\\slash.txt", "back＼slash.txt")]
    [InlineData("Ende ", "Ende␠")]
    [InlineData("Ende.", "Ende．")]
    [InlineData("Ende. ", "Ende.␠")]
    [InlineData(" ", "␠")]
    // rclone's standard encoding shows control characters as symbols already; Windows keeps them.
    [InlineData("␁Steuerzeichen", "␁Steuerzeichen")]
    [InlineData("a／b", "a／b")]
    public void Names_from_the_cloud_get_the_names_rclone_gives_them_on_Windows(string standard, string local)
    {
        Assert.Equal(local, NameEncoding.ToLocalName(standard));
        Assert.Equal(standard, NameEncoding.ToStandardName(local));
    }

    [Theory]
    // Look-alikes that were in the name already are quoted, so they are never taken for a replacement.
    [InlineData("a＊b.txt", "a‛＊b.txt")]
    [InlineData("Frage？.txt", "Frage‛？.txt")]
    [InlineData("Ende␠", "Ende‛␠")]
    [InlineData("Ende．", "Ende‛．")]
    // rclone's standard encoding doubles the quote character itself; so does the Windows encoding.
    [InlineData("‛‛Zitat", "‛‛Zitat")]
    public void Look_alikes_in_a_name_stay_what_they_were(string standard, string local)
    {
        Assert.Equal(local, NameEncoding.ToLocalName(standard));
        Assert.Equal(standard, NameEncoding.ToStandardName(local));
    }

    [Fact]
    public void Paths_are_encoded_name_by_name()
    {
        Assert.Equal(@"Ordner？\Unter：ordner\Datei.txt", NameEncoding.ToLocalPath("Ordner?/Unter:ordner/Datei.txt"));
        Assert.Equal("Ordner?/Unter:ordner/Datei.txt", NameEncoding.ToStandardPath(@"Ordner？\Unter：ordner\Datei.txt"));
    }
}
