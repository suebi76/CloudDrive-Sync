using System.Text;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.OnDemand;
using CloudDriveSync.Core.Sync;
using System.Runtime.Versioning;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>The Windows side of files on demand: registering, placeholders, fetching, pinning, switching, unregistering.</summary>
[SupportedOSPlatform("windows10.0.17763")]
public class CloudFilesTests
{
    private static readonly DateTime Monday = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Uninstall_keeps_a_registration_when_its_folder_cannot_be_determined()
    {
        await using var root = await TestSyncRoot.CreateAsync();
        var paths = new AppPaths(Path.Combine(root.Root, "home"));

        Assert.Empty(SyncService.EndAllForUninstall(paths, TimeSpan.FromSeconds(1), _ => null));
        Assert.True(SyncRoots.IsRegistered(root.Id));
        Assert.Empty(SyncService.EndAllForUninstall(paths, TimeSpan.FromSeconds(1), _ => throw new UnauthorizedAccessException()));
        Assert.True(SyncRoots.IsRegistered(root.Id));
    }

    /// <summary>
    /// Windows writes a placeholder broken for good ("Die Clouddatei-Metadaten sind beschädigt") when, in one call, its
    /// identity is shorter than one made before it - found with a real cloud folder, where 17 of 23 sub-folders and
    /// files in them came out like that. Should this test ever fail, the broken entries cannot be deleted while Windows
    /// runs normally (only in safe mode); the test folder stays in %TEMP%\clouddrive-sync-it.
    /// </summary>
    [Fact]
    public async Task Placeholders_made_in_one_go_open_whatever_the_length_of_their_identities()
    {
        await using var root = await TestSyncRoot.CreateAsync();
        // Long, short, longer, shorter - as a folder listing comes.
        var lengths = new[] { 40, 160, 90, 150, 30, 130, 60, 140, 20, 120 };
        var items = new List<NewPlaceholder>();
        for (var i = 0; i < lengths.Length; i++)
        {
            var path = $"Ordner {i} " + new string('x', lengths[i]);
            items.Add(new NewPlaceholder($"Ordner {i}", true, 0, Monday, ItemIdentity.Encode(i + 1, path)));
            items.Add(new NewPlaceholder($"Datei {i}.txt", false, 10, Monday, ItemIdentity.Encode(100 + i, path + "/Datei.txt")));
        }

        Assert.All(Placeholders.Create(root.Path, items), Assert.Null);

        for (var i = 0; i < lengths.Length; i++)
        {
            Assert.Empty(Directory.GetFileSystemEntries(root.File($"Ordner {i}")));
            Assert.NotNull(Placeholders.Read(root.File($"Ordner {i}")));
            Assert.NotNull(Placeholders.Read(root.File($"Datei {i}.txt")));
        }
    }

