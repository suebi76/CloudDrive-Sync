using CloudDriveSync.Core.Accounts;

namespace CloudDriveSync.Core.Tests;

public class AccountIdTests
{
    [Theory]
    [InlineData("IServ Schule", "iserv-schule")]
    [InlineData("Größe & Übersicht", "groesse-uebersicht")]
    [InlineData("!!!", "konto")]
    public void Ids_are_readable(string label, string id) => Assert.Equal(id, AccountService.UniqueId(label, new HashSet<string>()));

    [Fact]
    public void Ids_are_unique() =>
        Assert.Equal("iserv-3", AccountService.UniqueId("IServ", new HashSet<string>(["iserv", "iserv-2"])));
}
