using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>
/// One test world: a WebDAV server with its files ("the cloud"), a local folder ("the PC") and CloudDrive-Sync with its
/// own home folder and its own key in the Credential Manager, removed again at the end.
/// </summary>
internal sealed class SyncWorld : IAsyncDisposable
{
    public const string User = "lehrer";
    public const string Password = "test-passwort-7f3k";
    public const string CloudFolder = "Eigene Dateien";

    private SyncWorld(string root, string cloudRoot, string local, WebDavKind kind, WebDavServer server, CloudDriveSyncHost host)
    {
        Root = root;
        CloudRoot = cloudRoot;
        Local = local;
        Kind = kind;
        Server = server;
        Host = host;
        Runner = new SyncRunner(host.Paths, host.Engine);
    }

    public string Root { get; }
    public string CloudRoot { get; }
    public string Local { get; }
    public WebDavKind Kind { get; }
    public WebDavServer Server { get; }
    public CloudDriveSyncHost Host { get; private set; }
    public SyncRunner Runner { get; private set; }
    public AccountSettings Account { get; private set; } = null!;
    public SyncPairSettings Pair { get; private set; } = null!;

    public static async Task<SyncWorld> CreateAsync(WebDavKind kind = WebDavKind.IServ)
    {
        var root = Path.Combine(Path.GetTempPath(), "clouddrive-sync-it", $"{DateTime.Now:HHmmss}-{Guid.NewGuid():N}"[..13]);
        var cloudRoot = Path.Combine(root, "cloud");
        // Spaces and umlauts on purpose.
        var local = Path.Combine(root, "pc", "Schule Übungen");
        Directory.CreateDirectory(Path.Combine(cloudRoot, CloudFolder));
        var paths = new AppPaths(Path.Combine(root, "home"));
        paths.EnsureCreated();
        await TestRclone.ProvideAsync(paths);
        var serverConfig = Path.Combine(root, "server.conf");
        await File.WriteAllTextAsync(serverConfig, "");
        // Nextcloud's WebDAV address always ends with /remote.php/dav/files/<user>; rclone relies on it.
        var basePath = kind == WebDavKind.Nextcloud ? $"/remote.php/dav/files/{User}" : "";
        var server = await WebDavServer.StartAsync(await TestRclone.ExeAsync(), cloudRoot, User, Password, serverConfig, basePath);
        var host = new CloudDriveSyncHost(paths);
        var world = new SyncWorld(root, cloudRoot, local, kind, server, host);
        try
        {
            await host.Engine.StartAsync();
            return world;
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    public WebDavCredential Credential(string? password = null) => new()
    {
        Url = Server.Url,
        Kind = Kind,
        User = User,
        Password = password ?? Password,
    };

    public async Task<AccountSettings> AddAccountAsync(string label = "IServ Test")
    {
        Account = await Host.Accounts.AddWebDavAsync(label, Credential());
        return Account;
    }

    public async Task<SyncPairSettings> AddPairAsync(Action<SyncPairSettings>? configure = null)
    {
        var draft = new SyncPairSettings { AccountId = Account.Id, RemotePath = CloudFolder, LocalPath = Local };
        configure?.Invoke(draft);
        Pair = await Host.Sync.AddAsync(draft);
        return Pair;
    }

    /// <summary>Ends CloudDrive-Sync and starts it again on the same data (like a restart of the PC).</summary>
    public async Task RestartAsync()
    {
        var paths = Host.Paths;
        await Host.DisposeAsync();
        Host = new CloudDriveSyncHost(paths);
        Runner = new SyncRunner(Host.Paths, Host.Engine);
        await Host.Engine.StartAsync();
    }

    /// <summary>One run like the service does it, but in the test's own rhythm.</summary>
    public async Task<SyncRunOutcome> RunAsync(BisyncMode mode = BisyncMode.Normal, bool keepTrash = true)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var pair = Host.Sync.FindPair(Pair.Id) ?? Pair;
        return await Runner.RunAsync(pair, Account, mode, "newer", null, limit.Token, keepTrash);
    }

    public string Cloud(string relative) => Path.Combine(CloudRoot, CloudFolder, relative.Replace('/', '\\'));

    public string Pc(string relative) => Path.Combine(Local, relative.Replace('/', '\\'));

    /// <summary>Writes a file directly on the server. <paramref name="later"/> gives it a clearly newer time.</summary>
    public void WriteCloud(string relative, string text, bool later = false) => Write(Cloud(relative), text, later);

    public void WritePc(string relative, string text, bool later = false) => Write(Pc(relative), text, later);

    public string ReadCloud(string relative) => File.ReadAllText(Cloud(relative));

    public string ReadPc(string relative) => File.ReadAllText(Pc(relative));

    /// <summary>Files of the synchronised cloud folder (without CloudDrive-Sync's own files), relative, sorted.</summary>
    public IReadOnlyList<string> CloudFiles() => Files(Path.Combine(CloudRoot, CloudFolder));

    public IReadOnlyList<string> PcFiles() => Files(Local);


    public IReadOnlyList<string> PcTrash() => Contents(Pc(SyncFilters.TrashFolder));

    private static void Write(string file, string text, bool later)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        if (later) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(2));
    }

    private static IReadOnlyList<string> Files(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> Contents(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(File.ReadAllText).ToList() : [];

    /// <summary>Reads a file another process still has open (the engine's log).</summary>
    public static string ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await Server.DisposeAsync();
        Host.Secrets.Delete("config");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                await Task.Delay(300);
            }
        }
    }
}
