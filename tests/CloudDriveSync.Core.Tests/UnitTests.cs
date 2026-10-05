using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

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

public class SyncFilterTests
{
    [Fact]
    public void Everything_keeps_only_the_standard_exclusions()
    {
        var text = SyncFilters.Build(new SyncSelection { Mode = SelectionMode.All });
        Assert.Contains("- /.clouddrive-papierkorb/**", text);
        Assert.Contains("- ~$*", text);
        Assert.Contains("+ /.clouddrive-sync", text);
        Assert.DoesNotContain("- **", text.Split('\n').Select(l => l.Trim()));
    }

    [Fact]
    public void A_selection_includes_folders_and_files_and_excludes_the_rest()
    {
        var text = SyncFilters.Build(new SyncSelection
        {
            Mode = SelectionMode.Selected,
            Include = ["Unterricht/2026/", "Verwaltung/Listen.xlsx", "Unterricht/2026/Mathe/", "Fotos [alt]/"],
            Exclude = ["**/node_modules/**"],
        });
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains("+ /Unterricht/2026/**", lines);
        Assert.Contains("+ /Verwaltung/Listen.xlsx", lines);
        Assert.DoesNotContain("+ /Unterricht/2026/Mathe/**", lines);
        Assert.Contains(@"+ /Fotos \[alt\]/**", lines);
        Assert.Contains("- **/node_modules/**", lines);
        Assert.Equal("- **", lines.Last(l => l.Length > 0));
        // The sentinel file is included before everything else is excluded.
        Assert.True(lines.IndexOf("+ /.clouddrive-sync") < lines.IndexOf("- **"));
    }
}

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

public class CloudFolderTests
{
    [Fact]
    public void The_protection_file_stays_out_where_only_the_PC_has_it()
    {
        Assert.Contains("+ /.clouddrive-sync", SyncFilters.Build(new SyncSelection()));
        var without = SyncFilters.Build(new SyncSelection(), cloudCheckFile: false);
        Assert.Contains("- /.clouddrive-sync", without);
        Assert.DoesNotContain("+ /.clouddrive-sync", without);
    }

    [Fact]
    public void Bisync_checks_the_protection_file_only_where_it_lies_on_both_sides()
    {
        var account = new AccountSettings { Id = "iserv", Kind = WebDavKind.IServ };
        var pair = new SyncPairSettings { Id = "p", AccountId = "iserv", RemotePath = "Groups", LocalPath = @"C:\Gruppen" };
        Assert.True((bool?)BisyncCommand.Build(pair, account, "w", "f", BisyncMode.Normal)["checkAccess"]);
        pair.CloudCheckFile = false;
        Assert.False((bool?)BisyncCommand.Build(pair, account, "w", "f", BisyncMode.Normal)["checkAccess"]);
    }

    [Theory]
    [InlineData(WebDavKind.IServ, "Groups", "Gruppen")]
    [InlineData(WebDavKind.IServ, "Files", "Eigene Dateien")]
    [InlineData(WebDavKind.IServ, "Groups/7a/Files", "Gruppen › 7a › Files")]
    [InlineData(WebDavKind.IServ, "", "Alles")]
    [InlineData(WebDavKind.Nextcloud, "Groups", "Groups")]
    [InlineData(WebDavKind.Other, "Files/Unterricht", "Files › Unterricht")]
    public void Folders_carry_the_names_people_know(WebDavKind kind, string path, string shown) =>
        Assert.Equal(shown, CloudFolderNames.ShowPath(kind, path));

    [Fact]
    public void Knows_how_many_entries_the_cloud_side_had_at_the_last_good_run()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"cd-sync-cloud-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, CloudFolderCheck.KnownEntries(folder));
            Directory.CreateDirectory(Path.Combine(folder, "last-good"));
            File.WriteAllLines(Path.Combine(folder, "last-good", "a..b.path1.lst"),
            [
                "# bisync listing v1 from 2026-10-05 20:00:00.000000000 +0000 UTC",
                "- 4 - - 2026-10-05T20:00:00.000000000+0000 \"Gruppe A/Plan.txt\"",
                "- 5 - - 2026-10-05T20:00:00.000000000+0000 \"Gruppe B/Liste.txt\"",
            ]);
            Assert.Equal(2, CloudFolderCheck.KnownEntries(folder));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}

