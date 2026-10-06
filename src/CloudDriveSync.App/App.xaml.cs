using System.Windows;
using System.Windows.Threading;
using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.App.ViewModels;
using CloudDriveSync.App.Views;
using CloudDriveSync.Core;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.App;

/// <summary>
/// CloudDrive-Sync starts once per user, lives in the notification area and keeps synchronising when its window is
/// closed. "--background" (start with Windows) starts without opening the window.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _instance;
    private CloudDriveSyncHost? _host;
    private MainViewModel? _main;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;
    private bool _toldAboutBackground;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Windows 11 look; light or dark like Windows.
        ThemeMode = ThemeMode.System;
        DispatcherUnhandledException += OnUnhandledException;

        var paths = AppPaths.FromEnvironment();
        _instance = SingleInstance.TryAcquire(paths.SecretPrefix);
        if (_instance is null)
        {
            SingleInstance.ShowRunning(paths.SecretPrefix);
            Shutdown();
            return;
        }
        _instance.ShowRequested += (_, _) => Dispatcher.InvokeAsync(ShowMainWindow);

        // The installed program records where it is, so CloudDrives finds it wherever it was installed - and comes back
        // by itself after a crash.
        if (Updater.IsInstalled && paths.IsDefaultHome)
        {
            AppRegistration.Register();
            CrashRestart.Register();
        }
        var migrated = TestBuildMigration.Run(paths);
        _host = new CloudDriveSyncHost(paths);
        if (migrated > 0) Log.Info("App", $"Data of the test build \"CloudDrives 2\" taken over ({migrated} item(s)).");
        _main = new MainViewModel(_host, new DialogService(_host, () => _window is { IsVisible: true } ? _window : null), Dispatcher);
        _tray = new TrayIcon(ShowMainWindow, () => _main.SyncAllCommand.Execute(null), paused => _main.PauseAll(paused), () =>
        {
            ShowMainWindow();
            _main.ShowAbout();
        }, () => _ = ExitAsync());
        _main.Notice += (_, notice) => _tray.Notify(notice.Title, notice.Text, notice.Kind != SyncNoticeKind.Conflicts);
        _main.SummaryChanged += (_, _) =>
        {
            _tray.SetStatus(_main.SummaryTitle, _main.SummaryTone);
            _tray.SetAllPaused(_main.AllPaused);
        };

        _window = new MainWindow(_main);
        _main.Updates.IsQuiet = () => _window is not { IsVisible: true } && !_main.Pairs.Any(p => p.IsBusy);
        _main.Updates.Announced += (_, version) => _tray.Notify("Neue Version von CloudDrive-Sync",
            $"Version {version} ist verfügbar. Installieren kannst du sie mit einem Klick in der Übersicht.", warning: false);
        _main.Updates.RestartRequested += async (_, background) => await RestartForUpdateAsync(background);
        _window.HiddenToTray += (_, _) =>
        {
            if (_toldAboutBackground) return;
            _toldAboutBackground = true;
            _tray.Notify("CloudDrive-Sync läuft weiter", "Deine Ordner werden im Hintergrund aktuell gehalten. Über das Symbol im Infobereich öffnest du CloudDrive-Sync wieder.", warning: false);
        };
        if (!e.Args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase))) ShowMainWindow();
        WindowSnapshots.StartIfRequested(this);
        await _main.StartAsync();
    }

    private void ShowMainWindow()
    {
        if (_window is null || _exiting) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Ends CloudDrive-Sync: stops the synchronisations cleanly (an interrupted run continues next time) and the engine.</summary>
    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        await StopAsync();
        Shutdown();
    }

    /// <summary>Ends CloudDrive-Sync cleanly for an update; Velopack installs it and starts the program again.</summary>
    private async Task RestartForUpdateAsync(bool background)
    {
        if (_exiting || _main is null) return;
        _exiting = true;
        Log.Info("App", "Ending for the update.");
        await StopAsync();
        try
        {
            _main.Updates.InstallAndRestart(background);
        }
        catch (Exception e)
        {
            // The update could not start: CloudDrive-Sync starts again as it was, so nothing stops synchronising.
            Log.Error("App", $"The update could not be installed: {e}");
            if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe, "--background");
        }
        Shutdown();
    }

    private async Task StopAsync()
    {
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        _tray?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
        _instance?.Dispose();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        _ = ExitAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("App", $"Unexpected error: {e.Exception}");
        e.Handled = true;
        MessageDialog.Show(_window, "Unerwarteter Fehler",
            $"Etwas ist schiefgelaufen; CloudDrive-Sync läuft weiter. Die Einzelheiten stehen im Protokoll.\n\n{e.Exception.Message}", "OK", null, "", danger: false);
    }
}
