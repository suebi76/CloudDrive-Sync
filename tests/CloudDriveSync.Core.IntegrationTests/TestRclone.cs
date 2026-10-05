using System.Runtime.InteropServices;
using CloudDriveSync.Core.Engine;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>
/// rclone for the tests: CLOUDDRIVE_SYNC_RCLONE, a copy that is already on this PC, or the pinned and checked download
/// (once, into a shared folder). Every test home gets it as a hard link, so nothing large is copied.
/// </summary>
internal static partial class TestRclone
{
    private static readonly Lazy<Task<string>> Source = new(FindOrDownloadAsync);

    public static Task<string> ExeAsync() => Source.Value;

    public static async Task ProvideAsync(AppPaths paths)
    {
        var source = await Source.Value;
        var target = new RcloneInstaller(paths).ExePath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!CreateHardLink(target, source, IntPtr.Zero)) File.Copy(source, target, overwrite: true);
    }

    private static async Task<string> FindOrDownloadAsync()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string?[] candidates =
        [
            Environment.GetEnvironmentVariable(RcloneInstaller.SourceVariable),
            Path.Combine(local, "CloudDrives-dev", "rclone", RcloneInstaller.Version, "rclone.exe"),
            Path.Combine(local, "CloudDrive-Sync", "deps", "rclone", RcloneInstaller.Version, "rclone.exe"),
            Path.Combine(local, "CloudDrives", "deps", "rclone", RcloneInstaller.Version, "rclone.exe"),
        ];
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
        var shared = new AppPaths(Path.Combine(Path.GetTempPath(), "clouddrive-sync-tests-rclone"));
        return await new RcloneInstaller(shared).EnsureAsync();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