public class LocalOnlyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"cd-sync-localonly-{Guid.NewGuid():N}");

    public LocalOnlyTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Finds_the_uploads_the_server_refused()
    {
        const string report = """
            2026/10/05 20:36:24 INFO  : - Path2             Queue copy to Path1                         - cd-iserv:Groups/Gruppe A/Meins.txt
            2026/10/05 20:36:24 INFO  : - Path1             Queue copy to Path2                         - C:\pc\Gruppe B\Neu.txt
            2026/10/05 20:36:24 ERROR : Gruppe A/Meins.txt: Failed to copy: unchunked simple update failed: Failed to write file.: IServ\Library\Sudo\Exception\SudoException: 500 Internal Server Error
            2026/10/05 20:36:25 ERROR : Gruppe A/Meins.txt: Failed to copy: unchunked simple update failed: Failed to write file.: IServ\Library\Sudo\Exception\SudoException: 500 Internal Server Error
            2026/10/05 20:36:25 ERROR : Gruppe B/Neu.txt: Failed to copy: 403 Forbidden
            """;
        // Only uploads count: the failed download of Neu.txt is something else.
        Assert.Equal(["Gruppe A/Meins.txt"], RunSafety.RefusedUploads(report, "cd-iserv:Groups"));
        Assert.Empty(RunSafety.RefusedUploads("2026/10/05 ERROR : x.txt: Failed to copy: dial tcp: i/o timeout", "cd-iserv:"));
    }

    [Fact]
    public void Files_that_stay_on_the_PC_are_left_out()
    {
        var pair = new SyncPairSettings { LocalOnly = ["Gruppe A/Meins [1].txt"] };
        Assert.Contains("- /Gruppe A/Meins \\[1\\].txt", SyncFilters.Build(pair));
    }

    [Fact]
    public void Leaving_files_out_needs_no_rebuild_but_a_new_selection_does()
    {
        var filters = Path.Combine(_folder, "filter.txt");
        var pair = new SyncPairSettings();
        // A filter of an earlier version, with bisync's checksum of it.
        File.WriteAllText(filters, SyncFilters.Build(pair.Selection, pair.CloudCheckFile));
        File.WriteAllText(filters + ".md5", "checksum-of-bisync");
        pair.LocalOnly.Add("Gruppe A/Meins.txt");
        SyncFilters.Write(_folder, filters, pair);
        Assert.Contains("- /Gruppe A/Meins.txt", File.ReadAllText(filters));
        Assert.Equal(Md5(filters), File.ReadAllText(filters + ".md5"));
        // A new selection: bisync has to rebuild, its checksum stays as it is.
        File.WriteAllText(filters + ".md5", "checksum-of-bisync");
        pair.Selection = new SyncSelection { Mode = SelectionMode.Selected, Include = ["Gruppe B/"] };
        SyncFilters.Write(_folder, filters, pair);
        Assert.Equal("checksum-of-bisync", File.ReadAllText(filters + ".md5"));
    }

    private static string Md5(string file) => Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(file)));
}

public class RunChangesTests
{
    private const string Cloud = "cd-iserv:Eigene Dateien";
    private const string Local = @"C:\pc\Schule";

