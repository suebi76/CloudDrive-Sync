using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

public class SetupSafetyTests
{
    [Fact]
    public async Task An_overlapping_pair_is_rejected_before_creating_a_folder()
    {
        await using var world = await SyncWorld.CreateAsync();
        await world.AddAccountAsync();
        await world.AddPairAsync();
        var nested = Path.Combine(world.Local, "Nested");
        var draft = new SyncPairSettings { AccountId = world.Account.Id, RemotePath = SyncWorld.CloudFolder, LocalPath = nested };

        var error = await Assert.ThrowsAsync<CdException>(() => world.Host.Sync.AddAsync(draft));

        Assert.Equal("CD-4506", error.Code);
        Assert.False(Directory.Exists(nested));
        Assert.Single(world.Host.Sync.Pairs);
    }

    [Fact]
    public async Task A_cancelled_setup_removes_only_folders_it_created()
    {
        await using var world = await SyncWorld.CreateAsync();
        await world.AddAccountAsync();
        var parent = Path.Combine(world.Root, "new-local-parent");
        var local = Path.Combine(parent, "new-local-folder");
        var draft = new SyncPairSettings { AccountId = world.Account.Id, RemotePath = SyncWorld.CloudFolder, LocalPath = local };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Host.Sync.AddAsync(draft, cancellation.Token));

        Assert.False(Directory.Exists(parent));
        Assert.Empty(world.Host.Sync.Pairs);
    }

    [Fact]
    public async Task A_cancelled_setup_preserves_existing_local_files()
    {
        await using var world = await SyncWorld.CreateAsync();
        await world.AddAccountAsync();
        var local = Path.Combine(world.Root, "existing-local-folder");
        Directory.CreateDirectory(local);
        var keep = Path.Combine(local, "keep.txt");
        await File.WriteAllTextAsync(keep, "important");
        var draft = new SyncPairSettings { AccountId = world.Account.Id, RemotePath = SyncWorld.CloudFolder, LocalPath = local };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Host.Sync.AddAsync(draft, cancellation.Token));

        Assert.Equal("important", await File.ReadAllTextAsync(keep));
        Assert.False(File.Exists(Path.Combine(local, SyncFilters.SentinelFile)));
        Assert.Empty(world.Host.Sync.Pairs);
    }

    [Fact]
    public async Task Orphaned_cloud_marks_block_setup_before_writing_a_sentinel()
    {
        await using var world = await SyncWorld.CreateAsync();
        await world.AddAccountAsync();
        var local = Path.Combine(world.Root, "old-cloud-folder");
        var deep = Path.Combine(local, "subfolder", "deeper");
        Directory.CreateDirectory(deep);
        var file = Path.Combine(deep, "placeholder.txt");
        await File.WriteAllTextAsync(file, "keep");
        File.SetAttributes(file, FileAttributes.Offline);
        var draft = new SyncPairSettings { AccountId = world.Account.Id, RemotePath = SyncWorld.CloudFolder, LocalPath = local };
        try
        {
            var error = await Assert.ThrowsAsync<CdException>(() => world.Host.Sync.AddAsync(draft));
            Assert.Equal("CD-4513", error.Code);
            Assert.False(File.Exists(Path.Combine(local, SyncFilters.SentinelFile)));
            Assert.Empty(world.Host.Sync.Pairs);
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Cancelling_during_removal_does_not_leave_a_half_removed_pair()
    {
        await using var world = await SyncWorld.CreateAsync();
        await world.AddAccountAsync();
        await world.AddPairAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await world.Host.Sync.RemoveAsync(world.Pair.Id, cancellationToken: cancellation.Token);

        Assert.Empty(world.Host.Sync.Pairs);
        Assert.False(File.Exists(Path.Combine(world.Local, SyncFilters.SentinelFile)));
    }

    [Fact]
    public async Task A_damaged_cleanup_list_is_set_aside_and_never_blocks_a_setup()
    {
        await using var world = await SyncWorld.CreateAsync();
        await world.AddAccountAsync();
        Directory.CreateDirectory(world.Local);
        var keep = Path.Combine(world.Local, "keep.txt");
        await File.WriteAllTextAsync(keep, "important");
        await File.WriteAllTextAsync(Path.Combine(world.Host.Paths.Home, "cleanup.json"), "not json");
        var draft = new SyncPairSettings
        {
            AccountId = world.Account.Id,
            RemotePath = SyncWorld.CloudFolder,
            LocalPath = world.Local,
            Mode = SyncMode.OnDemand,
        };

        await world.Host.Sync.AddAsync(draft);

        Assert.Single(world.Host.Sync.Pairs);
        Assert.Equal("important", await File.ReadAllTextAsync(keep));
        // Kept for a look later, never thrown away.
        var aside = Assert.Single(Directory.GetFiles(world.Host.Paths.Home, "cleanup.json.damaged-*"));
        Assert.Equal("not json", await File.ReadAllTextAsync(aside));
    }
}