    [Fact]
    public async Task Placeholders_take_no_space_until_their_data_is_fetched()
    {
        await using var root = await TestSyncRoot.CreateAsync();
        var data = Encoding.UTF8.GetBytes(new string('ä', 50_000));
        root.Fetcher.Files["Ordner/Bericht.txt"] = data;
        Assert.All(Placeholders.Create(root.Path, [new NewPlaceholder("Ordner", true, 0, Monday, ItemIdentity.Encode(1, "Ordner"))]), Assert.Null);
        Assert.All(Placeholders.Create(root.File("Ordner"), [new NewPlaceholder("Bericht.txt", false, data.Length, Monday, ItemIdentity.Encode(2, "Ordner/Bericht.txt"))]), Assert.Null);

        var file = root.File("Ordner/Bericht.txt");
        var before = Placeholders.Read(file)!;
        Assert.Equal(data.Length, before.Size);
        Assert.Equal(0, before.OnDiskSize);
        Assert.True(before.InSync);
        Assert.Equal((2L, "Ordner/Bericht.txt"), ItemIdentity.Decode(before.Identity));
        Assert.Equal(Monday, File.GetLastWriteTimeUtc(file));

        Placeholders.Hydrate(file);
        Assert.True(Placeholders.Read(file)!.IsFullyOnDisk);
        Assert.Equal(data, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task CloudDrive_Sync_itself_never_fetches_a_file_by_reading_it()
    {
        await using var root = await TestSyncRoot.CreateAsync();
        root.Fetcher.Files["Liste.txt"] = "Inhalt"u8.ToArray();
        Placeholders.Create(root.Path, [new NewPlaceholder("Liste.txt", false, 6, Monday, ItemIdentity.Encode(1, "Liste.txt"))]);

        Assert.ThrowsAny<IOException>(() => File.ReadAllBytes(root.File("Liste.txt")));
        Assert.Empty(root.Fetcher.Requests);
        Assert.Equal(0, Placeholders.Read(root.File("Liste.txt"))!.OnDiskSize);
    }

    [Fact]
    public async Task A_failed_fetch_leaves_the_file_online_only()
    {
        await using var root = await TestSyncRoot.CreateAsync();
        // The cloud has another version than the placeholder stands for.
        root.Fetcher.Files["Plan.txt"] = "neue, längere Fassung"u8.ToArray();
        Placeholders.Create(root.Path, [new NewPlaceholder("Plan.txt", false, 5, Monday, ItemIdentity.Encode(1, "Plan.txt"))]);

        Assert.ThrowsAny<IOException>(() => Placeholders.Hydrate(root.File("Plan.txt")));
        Assert.Equal(0, Placeholders.Read(root.File("Plan.txt"))!.OnDiskSize);

        // The new version, announced to the placeholder, arrives.
        var data = root.Fetcher.Files["Plan.txt"];
        Placeholders.UpdateToNewVersion(root.File("Plan.txt"), data.Length, Monday.AddHours(1), ItemIdentity.Encode(1, "Plan.txt"));
        Placeholders.Hydrate(root.File("Plan.txt"));
        Assert.Equal(data, File.ReadAllBytes(root.File("Plan.txt")));
    }

    [Fact]
    public async Task Pinning_freeing_and_marking_in_sync_follow_the_rules()
    {
        await using var root = await TestSyncRoot.CreateAsync();
        root.Fetcher.Files["Notizen.txt"] = "12345"u8.ToArray();
        Placeholders.Create(root.Path, [new NewPlaceholder("Notizen.txt", false, 5, Monday, ItemIdentity.Encode(1, "Notizen.txt"))]);
        var file = root.File("Notizen.txt");

        Placeholders.SetPinState(file, PinState.Pinned, recurse: false);
        Placeholders.Hydrate(file);
        Assert.Equal(PinState.Pinned, Placeholders.Read(file)!.Pin);

        Placeholders.SetPinState(file, PinState.Unpinned, recurse: false);
        Placeholders.Dehydrate(file);
        Assert.Equal(0, Placeholders.Read(file)!.OnDiskSize);

        // A change on the PC: no longer in sync - and it may not be dropped by a new version from the cloud.
        Placeholders.Hydrate(file);
        File.AppendAllText(file, " geändert");
        Assert.False(Placeholders.Read(file)!.InSync);
        var refused = Assert.Throws<CloudFileException>(() => Placeholders.UpdateToNewVersion(file, 99, Monday.AddDays(1), ItemIdentity.Encode(1, "Notizen.txt")));
        Assert.True(refused.NotInSync);
        Assert.Equal("12345 geändert", File.ReadAllText(file));

        // Marked as in sync only when the file is still the version that was uploaded.
        var uploaded = Placeholders.ReadVersion(file);
        File.AppendAllText(file, " noch einmal");
        Assert.False(Placeholders.MarkInSyncIfUnchanged(file, uploaded));
        Assert.False(Placeholders.Read(file)!.InSync);
        uploaded = Placeholders.ReadVersion(file);
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Assert.False(Placeholders.MarkInSyncIfUnchanged(file, uploaded));
        Assert.True(Placeholders.MarkInSyncIfUnchanged(file, uploaded));
        Assert.True(Placeholders.Read(file)!.InSync);
    }

    [Fact]
    public async Task Switching_a_folder_of_normal_files_over_keeps_their_data()
    {
        await using var root = await TestSyncRoot.CreateAsync(prepare: path =>
        {
            Directory.CreateDirectory(Path.Combine(path, "Unterordner"));
            File.WriteAllText(Path.Combine(path, "Unterordner", "Klassisch für Mai.txt"), "klassisch");
        });
        var folder = root.File("Unterordner");
        var file = root.File("Unterordner/Klassisch für Mai.txt");
        Assert.Null(Placeholders.Read(file));

        Placeholders.Convert(folder, ItemIdentity.Encode(1, "Unterordner"), markInSync: true);
        Placeholders.Convert(file, ItemIdentity.Encode(2, "Unterordner/Klassisch für Mai.txt"), markInSync: true);

        var info = Placeholders.Read(file)!;
        Assert.True(info.InSync);
        Assert.True(info.IsFullyOnDisk);
        Assert.Equal("klassisch", File.ReadAllText(file));
        Assert.Empty(root.Fetcher.Requests);
    }

    [Fact]
    public async Task Unregistering_keeps_fetched_files_and_removes_online_only_ones()
    {
        await using var root = await TestSyncRoot.CreateAsync();
        root.Fetcher.Files["geholt.txt"] = "da"u8.ToArray();
        Placeholders.Create(root.Path, [
            new NewPlaceholder("geholt.txt", false, 2, Monday, ItemIdentity.Encode(1, "geholt.txt")),
            new NewPlaceholder("online.txt", false, 2, Monday, ItemIdentity.Encode(2, "online.txt")),
        ]);
        Placeholders.Hydrate(root.File("geholt.txt"));

        root.Disconnect();
        SyncRoots.Unregister(root.Id);

        Assert.False(SyncRoots.IsRegistered(root.Id));
        Assert.Equal("da", File.ReadAllText(root.File("geholt.txt")));
        Assert.Null(Placeholders.Read(root.File("geholt.txt")));
        Assert.False(File.Exists(root.File("online.txt")));
    }

    [Fact]
    public async Task Windows_tells_which_folder_belongs_to_which_synchronisation()
    {
        await using var root = await TestSyncRoot.CreateAsync();
        var inner = Directory.CreateDirectory(root.File("Innen")).FullName;
        Assert.True(SyncRoots.IsRegistered(root.Id));
        Assert.NotNull(SyncRoots.NavigationPaneEntry(root.Id));
        Assert.Equal(root.PairId, SyncRoots.ContextOf(root.Path));
        Assert.Equal(root.PairId, SyncRoots.ContextOf(inner));
        Assert.Null(SyncRoots.ContextOf(root.Root));
        // Windows refuses a sync root inside another one.
        var innerId = root.Id + "-innen";
        try
        {
            Assert.ThrowsAny<Exception>(() => SyncRoots.Register(new SyncRootSpec(innerId, inner, "Innen", "", "0.3-test", "innen")));
        }
        finally
        {
            if (SyncRoots.IsRegistered(innerId)) SyncRoots.Unregister(innerId);
        }
    }
}
