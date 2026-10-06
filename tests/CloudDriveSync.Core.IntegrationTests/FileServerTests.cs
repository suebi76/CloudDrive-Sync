using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Engine;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>Reading file data for files on demand through the engine's file server (rclone serve http).</summary>
public class FileServerTests
{
    [Fact]
    public async Task Reads_any_range_of_a_file_and_sees_changes_at_once()
    {
        await using var world = await SyncWorld.CreateAsync();
        await world.AddAccountAsync();
        var data = new byte[3 * 1024 * 1024 + 123];
        new Random(7).NextBytes(data);
        Directory.CreateDirectory(Path.GetDirectoryName(world.Cloud("Ordner mit Ä/Was？.bin"))!);
        // On the server's disk the name is in rclone's Windows encoding; clients see "Was?.bin".
        await File.WriteAllBytesAsync(world.Cloud("Ordner mit Ä/Was？.bin"), data);
        using var server = new FileServer(world.Host.Engine);
        var remote = AccountService.RemoteName(world.Account.Id) + ":";
        var path = $"{SyncWorld.CloudFolder}/Ordner mit Ä/Was?.bin";

        Assert.Equal(data, await ReadAsync(server, remote, path, 0, data.Length, expectedTotal: data.Length));
        Assert.Equal(data[4096..(4096 + 65536)], await ReadAsync(server, remote, path, 4096, 65536, expectedTotal: data.Length));

        // Changed on the server with another size: the next request reports the new size at once.
        await File.WriteAllBytesAsync(world.Cloud("Ordner mit Ä/Was？.bin"), data[..1000]);
        Assert.Equal(data[..1000], await ReadAsync(server, remote, path, 0, 1000, expectedTotal: 1000));

        // After a restart of the engine the server is started again by itself.
        await world.Host.Engine.StopAsync();
        Assert.Equal(data[..10], await ReadAsync(server, remote, path, 0, 10, expectedTotal: 1000));
    }

    private static async Task<byte[]> ReadAsync(FileServer server, string remote, string path, long offset, long length, long expectedTotal)
    {
        await using var read = await server.OpenAsync(remote, path, offset, length, CancellationToken.None);
        Assert.Equal(expectedTotal, read.TotalSize);
        using var buffer = new MemoryStream();
        await read.Content.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
