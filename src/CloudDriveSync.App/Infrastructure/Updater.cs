using Velopack;
using Velopack.Sources;

namespace CloudDriveSync.App.Infrastructure;

/// <summary>A new version found on GitHub, with the manager that found it (it also downloads and installs it).</summary>
internal sealed record AvailableUpdate(UpdateManager Manager, UpdateInfo Info)
{
    public string Version => Info.TargetFullRelease.Version.ToString();
}

/// <summary>
/// Updates through Velopack from the releases of the GitHub project; test versions are GitHub pre-releases. Only the
/// installed program can update itself - a copy started from elsewhere (development) cannot.
/// </summary>
internal static class Updater
{
    private static UpdateManager Manager(bool testVersions) => new(new GithubSource(AppInfo.ProjectUrl, null, testVersions));

    /// <summary>Installed with the setup program (and so able to update itself).</summary>
    public static bool IsInstalled { get; } = DetectInstalled();

    /// <summary>A test version is running: it keeps getting test versions until a newer regular version is out.</summary>
    public static bool RunsTestVersion => AppInfo.Version.Contains('-');

    /// <summary>The newer version, or null. Throws when GitHub cannot be reached.</summary>
    public static async Task<AvailableUpdate?> CheckAsync(bool testVersions)
    {
        var manager = Manager(testVersions || RunsTestVersion);
        var info = await manager.CheckForUpdatesAsync();
        return info is null ? null : new AvailableUpdate(manager, info);
    }

    public static Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken cancellationToken) =>
        update.Manager.DownloadUpdatesAsync(update.Info, progress, cancellationToken);

    /// <summary>Ends the program at once, installs the downloaded version and starts it again.</summary>
    public static void InstallAndRestart(AvailableUpdate update, bool background) =>
        update.Manager.ApplyUpdatesAndRestart(update.Info.TargetFullRelease, background ? ["--background"] : []);

    private static bool DetectInstalled()
    {
        try
        {
            return Manager(false).IsInstalled;
        }
        catch (Exception)
        {
            return false;
        }
    }
}