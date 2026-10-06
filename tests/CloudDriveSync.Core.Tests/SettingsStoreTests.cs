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
    public void Synchronisations_from_before_files_on_demand_stay_classic()
    {
        // As version 0.2 wrote it: no mode, no name in Explorer, no setting for freeing space.
        var file = Path.Combine(_folder, "settings.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(file, """
            { "schemaVersion": 1, "accounts": [], "syncs": [ { "id": "p", "accountId": "a", "localPath": "E:\\X" } ], "preferences": { "trashDays": 30 } }
            """);
        var loaded = new SettingsStore(file).Current;
        Assert.Equal(SyncMode.Classic, loaded.Syncs[0].Mode);
        Assert.Null(loaded.Syncs[0].ExplorerName);
        Assert.Equal(0, loaded.Preferences.FreeUpAfterDays);
    }

    [Fact]
    public void Files_on_demand_are_remembered_by_name()
    {
        var file = Path.Combine(_folder, "settings.json");
        new SettingsStore(file).Update(s => s.Syncs.Add(new SyncPairSettings { Id = "p", Mode = SyncMode.OnDemand, ExplorerName = "IServ – Eigene Dateien" }));
        Assert.Contains("\"mode\": \"OnDemand\"", File.ReadAllText(file));
        var loaded = new SettingsStore(file).Current.Syncs[0];
        Assert.Equal(SyncMode.OnDemand, loaded.Mode);
        Assert.Equal("IServ – Eigene Dateien", loaded.ExplorerName);
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
