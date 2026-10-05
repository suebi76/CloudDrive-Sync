using System.Reflection;
using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

/// <summary>A value with a text, for lists to choose from.</summary>
public sealed record Choice<T>(T Value, string Title, string Description = "")
{
    /// <summary>Also the name screen readers announce for the entry.</summary>
    public override string ToString() => Title;
}

/// <summary>The settings page.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public const string AutostartName = "CloudDrive-Sync";
    private readonly MainViewModel _main;
    private bool _loading;

    public SettingsViewModel(MainViewModel main) => _main = main;

    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool Notifications { get; set; }
    [ObservableProperty] public partial int TrashDays { get; set; }

    public IReadOnlyList<Choice<int>> TrashChoices { get; } =
    [
        new(0, "Aus"), new(7, "7 Tage"), new(14, "14 Tage"), new(30, "30 Tage"), new(60, "60 Tage"), new(90, "90 Tage"), new(365, "1 Jahr"),
    ];

    /// <summary>Only the normal installation starts with Windows - not a copy with another data folder.</summary>
    public bool CanStartWithWindows => _main.Host.Paths.IsDefaultHome;

    public string Version
    {
        get
        {
            var version = typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = version.IndexOf('+');
            return plus > 0 ? version[..plus] : version;
        }
    }

    public string EngineVersion => $"rclone {RcloneInstaller.Version}";

    public string DataFolder => _main.Host.Paths.Home;

    public void Load(Preferences preferences)
    {
        _loading = true;
        StartWithWindows = preferences.StartWithWindows;
        Notifications = preferences.Notifications;
        TrashDays = preferences.TrashDays;
        _loading = false;
    }

    /// <summary>Keeps the Windows start entry in line with the setting (also after the program moved).</summary>
    public void ApplyAutostart() => Autostart.Apply(StartWithWindows && CanStartWithWindows, AutostartName);

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        _main.Host.Settings.Update(s => s.Preferences.StartWithWindows = value);
        ApplyAutostart();
    }

    partial void OnNotificationsChanged(bool value)
    {
        if (!_loading) _main.Host.Settings.Update(s => s.Preferences.Notifications = value);
    }

    partial void OnTrashDaysChanged(int value)
    {
        if (!_loading && value >= 0) _main.Host.Settings.Update(s => s.Preferences.TrashDays = value);
    }

    /// <summary>CloudDrives, the drives program, is installed and can be opened from here.</summary>
    public bool HasDrivesProgram => Companion.DrivesProgram is not null;

    [RelayCommand]
    private void OpenDrives() => Companion.OpenDrivesProgram();

    [RelayCommand]
    private void OpenDataFolder() => Shell.OpenFolder(_main.Host.Paths.Home);

    [RelayCommand]
    private void OpenLogs() => Shell.OpenFolder(_main.Host.Paths.LogDir);
}
