using System.Text.Json.Nodes;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>Accounts against a real WebDAV server: sign-in check, folders, signing in again, safe storage.</summary>
public class AccountTests
{
    [Fact]
    public async Task A_wrong_password_is_refused_and_nothing_stays_behind()
    {
        await using var world = await SyncWorld.CreateAsync();
        var error = await Assert.ThrowsAsync<CdException>(() => world.Host.Accounts.AddWebDavAsync("IServ", world.Credential(password: "falsch")));
        Assert.Equal("CD-3012", error.Code);
        Assert.Empty(world.Host.Settings.Current.Accounts);
        var remotes = await world.Host.Engine.Rc.CallAsync("config/listremotes");
        Assert.Empty(remotes["remotes"] as JsonArray ?? []);
    }

    [Fact]
    public async Task An_account_shows_its_folders_and_is_added_only_once()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("Plan.txt", "Stundenplan");
        Directory.CreateDirectory(world.Cloud("Mathe"));

        var account = await world.AddAccountAsync();
        Assert.Equal("iserv-test", account.Id);
        Assert.Equal(WebDavKind.IServ, account.Kind);

        var top = await world.Host.Accounts.ListAsync(account.Id, "", includeFiles: false);
        Assert.Equal([SyncWorld.CloudFolder], top.Select(e => e.Name));
        var inside = await world.Host.Accounts.ListAsync(account.Id, SyncWorld.CloudFolder, includeFiles: true);
        Assert.Equal(["Mathe", "Plan.txt"], inside.Select(e => e.Name));
        Assert.True(inside[0].IsDirectory);
        Assert.Equal(11, inside[1].Size);
        Assert.Equal((11L, 1L), await world.Host.Accounts.GetSizeAsync(account.Id, SyncWorld.CloudFolder));
        await world.Host.Accounts.GetQuotaAsync(account.Id);

        var twice = await Assert.ThrowsAsync<CdException>(() => world.Host.Accounts.AddWebDavAsync("Nochmal", world.Credential()));
        Assert.Equal("CD-3010", twice.Code);
        Assert.Single(world.Host.Settings.Current.Accounts);
    }

    [Fact]
    public async Task Signing_in_again_with_a_wrong_password_keeps_the_working_sign_in()
    {
        await using var world = await SyncWorld.CreateAsync();
        var account = await world.AddAccountAsync();

        var refused = await Assert.ThrowsAsync<CdException>(() => world.Host.Accounts.ReloginAsync(account.Id, world.Credential(password: "falsch")));
        Assert.Equal("CD-3012", refused.Code);
        Assert.Single(await world.Host.Accounts.ListAsync(account.Id, "", includeFiles: false));

        await world.Host.Accounts.ReloginAsync(account.Id, world.Credential());
        Assert.Single(await world.Host.Accounts.ListAsync(account.Id, "", includeFiles: false));
        var remotes = await world.Host.Engine.Rc.CallAsync("config/listremotes");
        Assert.Equal(["cd-iserv-test"], (remotes["remotes"] as JsonArray ?? []).Select(n => n!.GetValue<string>()));

        var otherUser = new Accounts.WebDavCredential { Url = world.Server.Url, Kind = WebDavKind.IServ, User = "jemand", Password = SyncWorld.Password };
        Assert.Equal("CD-3009", (await Assert.ThrowsAsync<CdException>(() => world.Host.Accounts.ReloginAsync(account.Id, otherUser))).Code);
    }

    [Fact]
    public async Task The_password_is_stored_only_encrypted_and_never_logged()
    {
        await using var world = await SyncWorld.CreateAsync();
        await world.AddAccountAsync();
        await world.Host.Accounts.ReloginAsync(world.Account.Id, world.Credential());

        var config = File.ReadAllText(world.Host.Paths.RcloneConfig);
        Assert.Matches("(?m)^RCLONE_ENCRYPT_V0:", config);
        Assert.True(Engine.RcloneEngine.IsEncrypted(world.Host.Paths.RcloneConfig));
        Assert.DoesNotContain(SyncWorld.Password, config);
        Assert.DoesNotContain(SyncWorld.Password, File.ReadAllText(world.Host.Paths.SettingsFile));
        Assert.NotEmpty(Directory.EnumerateFiles(world.Host.Paths.LogDir));
        foreach (var log in Directory.EnumerateFiles(world.Host.Paths.LogDir))
            Assert.DoesNotContain(SyncWorld.Password, SyncWorld.ReadShared(log));
        Assert.NotNull(world.Host.Secrets.Get("config"));
    }
}
