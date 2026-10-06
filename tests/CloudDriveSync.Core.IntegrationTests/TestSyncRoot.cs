using System.Collections.Concurrent;
using CloudDriveSync.Core.CloudFiles;
using System.Runtime.Versioning;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>
/// A folder registered with Windows for files on demand, for one test: under the test provider of its own home
/// ("CloudDriveSyncTest-…"), connected to a fetcher that serves data from memory, unregistered and removed at the end.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed class TestSyncRoot : IAsyncDisposable
{
    private SyncRootConnection? _connection;

    private TestSyncRoot(string root, string path, string id)
    {
        Root = root;
        Path = path;
        Id = id;
    }

    public string Root { get; }
    /// <summary>The registered folder (with a space and an umlaut on purpose).</summary>
    public string Path { get; }
    public string Id { get; }
    public string PairId => Id.Split('!')[^1];
    public MemoryFetcher Fetcher { get; } = new();

    public static async Task<TestSyncRoot> CreateAsync(bool connect = true, Action<string>? prepare = null)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clouddrive-sync-it", $"cf-{Guid.NewGuid():N}"[..15]);
        var path = System.IO.Path.Combine(root, "Bei Bedarf Übung");
        Directory.CreateDirectory(path);
        prepare?.Invoke(path);
        var paths = new AppPaths(System.IO.Path.Combine(root, "home"));
        var pairId = "test-" + Guid.NewGuid().ToString("N")[..8];
        var id = SyncRoots.IdFor(paths, pairId);
        SyncRoots.Register(new SyncRootSpec(id, path, "CloudDrive-Sync Integrationstest",
            Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\shell32.dll") + ",4", "0.3-test", pairId));
        var world = new TestSyncRoot(root, path, id);
        if (connect) world.Connect();
        return world;
    }

    public void Connect() => _connection = SyncRootConnection.Connect(Path, Fetcher);

    public void Disconnect()
    {
        _connection?.Dispose();
        _connection = null;
    }

    public string File(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', '\\'));

    public ValueTask DisposeAsync()
    {
        Disconnect();
        if (SyncRoots.IsRegistered(Id)) SyncRoots.Unregister(Id);
        if (Environment.GetEnvironmentVariable("CLOUDDRIVE_SYNC_KEEP_TEST_WORLDS") != "1")
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A test that failed half-way may leave a file in use; the temp folder is cleaned up later.
            }
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Serves the data of online-only files from memory, by the path stored in their identity.</summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed class MemoryFetcher : IFileFetcher
{
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public ConcurrentQueue<string> Requests { get; } = new();
    public int ChunkSize { get; set; } = 64 * 1024;

    public void Fetch(FetchRequest request)
    {
        var path = OnDemand.ItemIdentity.Decode(request.Identity)?.Path ?? "";
        Requests.Enqueue(path);
        _ = Task.Run(() =>
        {
            var offset = request.Offset;
            try
            {
                if (!Files.TryGetValue(path, out var data) || data.Length != request.FileSize)
                {
                    request.Fail(FetchFailure.Unsuccessful, request.Offset);
                    return;
                }
                for (; offset < request.End; offset += ChunkSize)
                    request.Transfer(data.AsSpan((int)offset, (int)Math.Min(ChunkSize, request.End - offset)), offset);
            }
            catch (IOException)
            {
                request.Fail(FetchFailure.Unsuccessful, offset);
            }
        });
    }

    public void Cancel(long transferKey)
    {
    }
}
