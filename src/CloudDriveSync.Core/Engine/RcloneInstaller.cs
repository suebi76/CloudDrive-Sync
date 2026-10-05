using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Errors;

namespace CloudDriveSync.Core.Engine;

/// <summary>
/// Provides rclone in the pinned version: from the CloudDrive-Sync folder, from CLOUDDRIVE_SYNC_RCLONE (developers and
/// tests) or downloaded from rclone.org - only accepted when the SHA256 checksum of the package matches.
/// </summary>
public sealed class RcloneInstaller
{
    public const string Version = "1.75.1";
    public const string SourceVariable = "CLOUDDRIVE_SYNC_RCLONE";

    // Checksums of the official release packages (the same as in CloudDrives).
    private static readonly Dictionary<Architecture, (string Name, string Sha256)> Packages = new()
    {
        [Architecture.X64] = ("amd64", "200eb602c126d82aa38b51e0f6b9ae837473ff99b51278d3f6f837574c494d6e"),
        [Architecture.Arm64] = ("arm64", "c3c6cd0424dd49076ad179c30c3f9e5cde2c004ec07ea9fe6911f23e32eafe0f"),
        [Architecture.X86] = ("386", "42257481a961b39cdee3b3eea7b9945a1961825e332d07b3aa321808c0afc436"),
    };

    private static readonly string[] Mirrors =
    [
        "https://downloads.rclone.org/v{0}/rclone-v{0}-windows-{1}.zip",
        "https://github.com/rclone/rclone/releases/download/v{0}/rclone-v{0}-windows-{1}.zip",
    ];

    private readonly AppPaths _paths;

    public RcloneInstaller(AppPaths paths) => _paths = paths;

    public string ExePath => Path.Combine(_paths.DepsDir, "rclone", Version, "rclone.exe");

    public async Task<string> EnsureAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (File.Exists(ExePath)) return ExePath;
        Directory.CreateDirectory(Path.GetDirectoryName(ExePath)!);

        var source = Environment.GetEnvironmentVariable(SourceVariable);
        if (!string.IsNullOrWhiteSpace(source) && File.Exists(source))
        {
            File.Copy(source, ExePath, overwrite: true);
            Log.Info("Engine", $"rclone {Version} taken from {SourceVariable}.");
            return ExePath;
        }

        if (!Packages.TryGetValue(RuntimeInformation.OSArchitecture, out var package))
            throw new CdException("CD-1004", $"no rclone package for {RuntimeInformation.OSArchitecture}");

        Exception? last = null;
        foreach (var mirror in Mirrors)
        {
            var url = string.Format(mirror, Version, package.Name);
            try
            {
                progress?.Report($"rclone {Version} wird heruntergeladen …");
                await DownloadAndExtractAsync(url, package.Sha256, cancellationToken);
                Log.Info("Engine", $"rclone {Version} installed from {url}.");
                return ExePath;
            }
            catch (CdException e) when (e.Code == "CD-1006")
            {
                throw;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
            {
                last = e;
                Log.Warn("Engine", $"Download from {url} failed: {e.Message}");
            }
        }
        throw new CdException("CD-1004", last?.Message, last);
    }

    private async Task DownloadAndExtractAsync(string url, string sha256, CancellationToken cancellationToken)
    {
        var zip = Path.Combine(Path.GetTempPath(), $"clouddrive-sync-rclone-{Guid.NewGuid():N}.zip");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"CloudDrive-Sync/{typeof(RcloneInstaller).Assembly.GetName().Version}");
                await using var download = await http.GetStreamAsync(url, cancellationToken);
                await using var file = File.Create(zip);
                await download.CopyToAsync(file, cancellationToken);
            }

            string actual;
            await using (var file = File.OpenRead(zip))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                throw new CdException("CD-1006", $"rclone package: expected {sha256}, got {actual}");

            using var archive = ZipFile.OpenRead(zip);
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("rclone.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new CdException("CD-1004", "rclone.exe missing in the package");
            var temporary = ExePath + ".tmp";
            entry.ExtractToFile(temporary, overwrite: true);
            File.Move(temporary, ExePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(zip); } catch (IOException) { }
        }
    }
}