    [Fact]
    public void Reads_which_files_a_normal_run_moved_where()
    {
        const string report = """
            2026/10/05 21:02:50 INFO  : - Path1             Queue copy to Path2                         - C:\pc\Schule\Geändert Server.txt
            2026/10/05 21:02:50 INFO  : - Path2             Queue delete                                - C:\pc\Schule\Weg vom Server.txt
            2026/10/05 21:02:50 INFO  : - Path2             Queue copy to Path1                         - cd-iserv:Eigene Dateien/Ordner/Beide.txt
            2026/10/05 21:02:50 INFO  : - Path1             Queue delete                                - cd-iserv:Eigene Dateien/Weg vom PC.txt
            2026/10/05 21:02:50 INFO  : - Path2             Do queued copies to                         - Path1
            2026/10/05 21:02:50 INFO  : Ordner/Beide.txt: Copied (replaced existing)
            2026/10/05 21:02:50 INFO  : Weg vom PC.txt: Deleted
            """;
        Assert.Equal(new[]
        {
            new FileChange(ChangeKind.Downloaded, "Geändert Server.txt"),
            new FileChange(ChangeKind.DeletedOnPc, "Weg vom Server.txt"),
            new FileChange(ChangeKind.Uploaded, "Ordner/Beide.txt"),
            new FileChange(ChangeKind.DeletedInCloud, "Weg vom PC.txt"),
        }, RunChanges.FromReport(report, Cloud, Local));
    }

    [Fact]
    public void Reads_what_a_first_run_copied_in_each_direction()
    {
        const string report = """
            2026/10/05 21:02:49 INFO  : - Path2             Resync is copying files to                  - Path1
            2026/10/05 21:02:49 INFO  : Nur PC.txt: Copied (new)
            2026/10/05 21:02:49 INFO  : - Path1             Resync is copying files to                  - Path2
            2026/10/05 21:02:49 INFO  : Nur Cloud.txt: Copied (new)
            2026/10/05 21:02:49 INFO  : Ordner/Beide.txt: Copied (new)
            2026/10/05 21:02:49 INFO  : .clouddrive-sync: Copied (new)
            2026/10/05 21:02:49 INFO  : Resync updating listings
            2026/10/05 21:02:49 INFO  : Danach.txt: Copied (new)
            """;
        Assert.Equal(new[]
        {
            new FileChange(ChangeKind.Uploaded, "Nur PC.txt"),
            new FileChange(ChangeKind.Downloaded, "Nur Cloud.txt"),
            new FileChange(ChangeKind.Downloaded, "Ordner/Beide.txt"),
        }, RunChanges.FromReport(report, Cloud, Local));
    }

    [Fact]
    public void Knows_the_cloud_side_of_a_whole_account() =>
        Assert.Equal(new[] { new FileChange(ChangeKind.Uploaded, "Files/a.txt") },
            RunChanges.FromReport("INFO  : - Path2  Queue copy to Path1  - cd-iserv:Files/a.txt", "cd-iserv:", Local));
}
public class ErrorCatalogTests
{
    [Theory]
    [InlineData("Bisync critical error: too many deletes", "CD-4502")]
    [InlineData("Safety abort: all files were changed on Path1 \"x\". Run with --force if desired.", "CD-4509")]
    [InlineData("Bisync critical error: check file check failed\nBisync aborted. Error is retryable without --resync due to --resilient mode.", "CD-4503")]
    [InlineData("Bisync critical error: something\nBisync aborted. Must run --resync to recover.", "CD-4504")]
    [InlineData("prior lock file found: C:\\w\\x.lck", "CD-4508")]
    [InlineData("couldn't list files: No public access to this resource.: Sabre\\DAV\\Exception\\NotAuthenticated: 401 Unauthorized", "CD-3012")]
    [InlineData("error listing: : 401 Unauthorized", "CD-3012")]
    [InlineData("Post \"https://x/\": dial tcp: lookup x: no such host", "CD-5001")]
    [InlineData("operations/copyfile: unchunked simple update failed: Failed to write file.: IServ\\Library\\Sudo\\Exception\\SudoException: 500 Internal Server Error", "CD-4511")]
    [InlineData("Put \"https://webdav.example.org/Groups/.clouddrive-sync\": 403 Forbidden", "CD-4511")]
    [InlineData("something else entirely", "CD-9000")]
    public void Recognises_errors(string text, string code) => Assert.Equal(code, ErrorCatalog.Classify(text));

