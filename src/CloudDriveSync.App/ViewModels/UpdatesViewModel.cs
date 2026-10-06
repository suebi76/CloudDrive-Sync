using System.Windows.Threading;
using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

/// <summary>
/// New versions of CloudDrive-Sync. Looks on GitHub a few minutes after the start and then every six hours (not at all
/// with "Nur wenn ich nachsehe"). Depending on the setting it shows a notice - installed with one click - or downloads
/// the new version and installs it on its own at a quiet moment: nothing is being synchronised and the window is closed.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private static readonly TimeSpan Period = TimeSpan.FromHours(6);
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _quietWatch;
    private AvailableUpdate? _update;
    private bool _downloaded;
    private string? _dismissed;

    public UpdatesViewModel(MainViewModel main, Dispatcher dispatcher)
    {
        _main = main;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += async (_, _) => await CheckInBackgroundAsync();
        _quietWatch = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => InstallIfQuiet(), dispatcher);
        _quietWatch.Stop();
        StatusText = Updater.IsInstalled
            ? $"Version {AppInfo.Version}"
            : $"Version {AppInfo.Version} – diese Kopie wurde nicht mit dem Installationsprogramm eingerichtet und aktualisiert sich nicht selbst.";
    }

    public bool IsInstalled => Updater.IsInstalled;

    /// <summary>Asked before installing on its own: nothing is being synchronised and the window is closed.</summary>
    public Func<bool> IsQuiet { get; set; } = () => false;

    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial int Progress { get; set; }
    [ObservableProperty] public partial bool CanInstall { get; set; }
    [ObservableProperty] public partial string InstallText { get; set; } = "Jetzt installieren";

    /// <summary>The notice on the overview.</summary>
    [ObservableProperty] public partial bool ShowNotice { get; set; }
    [ObservableProperty] public partial string NoticeText { get; set; } = "";

    /// <summary>A new version to tell the user about once, e.g. in the notification area.</summary>
    public event EventHandler<string>? Announced;

    /// <summary>The program has to end for the update; true: start again in the background.</summary>
    public event EventHandler<bool>? RestartRequested;

    private Preferences Preferences => _main.Host.Settings.Current.Preferences;

    /// <summary>After the start: the first look waits a few minutes, spread out, because the PCs of a school share GitHub's limit for requests.</summary>
    public void Start()
    {
        if (Updater.IsInstalled && Preferences.Updates != UpdateMode.Manual) Schedule(TimeSpan.FromMinutes(Random.Shared.Next(2, 16)));
    }

    /// <summary>The way updates arrive or the test versions changed: forget what was found and look again soon.</summary>
    public void SettingsChanged()
    {
        if (!Updater.IsInstalled || IsBusy) return;
        _update = null;
        _downloaded = false;
        CanInstall = false;
        ShowNotice = false;
        _quietWatch.Stop();
        StatusText = $"Version {AppInfo.Version}";
        if (Preferences.Updates == UpdateMode.Manual) _timer.Stop();
        else Schedule(TimeSpan.FromSeconds(5));
    }

    /// <summary>Called by the program right before it ends for the update.</summary>
    public void InstallAndRestart(bool background)
    {
        if (_update is not null) Updater.InstallAndRestart(_update, background);
    }

    [RelayCommand]
    private Task CheckNowAsync() => CheckAsync(userAsked: true);

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (_update is null || IsBusy) return;
        if (!_downloaded && !await DownloadAsync()) return;
        RestartRequested?.Invoke(this, false);
    }

    [RelayCommand]
    private void Dismiss()
    {
        _dismissed = _update?.Version;
        ShowNotice = false;
    }

    private void Schedule(TimeSpan delay)
    {
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    private async Task CheckInBackgroundAsync()
    {
        _timer.Stop();
        if (Preferences.Updates == UpdateMode.Manual) return;
        await CheckAsync(userAsked: false);
        Schedule(Period + TimeSpan.FromMinutes(Random.Shared.Next(0, 30)));
    }

    private async Task CheckAsync(bool userAsked)
    {
        if (!Updater.IsInstalled || IsBusy) return;
        IsBusy = true;
        StatusText = "Sucht nach einer neuen Version …";
        try
        {
            var update = await Updater.CheckAsync(Preferences.TestVersions);
            if (update is null)
            {
                _update = null;
                CanInstall = false;
                ShowNotice = false;
                StatusText = $"Version {AppInfo.Version} – das ist die neueste.";
                return;
            }
            if (_update?.Version != update.Version)
            {
                Log.Info("Update", $"Version {update.Version} is available.");
                _update = update;
                _downloaded = false;
            }
            StatusText = $"Version {update.Version} ist verfügbar (installiert: {AppInfo.Version}).";
            if (Preferences.Updates == UpdateMode.Automatic && !_downloaded) await DownloadCoreAsync();
            Present(userAsked);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // GitHub not reachable or its limit for requests reached: simply the next time.
            Log.Info("Update", $"Looking for a new version failed: {e.Message}");
            StatusText = "GitHub war gerade nicht erreichbar. CloudDrive-Sync sieht später wieder nach.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> DownloadAsync()
    {
        IsBusy = true;
        try
        {
            return await DownloadCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> DownloadCoreAsync()
    {
        if (_update is null) return false;
        var version = _update.Version;
        IsDownloading = true;
        try
        {
            Progress = 0;
            StatusText = $"Lädt Version {version} …";
            await Updater.DownloadAsync(_update, percent => _main.OnWindowThread(() =>
            {
                Progress = percent;
                StatusText = $"Lädt Version {version} … {percent} %";
            }), CancellationToken.None);
            _downloaded = true;
            StatusText = $"Version {version} ist geladen.";
            Log.Info("Update", $"Version {version} downloaded.");
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn("Update", $"Download of {version} failed: {e.Message}");
            StatusText = $"Version {version} konnte nicht geladen werden. CloudDrive-Sync versucht es später erneut.";
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>Tells the user about the found version, as the setting says.</summary>
    private void Present(bool userAsked)
    {
        if (_update is null) return;
        var version = _update.Version;
        CanInstall = true;
        if (Preferences.Updates == UpdateMode.Automatic && _downloaded)
        {
            InstallText = "Jetzt neu starten";
            NoticeText = $"CloudDrive-Sync {version} ist geladen und wird installiert, sobald nichts abgeglichen wird und das Fenster geschlossen ist.";
            _quietWatch.Start();
        }
        else
        {
            InstallText = "Jetzt installieren";
            NoticeText = $"CloudDrive-Sync {version} ist verfügbar. Die Installation dauert nur einen Moment; danach läuft alles weiter.";
        }
        ShowNotice = userAsked || _dismissed != version;
        if (Preferences.AnnouncedUpdate != version && Preferences.Updates != UpdateMode.Automatic)
        {
            _main.Host.Settings.Update(s => s.Preferences.AnnouncedUpdate = version);
            Announced?.Invoke(this, version);
        }
    }

    private void InstallIfQuiet()
    {
        if (_update is null || !_downloaded || Preferences.Updates != UpdateMode.Automatic) return;
        if (!IsQuiet()) return;
        _quietWatch.Stop();
        Log.Info("Update", $"Installing {_update.Version} at a quiet moment.");
        RestartRequested?.Invoke(this, true);
    }
}
