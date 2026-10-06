using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cd2-settings-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Saves_and_loads_without_secrets()
    {
        var file = Path.Combine(_folder, "settings.json");
        var store = new SettingsStore(file);
        store.Update(s =>
        {
            s.Accounts.Add(new AccountSettings { Id = "iserv", Kind = WebDavKind.IServ, Label = "IServ" });
            s.Syncs.Add(new SyncPairSettings { Id = "p", AccountId = "iserv", LocalPath = @"E:\X", Conflicts = ConflictPolicy.PcWins });
        });
        var loaded = new SettingsStore(file).Current;
        Assert.Equal(WebDavKind.IServ, loaded.Accounts[0].Kind);
        Assert.Equal(ConflictPolicy.PcWins, loaded.Syncs[0].Conflicts);
        var text = File.ReadAllText(file);
        Assert.Contains("\"kind\": \"IServ\"", text);
        Assert.DoesNotContain("pass", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_damaged_file_falls_back_to_the_backup()
    {
        var file = Path.Combine(_folder, "settings.json");
        var store = new SettingsStore(file);
        store.Update(s => s.Accounts.Add(new AccountSettings { Id = "a", Label = "A" }));
        store.Update(s => s.Accounts.Add(new AccountSettings { Id = "b", Label = "B" }));
        File.WriteAllText(file, "{ kaputt");
        Assert.Single(new SettingsStore(file).Current.Accounts);
    }
}
