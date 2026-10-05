using System.IO;
using CloudDriveSync.App.Infrastructure;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

/// <summary>A building block of another project that CloudDrive-Sync uses, with its licence.</summary>
public sealed record Component(string Name, string Purpose, string Holder, string License, string Url);

/// <summary>The page "Über": version, author, licence and the building blocks CloudDrive-Sync stands on.</summary>
public sealed partial class AboutViewModel
{
    public string VersionText { get; } = $"Version {AppInfo.Version}";
    public string Copyright { get; } = AppInfo.Copyright;
    public string ProjectUrl => AppInfo.ProjectUrl;

    public IReadOnlyList<Component> Components { get; } =
    [
        new("rclone", "Anmeldung und Übertragung", "© Nick Craig-Wood", "MIT-Lizenz", "https://rclone.org"),
        new(".NET mit WPF und Windows Forms", "Laufzeit und Oberfläche", "© .NET Foundation und Mitwirkende", "MIT-Lizenz", "https://dotnet.microsoft.com"),
        new("CommunityToolkit.Mvvm", "Aufbau der Oberfläche", "© .NET Foundation und Mitwirkende", "MIT-Lizenz", "https://github.com/CommunityToolkit/dotnet"),
        new("Velopack", "Installation und Updates", "© Velopack Ltd.", "MIT-Lizenz", "https://velopack.io"),
    ];

    [RelayCommand]
    private void OpenProject() => Shell.OpenWebPage(AppInfo.ProjectUrl);

    /// <summary>The licence texts travel with the program, so they open without internet.</summary>
    [RelayCommand]
    private void OpenLicense() => Shell.OpenFile(Path.Combine(AppContext.BaseDirectory, "LICENSE.txt"));

    [RelayCommand]
    private void OpenNotices() => Shell.OpenFile(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"));

    [RelayCommand]
    private void OpenComponent(Component component) => Shell.OpenWebPage(component.Url);
}