    [Fact]
    public void Every_code_has_texts()
    {
        foreach (var code in ErrorCatalog.Codes)
        {
            var entry = ErrorCatalog.Get(code);
            Assert.False(string.IsNullOrWhiteSpace(entry.Title), code);
            Assert.False(string.IsNullOrWhiteSpace(entry.Fix), code);
        }
    }
}

public class LocalFolderCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cd2-check-" + Guid.NewGuid().ToString("N")[..8]);

    public LocalFolderCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Warns_but_allows_overlaps_and_full_folders()
    {
        var folder = Path.Combine(_root, "Sync");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        var other = new SyncPairSettings { Id = "x", LocalPath = Path.Combine(folder, "Inner") };
        var warnings = LocalFolderCheck.Check(folder, [other]);
        Assert.Contains(warnings, w => w.Kind == "overlap");
        Assert.Contains(warnings, w => w.Kind == "not-empty");
    }

    [Fact]
    public void An_empty_new_folder_is_fine() =>
        Assert.Empty(LocalFolderCheck.Check(Path.Combine(_root, "Neu"), []));

    [Fact]
    public void Only_impossible_paths_are_errors() =>
        Assert.Equal("CD-4501", Assert.Throws<CdException>(() => LocalFolderCheck.Check("relativ\\pfad", [])).Code);

    [Fact]
    public void System_folders_get_a_warning() =>
        Assert.Contains(LocalFolderCheck.Check(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "CloudDrives-Test"), []), w => w.Kind == "system");
}

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

public class EncryptedConfigTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"cd2-conf-{Guid.NewGuid():N}.conf");

    public void Dispose() => File.Delete(_file);

    [Theory]
    [InlineData("# Encrypted rclone configuration File\n\nRCLONE_ENCRYPT_V0:\nabc", true)]
    [InlineData("RCLONE_ENCRYPT_V0:\nabc", true)]
    [InlineData("", false)]
    [InlineData("[cd-iserv]\ntype = webdav\n", false)]
    [InlineData("# RCLONE_ENCRYPT_V0: only in a comment\n[x]\n", false)]
    public void Recognises_rclones_encrypted_configuration(string content, bool encrypted)
    {
        File.WriteAllText(_file, content.Replace("\n", "\r\n"));
        Assert.Equal(encrypted, Engine.RcloneEngine.IsEncrypted(_file));
    }
}
public class DeleteGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cd2-guard-{Guid.NewGuid():N}");

    public DeleteGuardTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "state"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SyncPairSettings Pair(SelectionMode mode = SelectionMode.All, params string[] include) => new()
    {
        Id = "p",
        LocalPath = Path.Combine(_root, "pc"),
        MaxDeletePercent = 50,
        Selection = new SyncSelection { Mode = mode, Include = include.ToList() },
    };

    private void Write(params string[] files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(_root, "pc", file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }
    }

    private void Delete(params string[] files)
    {
        foreach (var file in files) File.Delete(Path.Combine(_root, "pc", file));
    }

    [Fact]
    public void Counts_files_not_folders_and_needs_three_deletions()
    {
        var pair = Pair();
        Write("A/1.txt", "B/2.txt", "C/3.txt", "D/4.txt");
        DeleteGuard.Remember(Path.Combine(_root, "state"), pair);
        Delete("A/1.txt", "B/2.txt");
        Assert.Null(DeleteGuard.TooManyMissing(Path.Combine(_root, "state"), pair));
        Delete("C/3.txt");
        Assert.Equal((3, 4), DeleteGuard.TooManyMissing(Path.Combine(_root, "state"), pair));
    }

    [Fact]
    public void Ignores_temporary_files_own_files_and_files_outside_the_selection()
    {
        var pair = Pair(SelectionMode.Selected, "Mathe/", "Plan.txt");
        Write("Mathe/1.txt", "Plan.txt", "Privat/a.txt", "Privat/b.txt", "Privat/c.txt", "~$Brief.docx", "x.tmp", ".clouddrive-papierkorb/old.txt");
        Assert.Equal(["Mathe/1.txt", "Plan.txt"], DeleteGuard.LocalFiles(pair).Keys.Order());
    }

    [Fact]
    public void Without_a_limit_or_a_remembered_list_nothing_stops()
    {
        var pair = Pair();
        Write("1.txt", "2.txt", "3.txt");
        Assert.Null(DeleteGuard.TooManyMissing(Path.Combine(_root, "state"), pair));
        DeleteGuard.Remember(Path.Combine(_root, "state"), pair);
        Delete("1.txt", "2.txt", "3.txt");
        pair.MaxDeletePercent = 100;
        Assert.Null(DeleteGuard.TooManyMissing(Path.Combine(_root, "state"), pair));
    }

    [Fact]
    public void Finds_new_files_and_files_changed_without_a_new_size()
    {
        var pair = Pair();
        var state = Path.Combine(_root, "state");
        Write("Plan.txt", "Alt.txt");
        Assert.Empty(DeleteGuard.ChangedSinceLastRun(state, pair));
        DeleteGuard.Remember(state, pair);
        Assert.Empty(DeleteGuard.ChangedSinceLastRun(state, pair));
        var plan = Path.Combine(_root, "pc", "Plan.txt");
        File.WriteAllText(plan, "Plan.tx!"); // same size
        File.SetLastWriteTimeUtc(plan, File.GetLastWriteTimeUtc(plan).AddSeconds(5));
        Write("Neu.txt");
        Assert.Equal(["Neu.txt", "Plan.txt"], DeleteGuard.ChangedSinceLastRun(state, pair).Order());
    }

    [Fact]
    public void A_list_of_an_earlier_version_counts_every_file_as_changed()
    {
        var pair = Pair();
        var state = Path.Combine(_root, "state");
        Write("1.txt", "2.txt");
        File.WriteAllLines(Path.Combine(state, "local-files.txt"), ["1.txt", "2.txt"]);
        Assert.Equal(["1.txt", "2.txt"], DeleteGuard.ChangedSinceLastRun(state, pair).Order());
        Assert.Null(DeleteGuard.TooManyMissing(state, pair));
    }
}

