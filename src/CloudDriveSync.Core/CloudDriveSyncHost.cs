using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Security;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core;

/// <summary>Everything CloudDrive-Sync needs, put together once - for the program and for the tests.</summary>
public sealed class CloudDriveSyncHost : IAsyncDisposable
{
    public CloudDriveSyncHost(AppPaths? paths = null)
    {
        Paths = paths ?? AppPaths.FromEnvironment();
        Paths.EnsureCreated();
        Log.Initialize(Paths.LogDir);
        Settings = new SettingsStore(Paths.SettingsFile);
        Secrets = new SecretStore(Paths.SecretPrefix);
        Engine = new RcloneEngine(Paths, Secrets, new RcloneInstaller(Paths));
        Accounts = new AccountService(Settings, Engine);
        Sync = new SyncService(Paths, Settings, Accounts, Engine);
        Nextcloud = new NextcloudSignIn();
    }

    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public SecretStore Secrets { get; }
    public RcloneEngine Engine { get; }
    public AccountService Accounts { get; }
    public SyncService Sync { get; }
    public NextcloudSignIn Nextcloud { get; }

    /// <summary>Starts the engine and the synchronisations.</summary>
    public async Task StartAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        Log.Info("App", $"CloudDrive-Sync {typeof(CloudDriveSyncHost).Assembly.GetName().Version} starting (home {Paths.Home}).");
        await Engine.StartAsync(progress, cancellationToken);
        Sync.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await Sync.DisposeAsync();
        await Engine.DisposeAsync();
        Nextcloud.Dispose();
        Log.Info("App", "CloudDrive-Sync stopped.");
    }
}
