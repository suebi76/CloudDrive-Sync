using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.Tests;

public class BisyncCommandTests
{
    private static readonly SyncPairSettings Pair = new()
    {
        Id = "iserv-eigene",
        AccountId = "iserv",
        RemotePath = "Eigene",
        LocalPath = @"E:\Schule\IServ\Eigene",
        MaxDeletePercent = 50,
    };

    [Fact]
    public void A_normal_run_carries_every_safety_setting()
    {
        var body = BisyncCommand.Build(Pair, new AccountSettings { Id = "iserv", Kind = WebDavKind.IServ }, @"C:\w", @"C:\f.txt", BisyncMode.Normal, now: new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal("cd-iserv:Eigene", (string?)body["path1"]);
        Assert.Equal(@"E:\Schule\IServ\Eigene", (string?)body["path2"]);
        Assert.True((bool?)body["checkAccess"]);
        Assert.Equal(".clouddrive-sync", (string?)body["checkFilename"]);
        Assert.Equal(50, (int?)body["maxDelete"]);
        Assert.True((bool?)body["recover"]);
        Assert.True((bool?)body["resilient"]);
        Assert.Equal("newer", (string?)body["conflictResolve"]);
        Assert.Equal(BisyncCommand.ConflictSuffix, (string?)body["conflictSuffix"]);
        Assert.Equal(@"E:\Schule\IServ\Eigene\.clouddrive-papierkorb\2026-10-05_12-00-00", (string?)body["backupDir2"]);
        Assert.Null(body["backupDir1"]);
        Assert.True((bool?)body["_config"]!["TrackRenames"]);
        Assert.Null(body["resync"]);
        Assert.Null(body["force"]);
    }

    [Theory]
    [InlineData(WebDavKind.IServ)]
    [InlineData(WebDavKind.Nextcloud)]
    [InlineData(WebDavKind.Other)]
    public void No_server_gets_a_recycle_bin_folder_and_the_one_on_the_PC_can_be_switched_off(WebDavKind kind)
    {
        var account = new AccountSettings { Id = "iserv", Kind = kind };
        Assert.Null(BisyncCommand.Build(Pair, account, "w", "f", BisyncMode.Normal)["backupDir1"]);
        Assert.NotNull(BisyncCommand.Build(Pair, account, "w", "f", BisyncMode.Normal)["backupDir2"]);
        Assert.Null(BisyncCommand.Build(Pair, account, "w", "f", BisyncMode.Normal, keepTrash: false)["backupDir2"]);
    }

    [Theory]
    [InlineData(ConflictPolicy.NewerWins, "newer")]
    [InlineData(ConflictPolicy.KeepBoth, "none")]
    [InlineData(ConflictPolicy.CloudWins, "path1")]
    [InlineData(ConflictPolicy.PcWins, "path2")]
    public void Conflict_policies_map_to_bisync(ConflictPolicy policy, string expected) =>
        Assert.Equal(expected, BisyncCommand.Conflicts(policy).Resolve);

    [Fact]
    public void Rebuild_and_force_are_explicit()
    {
        var account = new AccountSettings { Id = "iserv" };
        var resync = BisyncCommand.Build(Pair, account, "w", "f", BisyncMode.Resync);
        Assert.True((bool?)resync["resync"]);
        Assert.Equal("newer", (string?)resync["resyncMode"]);
        Assert.True((bool?)BisyncCommand.Build(Pair, account, "w", "f", BisyncMode.Force)["force"]);
    }
}
