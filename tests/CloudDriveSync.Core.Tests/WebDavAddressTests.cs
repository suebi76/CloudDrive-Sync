using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Tests;

public class WebDavAddressTests
{
    [Theory]
    [InlineData("https://cloud.example.org/remote.php/dav/files/Max%20Mustermann", "https://cloud.example.org", "https://cloud.example.org/remote.php/dav/files/Max%20Mustermann")]
    [InlineData("cloud.example.org", "https://cloud.example.org", "")]
    [InlineData("https://example.com/nextcloud/index.php/apps/files/?dir=/", "https://example.com/nextcloud", "")]
    [InlineData("https://example.com/nextcloud/remote.php/dav/files/Max%20Mustermann/Documents/", "https://example.com/nextcloud", "https://example.com/nextcloud/remote.php/dav/files/Max%20Mustermann")]
    public void Nextcloud_addresses_lead_to_the_server(string input, string server, string url)
    {
        var address = WebDavAddresses.Parse(WebDavKind.Nextcloud, input);
        Assert.Equal(server, address.Server);
        Assert.Equal(url, address.Url);
    }

    [Theory]
    [InlineData("schule.example.org", "https://webdav.schule.example.org/")]
    [InlineData("https://webdav.schule.example.org", "https://webdav.schule.example.org/")]
    [InlineData("https://schule.example.org/webdav", "https://schule.example.org/webdav/")]
    [InlineData("https://schule.example.org/iserv/file/-/Groups", "https://webdav.schule.example.org/")]
    public void IServ_addresses_lead_to_the_WebDAV_server(string input, string url) =>
        Assert.Equal(url, WebDavAddresses.Parse(WebDavKind.IServ, input).Url);

    [Fact]
    public void Other_servers_keep_their_address_and_escape_spaces() =>
        Assert.Equal("https://server.example.com/remote%20dav/", WebDavAddresses.Parse(WebDavKind.Other, "https://server.example.com/remote dav/").Url);

    [Fact]
    public void Unencrypted_addresses_need_an_explicit_yes()
    {
        var error = Assert.Throws<CdException>(() => WebDavAddresses.Parse(WebDavKind.Other, "http://nas.local/webdav"));
        Assert.Equal("CD-3015", error.Code);
        var address = WebDavAddresses.Parse(WebDavKind.Other, "http://nas.local/webdav", allowInsecure: true);
        Assert.True(address.Insecure);
        Assert.False(WebDavAddresses.Parse(WebDavKind.Other, "http://127.0.0.1:8080/").Insecure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://server.example.com/")]
    [InlineData("https://")]
    public void Nonsense_is_refused(string input) =>
        Assert.Equal("CD-3013", Assert.Throws<CdException>(() => WebDavAddresses.Parse(WebDavKind.Other, input)).Code);

    [Fact]
    public void The_identity_is_the_user_at_the_address()
    {
        var credential = new WebDavCredential { Url = "https://webdav.schule.example.org/", Kind = WebDavKind.IServ, User = "max.mustermann", Password = "geheim-123" };
        Assert.Equal("max.mustermann@webdav.schule.example.org", credential.Identity.Id);
        Assert.Equal("max.mustermann @ webdav.schule.example.org", credential.Identity.Name);
        Assert.Equal("other", credential.Vendor);
        Assert.DoesNotContain("geheim", credential.ToString());
    }
}
