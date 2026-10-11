using System.Runtime.Versioning;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>
/// Shares another Nextcloud user owns, synchronised with files on demand: what the server does not let be changed - a
/// share to read only, a file someone has open - is never lost, never hidden for good and never tried again in every run.
/// The idea comes from the Nextcloud client: what may not be deleted or changed comes back from the server, and a change
/// of one's own stays beside it.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public class SharesTests
{
    private const string Shared = "Geteilt";

    private static async Task<SyncWorld> WorldAsync()
    {
        var world = await SyncWorld.CreateAsync(WebDavKind.Nextcloud);
        world.WriteCloud($"{Shared}/Arbeitsblatt.txt", "vom Besitzer");
        world.WriteCloud($"{Shared}/Unterordner/Liste.txt", "Liste");
        world.WriteCloud("Eigenes/Notiz.txt", "meins");
        world.Proxy!.ReadOnly($"{SyncWorld.CloudFolder}/{Shared}");
        await world.AddAccountAsync("Nextcloud Test");
        await world.AddPairAsync(p => p.Mode = SyncMode.OnDemand);
        var first = await world.RunAsync(BisyncMode.Resync);
        Assert.True(first.Success, $"{first.ErrorCode}: {first.ErrorDetail}");
        return world;
    }

    /// <summary>A run like the service does it: what stays on the PC is noted in the settings.</summary>
    private static async Task<SyncRunOutcome> RunAsync(SyncWorld world)
    {
        var outcome = await world.RunAsync();
        if (outcome.LocalOnlyAdded is { Count: > 0 } kept) world.Host.Sync.KeepLocalOnly(world.Pair.Id, kept);
        Assert.True(outcome.Success, $"{outcome.ErrorCode}: {outcome.ErrorDetail}");
        return outcome;
    }

    /// <summary>The next run changes nothing and asks the server for nothing it refuses: no loop.</summary>
    private static async Task AssertSettledAsync(SyncWorld world)
    {
        var refused = world.Proxy!.Forbidden;
        var again = await RunAsync(world);
        Assert.Equal(refused, world.Proxy.Forbidden);
        Assert.Empty(again.Changes ?? []);
        Assert.Null(again.LocalOnlyAdded);
    }

    private static List<string> LocalOnly(SyncWorld world) => world.Host.Sync.FindPair(world.Pair.Id)!.LocalOnly;

    private static string Fetch(SyncWorld world, string file)
    {
        Placeholders.Hydrate(world.Pc(file));
        return world.ReadPc(file);
    }

    private static bool InSync(SyncWorld world, string file) => Path.Exists(world.Pc(file)) && Placeholders.Read(world.Pc(file)) is { InSync: true };

    [Fact]
    public async Task A_file_deleted_in_a_share_to_read_only_comes_back()
    {
        await using var world = await WorldAsync();
        File.Delete(world.Pc($"{Shared}/Arbeitsblatt.txt"));

        var outcome = await RunAsync(world);
        Assert.Equal("vom Besitzer", world.ReadCloud($"{Shared}/Arbeitsblatt.txt"));
        Assert.True(InSync(world, $"{Shared}/Arbeitsblatt.txt"));
        Assert.Equal("vom Besitzer", Fetch(world, $"{Shared}/Arbeitsblatt.txt"));
        Assert.Contains(new FileChange(ChangeKind.Restored, $"{Shared}/Arbeitsblatt.txt"), outcome.Changes!);
        Assert.Empty(LocalOnly(world));
        await AssertSettledAsync(world);
    }

    [Fact]
    public async Task The_whole_share_deleted_on_the_PC_comes_back_with_everything_in_it()
    {
        await using var world = await WorldAsync();
        Directory.Delete(world.Pc(Shared), recursive: true);

        await RunAsync(world);
        Assert.Equal("Liste", world.ReadCloud($"{Shared}/Unterordner/Liste.txt"));
        Assert.True(InSync(world, $"{Shared}/Arbeitsblatt.txt"));
        Assert.Equal("Liste", Fetch(world, $"{Shared}/Unterordner/Liste.txt"));
        Assert.Empty(LocalOnly(world));
        await AssertSettledAsync(world);
    }

    [Fact]
    public async Task A_change_in_a_share_to_read_only_stays_beside_the_owners_version_which_keeps_coming()
    {
        await using var world = await WorldAsync();
        Fetch(world, $"{Shared}/Arbeitsblatt.txt");
        world.WritePc($"{Shared}/Arbeitsblatt.txt", "meine Änderung", later: true);

        await RunAsync(world);
        Assert.Equal("vom Besitzer", world.ReadCloud($"{Shared}/Arbeitsblatt.txt"));
        Assert.True(InSync(world, $"{Shared}/Arbeitsblatt.txt"));
        Assert.Equal("vom Besitzer", Fetch(world, $"{Shared}/Arbeitsblatt.txt"));
        Assert.Equal("meine Änderung", world.ReadPc($"{Shared}/Arbeitsblatt.Konflikt-PC1.txt"));
        Assert.Equal([$"{Shared}/Arbeitsblatt.Konflikt-PC1.txt"], LocalOnly(world));
        await AssertSettledAsync(world);

        // The owner's next version arrives as before.
        world.WriteCloud($"{Shared}/Arbeitsblatt.txt", "neue Fassung vom Besitzer", later: true);
        await RunAsync(world);
        Assert.Equal("neue Fassung vom Besitzer", Fetch(world, $"{Shared}/Arbeitsblatt.txt"));
        Assert.Equal("meine Änderung", world.ReadPc($"{Shared}/Arbeitsblatt.Konflikt-PC1.txt"));
    }

    [Fact]
    public async Task A_change_on_both_sides_in_a_share_to_read_only_keeps_both_versions()
    {
        await using var world = await WorldAsync();
        Fetch(world, $"{Shared}/Arbeitsblatt.txt");
        world.WriteCloud($"{Shared}/Arbeitsblatt.txt", "Besitzer hat geändert", later: true);
        world.WritePc($"{Shared}/Arbeitsblatt.txt", "ich habe auch geändert, später", later: true);
        File.SetLastWriteTimeUtc(world.Pc($"{Shared}/Arbeitsblatt.txt"), DateTime.UtcNow.AddHours(1));

        await RunAsync(world);
        Assert.Equal("Besitzer hat geändert", world.ReadCloud($"{Shared}/Arbeitsblatt.txt"));
        Assert.Equal("Besitzer hat geändert", Fetch(world, $"{Shared}/Arbeitsblatt.txt"));
        Assert.Equal("ich habe auch geändert, später", world.ReadPc($"{Shared}/Arbeitsblatt.Konflikt-PC1.txt"));
        Assert.Equal([$"{Shared}/Arbeitsblatt.Konflikt-PC1.txt"], LocalOnly(world));
        await AssertSettledAsync(world);
    }

    [Fact]
    public async Task A_new_folder_in_a_share_to_read_only_stays_on_the_PC_with_everything_in_it()
    {
        await using var world = await WorldAsync();
        world.WritePc($"{Shared}/Neu/eins.txt", "1");
        world.WritePc($"{Shared}/Neu/Tiefer/zwei.txt", "2");

        await RunAsync(world);
        Assert.Equal([$"{Shared}/Neu"], LocalOnly(world));
        Assert.False(Directory.Exists(world.Cloud($"{Shared}/Neu")));
        Assert.Equal("2", world.ReadPc($"{Shared}/Neu/Tiefer/zwei.txt"));
        await AssertSettledAsync(world);

        // Something new in it later is not tried either.
        world.WritePc($"{Shared}/Neu/drei.txt", "3");
        await AssertSettledAsync(world);
        Assert.Equal("3", world.ReadPc($"{Shared}/Neu/drei.txt"));
    }

    [Fact]
    public async Task A_file_renamed_in_a_share_to_read_only_goes_back_to_its_name()
    {
        await using var world = await WorldAsync();
        File.Move(world.Pc($"{Shared}/Arbeitsblatt.txt"), world.Pc($"{Shared}/Umbenannt.txt"));

        var outcome = await RunAsync(world);
        Assert.Equal("vom Besitzer", world.ReadCloud($"{Shared}/Arbeitsblatt.txt"));
        Assert.False(File.Exists(world.Cloud($"{Shared}/Umbenannt.txt")));
        // Its data was not on the PC: nothing to keep - it is simply back under its name.
        Assert.False(File.Exists(world.Pc($"{Shared}/Umbenannt.txt")));
        Assert.True(InSync(world, $"{Shared}/Arbeitsblatt.txt"));
        Assert.Contains(new FileChange(ChangeKind.Restored, $"{Shared}/Arbeitsblatt.txt"), outcome.Changes!);
        Assert.Empty(LocalOnly(world));
        await AssertSettledAsync(world);
    }

    [Fact]
    public async Task A_file_renamed_only_in_upper_and_lower_case_in_a_share_to_read_only_goes_back()
    {
        await using var world = await WorldAsync();
        File.Move(world.Pc($"{Shared}/Arbeitsblatt.txt"), world.Pc($"{Shared}/ARBEITSBLATT.txt"));

        await RunAsync(world);
        Assert.Equal(["Arbeitsblatt.txt"], Directory.GetFiles(world.Pc(Shared)).Select(Path.GetFileName));
        Assert.True(InSync(world, $"{Shared}/Arbeitsblatt.txt"));
        // The way back went through the folder's recycle bin, which never takes part: nothing is left there.
        Assert.Empty(Directory.EnumerateFileSystemEntries(world.Pc(SyncFilters.TrashFolder)));
        await AssertSettledAsync(world);
    }

    [Fact]
    public async Task A_file_moved_out_of_a_share_to_read_only_is_copied_and_the_original_comes_back()
    {
        await using var world = await WorldAsync();
        Fetch(world, $"{Shared}/Arbeitsblatt.txt");
        File.Move(world.Pc($"{Shared}/Arbeitsblatt.txt"), world.Pc("Eigenes/Arbeitsblatt.txt"));

        await RunAsync(world);
        // Its data was on the PC: it stays where it was put, as a file of one's own, and the share keeps the original.
        await RunAsync(world);
        Assert.Equal("vom Besitzer", world.ReadCloud("Eigenes/Arbeitsblatt.txt"));
        Assert.Equal("vom Besitzer", world.ReadCloud($"{Shared}/Arbeitsblatt.txt"));
        Assert.True(InSync(world, $"{Shared}/Arbeitsblatt.txt"));
        Assert.True(InSync(world, "Eigenes/Arbeitsblatt.txt"));
        Assert.Empty(LocalOnly(world));
        await AssertSettledAsync(world);
    }

    [Fact]
    public async Task A_folder_renamed_in_a_share_to_read_only_goes_back_to_its_name()
    {
        await using var world = await WorldAsync();
        Fetch(world, $"{Shared}/Unterordner/Liste.txt");
        Directory.Move(world.Pc($"{Shared}/Unterordner"), world.Pc($"{Shared}/Anders"));

        await RunAsync(world);
        Assert.False(Directory.Exists(world.Pc($"{Shared}/Anders")));
        Assert.Equal("Liste", world.ReadPc($"{Shared}/Unterordner/Liste.txt"));
        Assert.True(InSync(world, $"{Shared}/Unterordner/Liste.txt"));
        Assert.False(Directory.Exists(world.Cloud($"{Shared}/Anders")));
        Assert.Empty(LocalOnly(world));
        await AssertSettledAsync(world);
    }

    [Fact]
    public async Task A_file_someone_has_open_in_the_cloud_goes_up_once_it_is_free()
    {
        await using var world = await WorldAsync();
        Fetch(world, "Eigenes/Notiz.txt");
        world.WritePc("Eigenes/Notiz.txt", "geändert", later: true);
        // E.g. open in Nextcloud Office: "423 Locked" passes - it is never taken as "the server does not take it".
        world.Proxy!.Lock($"{SyncWorld.CloudFolder}/Eigenes/Notiz.txt");

        var locked = await world.RunAsync();
        Assert.False(locked.Success);
        Assert.Equal("CD-4513", locked.ErrorCode);
        Assert.True(locked.Retryable);
        Assert.Null(locked.LocalOnlyAdded);
        Assert.Equal("meins", world.ReadCloud("Eigenes/Notiz.txt"));
        Assert.Equal("geändert", world.ReadPc("Eigenes/Notiz.txt"));

        world.Proxy.Lock($"{SyncWorld.CloudFolder}/Eigenes/Notiz.txt", locked: false);
        await RunAsync(world);
        Assert.Equal("geändert", world.ReadCloud("Eigenes/Notiz.txt"));
        Assert.True(InSync(world, "Eigenes/Notiz.txt"));
    }

    [Fact]
    public async Task A_share_ended_by_its_owner_leaves_the_PC_but_a_change_of_ones_own_stays()
    {
        await using var world = await WorldAsync();
        Fetch(world, $"{Shared}/Unterordner/Liste.txt");
        Fetch(world, $"{Shared}/Arbeitsblatt.txt");
        world.WritePc($"{Shared}/Arbeitsblatt.txt", "meine Fassung", later: true);
        // The owner ends the share: for this user the folder is gone from the server.
        world.Proxy!.ReadOnly($"{SyncWorld.CloudFolder}/{Shared}", readOnly: false);
        Directory.Delete(world.Cloud(Shared), recursive: true);

        await RunAsync(world);
        // What came from the share goes - with its data into CloudDrive-Sync's recycle bin on the PC.
        Assert.False(File.Exists(world.Pc($"{Shared}/Unterordner/Liste.txt")));
        Assert.Contains("Liste", world.PcTrash());
        // A change of one's own is never lost: it goes up into one's own files.
        Assert.Equal("meine Fassung", world.ReadCloud($"{Shared}/Arbeitsblatt.txt"));
        Assert.Equal("meine Fassung", world.ReadPc($"{Shared}/Arbeitsblatt.txt"));
        await AssertSettledAsync(world);
    }

    [Fact]
    public async Task A_folder_deleted_on_the_PC_while_the_cloud_got_something_new_in_it_comes_back()
    {
        await using var world = await WorldAsync();
        Directory.Delete(world.Pc("Eigenes"), recursive: true);
        world.WriteCloud("Eigenes/Neu.txt", "neu aus der Cloud", later: true);

        await world.RunAsync();
        await RunAsync(world);
        Assert.False(File.Exists(world.Cloud("Eigenes/Notiz.txt")));
        Assert.Equal("neu aus der Cloud", Fetch(world, "Eigenes/Neu.txt"));
        await AssertSettledAsync(world);
    }
}