public class RunSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cd-sync-safety-{Guid.NewGuid():N}");

    public RunSafetyTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Finds_files_another_program_holds_exclusively()
    {
        File.WriteAllText(Path.Combine(_root, "frei.txt"), "f");
        File.WriteAllText(Path.Combine(_root, "offen.txt"), "o");
        File.WriteAllText(Path.Combine(_root, "gelesen.txt"), "g");
        using var exclusive = new FileStream(Path.Combine(_root, "offen.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        // Word and Excel let others read: such files are not in the way.
        using var shared = new FileStream(Path.Combine(_root, "gelesen.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Assert.Equal(["offen.txt"], RunSafety.LockedFiles(_root, ["frei.txt", "offen.txt", "gelesen.txt", "weg.txt"]));
    }

    [Fact]
    public void Puts_the_listings_of_the_last_good_run_back()
    {
        var work = Path.Combine(_root, "bisync");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "a..b.path1.lst"), "gut 1");
        File.WriteAllText(Path.Combine(work, "a..b.path2.lst"), "gut 2");
        RunSafety.RememberGoodState(work, _root);
        // A run that broke off leaves its listings renamed and half-written ones behind.
        File.Move(Path.Combine(work, "a..b.path1.lst"), Path.Combine(work, "a..b.path1.lst-err"));
        File.Move(Path.Combine(work, "a..b.path2.lst"), Path.Combine(work, "a..b.path2.lst-err"));
        File.WriteAllText(Path.Combine(work, "a..b.path1.lst-new"), "halb");
        Assert.True(RunSafety.RestoreGoodState(work, _root));
        Assert.Equal(["a..b.path1.lst", "a..b.path2.lst"], Directory.GetFiles(work).Select(Path.GetFileName).Order());
        Assert.Equal("gut 2", File.ReadAllText(Path.Combine(work, "a..b.path2.lst")));
    }

    [Fact]
    public void Without_a_good_state_nothing_is_put_back()
    {
        var work = Path.Combine(_root, "bisync");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "a..b.path1.lst-err"), "x");
        Assert.False(RunSafety.RestoreGoodState(work, _root));
        Assert.True(File.Exists(Path.Combine(work, "a..b.path1.lst-err")));
    }

    [Fact]
    public void Tells_passing_trouble_from_real_trouble()
    {
        const string locked = """
            ERROR : Offen.docx: Failed to copy: failed to open source object: open C:\x\Offen.docx: The process cannot access the file because it is being used by another process.
            ERROR : Bisync critical error: bisync aborted
            ERROR : Bisync aborted. Must run --resync to recover.
            """;
        Assert.True(RunSafety.IsCritical(locked));
        Assert.True(RunSafety.IsPassing(locked));
        Assert.True(RunSafety.IsFileInUse(locked));
        const string network = "ERROR : Bisync critical error: dial tcp: lookup webdav.example.org: i/o timeout";
        Assert.True(RunSafety.IsPassing(network));
        Assert.False(RunSafety.IsFileInUse(network));
        const string broken = "ERROR : Bisync critical error: path1 listing is corrupt";
        Assert.True(RunSafety.IsCritical(broken));
        Assert.False(RunSafety.IsPassing(broken));
    }
}
public class SyncTrashTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"cd-sync-trash-{Guid.NewGuid():N}");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Put(string stamp, string relative, string text)
    {
        var file = Path.Combine(_folder, SyncFilters.TrashFolder, stamp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    [Fact]
    public void Lists_files_newest_first_with_where_they_were()
    {
        Put("2026-10-05_10-00-00", @"Mathe\A.txt", "alt");
        Put("2026-10-05_12-30-00", "B.txt", "neu");
        File.WriteAllText(Path.Combine(_folder, SyncFilters.TrashFolder, "not-a-run.txt"), "x");
        var entries = SyncTrash.List("p", _folder);
        Assert.Equal([@"B.txt", @"Mathe\A.txt"], entries.Select(e => e.RelativePath));
        Assert.Equal(new DateTime(2026, 10, 5, 12, 30, 0), entries[0].Removed.LocalDateTime);
        Assert.Equal(3, entries[1].Size);
    }

    [Fact]
    public void Restores_to_the_old_place_without_overwriting_and_leaves_no_empty_folders()
    {
        Put("2026-10-05_10-00-00", @"Mathe\A.txt", "aus dem Papierkorb");
        Directory.CreateDirectory(Path.Combine(_folder, "Mathe"));
        File.WriteAllText(Path.Combine(_folder, "Mathe", "A.txt"), "aktuell");
        var entry = SyncTrash.List("p", _folder).Single();

        var restored = SyncTrash.Restore(_folder, entry);

        Assert.Equal(Path.Combine(_folder, "Mathe", "A (wiederhergestellt).txt"), restored);
        Assert.Equal("aus dem Papierkorb", File.ReadAllText(restored));
        Assert.Equal("aktuell", File.ReadAllText(Path.Combine(_folder, "Mathe", "A.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_folder, SyncFilters.TrashFolder)));
    }

    [Fact]
    public void Cleans_up_only_what_is_older_than_kept_and_empties_completely()
    {
        Put("2026-09-01_08-00-00", "alt.txt", "a");
        Put("2026-10-05_08-00-00", "neu.txt", "n");
        SyncTrash.CleanUp(_folder, TimeSpan.FromDays(30), new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 5))));
        Assert.Equal(["neu.txt"], SyncTrash.List("p", _folder).Select(e => e.RelativePath));
        SyncTrash.Empty(_folder);
        Assert.Empty(SyncTrash.List("p", _folder));
    }
}