using System.ComponentModel;
using System.Diagnostics;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.CloudFiles;
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

    private SyncWorld(string root, string cloudRoot, bool caseSensitive, string local, WebDavKind kind, WebDavServer server, CloudDriveSyncHost host)
    {
        Root = root;
        CaseSensitiveCloud = caseSensitive;
        CloudRoot = cloudRoot;
        Local = local;
        Kind = kind;
        Server = server;
        Host = host;
        Runner = new SyncRunner(host.Paths, host.Engine);
    }

    public string Root { get; }
    public string CloudRoot { get; }
    /// <summary>The server tells upper and lower case apart, like IServ and Nextcloud.</summary>
    public bool CaseSensitiveCloud { get; }
    public string Local { get; }
    public WebDavKind Kind { get; }
    public WebDavServer Server { get; }

    /// <summary>Between CloudDrive-Sync and the server when asked for (see <see cref="CreateAsync"/>).</summary>
    public RefusingProxy? Proxy { get; private init; }
    public CloudDriveSyncHost Host { get; private set; }
    public SyncRunner Runner { get; private set; }
    public AccountSettings Account { get; private set; } = null!;
    public SyncPairSettings Pair { get; private set; } = null!;

    /// <param name="refusingProxy">CloudDrive-Sync talks to the server through a <see cref="RefusingProxy"/>.</param>
    public static async Task<SyncWorld> CreateAsync(WebDavKind kind = WebDavKind.IServ, bool readOnlyServer = false, bool refusingProxy = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "clouddrive-sync-it", $"{DateTime.Now:HHmmss}-{Guid.NewGuid():N}"[..13]);
        var cloudRoot = Path.Combine(root, "cloud");
        // Spaces and umlauts on purpose.
        var local = Path.Combine(root, "pc", "Schule Übungen");
        Directory.CreateDirectory(cloudRoot);
        // Folders created in it later take the flag over.
        var caseSensitive = MakeCaseSensitive(cloudRoot);
        Directory.CreateDirectory(Path.Combine(cloudRoot, CloudFolder));
        var paths = new AppPaths(Path.Combine(root, "home"));
        paths.EnsureCreated();
        await TestRclone.ProvideAsync(paths);
        var serverConfig = Path.Combine(root, "server.conf");
        await File.WriteAllTextAsync(serverConfig, "");
        // Nextcloud's WebDAV address always ends with /remote.php/dav/files/<user>; rclone relies on it.
        var basePath = kind == WebDavKind.Nextcloud ? $"/remote.php/dav/files/{User}" : "";
        var server = await WebDavServer.StartAsync(await TestRclone.ExeAsync(), cloudRoot, User, Password, serverConfig, basePath, readOnlyServer);
        var host = new CloudDriveSyncHost(paths);
        var world = new SyncWorld(root, cloudRoot, caseSensitive, local, kind, server, host) { Proxy = refusingProxy ? new RefusingProxy(server.Url) : null };
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

    private static bool MakeCaseSensitive(string folder)
    {
        try
        {
            var start = new ProcessStartInfo("fsutil.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "file", "setCaseSensitiveInfo", folder, "enable" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            process.WaitForExit(15000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public WebDavCredential Credential(string? password = null) => new()
    {
        Url = Proxy?.Url ?? Server.Url,
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
    /// <param name="whileStopped">What the user does while CloudDrive-Sync is not running.</param>
    public async Task RestartAsync(Action? whileStopped = null)
    {
        var paths = Host.Paths;
        await Host.DisposeAsync();
        whileStopped?.Invoke();
        Host = new CloudDriveSyncHost(paths);
        Runner = new SyncRunner(Host.Paths, Host.Engine);
        await Host.Engine.StartAsync();
    }

    /// <summary>One run like the service does it, but in the test's own rhythm.</summary>
    public async Task<SyncRunOutcome> RunAsync(BisyncMode mode = BisyncMode.Normal, bool keepTrash = true)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var pair = Host.Sync.FindPair(Pair.Id) ?? Pair;
        if (pair.Mode == SyncMode.OnDemand) return await Host.Sync.RunOnDemandAsync(pair, Account, mode, _ => { }, keepTrash, limit.Token);
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

    /// <summary>Folders of the synchronised cloud folder (without CloudDrive-Sync's own), relative, sorted.</summary>
    public IReadOnlyList<string> CloudFolders() => Folders(Path.Combine(CloudRoot, CloudFolder));

    public IReadOnlyList<string> PcFolders() => Folders(Local);

    /// <summary>Both sides hold exactly the same folders and files, byte for byte.</summary>
    public void AssertInStep()
    {
        Assert.Equal(CloudFolders(), PcFolders());
        var files = PcFiles();
        Assert.Equal(CloudFiles(), files);
        foreach (var file in files)
            Assert.True(File.ReadAllBytes(Cloud(file)).AsSpan().SequenceEqual(File.ReadAllBytes(Pc(file))), $"content differs: {file}");
    }

    /// <summary>One run brings both sides in step; the next one has nothing left to do (no back and forth).</summary>
    public async Task<SyncRunOutcome> SyncAndAssertInStepAsync()
    {
        var outcome = await RunAsync();
        Assert.True(outcome.Success, $"{outcome.ErrorCode}: {outcome.ErrorDetail}");
        AssertInStep();
        var again = await RunAsync();
        Assert.True(again.Success, $"{again.ErrorCode}: {again.ErrorDetail}");
        Assert.Equal(0, again.Final.Transfers);
        Assert.Equal(0, again.Deletes);
        AssertInStep();
        return outcome;
    }

    private static IReadOnlyList<string> Folders(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

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
        // Folders registered with Windows for files on demand go first (only this world's provider, never the real one).
        // Windows may need a moment after the connection ended.
        if (SyncService.OnDemandSupported)
        {
            foreach (var id in SyncRoots.RegisteredIds(Host.Paths.SyncRootProvider))
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        SyncRoots.Unregister(id);
                        break;
                    }
                    catch (Exception) when (attempt < 5)
                    {
                        await Task.Delay(500);
                    }
                }
            }
        }
        if (Proxy is not null) await Proxy.DisposeAsync();
        await Server.DisposeAsync();
        Host.Secrets.Delete("config");
        // For looking into a failure: CLOUDDRIVE_SYNC_KEEP_TEST_WORLDS=1 keeps every world in %TEMP%\clouddrive-sync-it.
        if (Environment.GetEnvironmentVariable("CLOUDDRIVE_SYNC_KEEP_TEST_WORLDS") == "1") return;
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
