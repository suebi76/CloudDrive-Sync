using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Security;

namespace CloudDriveSync.Core.Engine;

/// <summary>
/// The engine: one hidden "rclone rcd" process that does all the work with the clouds. It listens only on 127.0.0.1
/// with a random port and random credentials, and it ends together with CloudDrive-Sync (job object). Its configuration
/// is always encrypted; the key lives in the Windows Credential Manager.
/// </summary>
public sealed class RcloneEngine : IAsyncDisposable
{
    private const string EncryptedHeader = "RCLONE_ENCRYPT_V0:";
    private readonly AppPaths _paths;
    private readonly SecretStore _secrets;
    private readonly RcloneInstaller _installer;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _process;
    private JobObject? _job;
    private RcClient? _rc;

    public RcloneEngine(AppPaths paths, SecretStore secrets, RcloneInstaller installer)
    {
        _paths = paths;
        _secrets = secrets;
        _installer = installer;
    }

    public bool IsRunning => _process is { HasExited: false } && _rc is not null;

    public RcClient Rc => _rc ?? throw new CdException("CD-5003", "the engine is not running");

    /// <summary>Starts the engine (or does nothing when it runs).</summary>
    public async Task StartAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) return;
            await StopCoreAsync();
            var exe = await _installer.EnsureAsync(progress, cancellationToken);
            _paths.EnsureCreated();
            var configKey = GetConfigKey();
            await EnsureEncryptedConfigAsync(exe, configKey, cancellationToken);

            Exception? last = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await StartProcessAsync(exe, configKey, cancellationToken);
                    return;
                }
                catch (CdException e) when (e.Code == "CD-5002" && attempt < 3)
                {
                    last = e;
                    Log.Warn("Engine", $"Start attempt {attempt} failed: {e.Detail}");
                    await StopCoreAsync();
                }
            }
            throw last ?? new CdException("CD-5002");
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Makes sure the engine runs and answers; restarts it when it does not.</summary>
    public async Task<RcClient> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            try
            {
                await Rc.CallAsync("rc/noop", timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
                return Rc;
            }
            catch (CdException e) when (e.Code == "CD-5003")
            {
                Log.Warn("Engine", "The engine does not answer, restarting it.");
            }
        }
        await StartAsync(cancellationToken: cancellationToken);
        return Rc;
    }

    private string GetConfigKey()
    {
        var key = _secrets.Get("config");
        if (!string.IsNullOrEmpty(key)) return key;
        if (File.Exists(_paths.RcloneConfig) && new FileInfo(_paths.RcloneConfig).Length > 0)
            throw new CdException("CD-2003", "rclone.conf exists, but its key is missing");
        Log.Info("Engine", "Generated a new configuration key.");
        return _secrets.GetOrCreateKey("config");
    }

    /// <summary>
    /// An encrypted configuration starts - after comment lines ("# Encrypted rclone configuration File") - with
    /// rclone's header line.
    /// </summary>
    internal static bool IsEncrypted(string file) =>
        File.Exists(file) && File.ReadLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'))
            ?.StartsWith(EncryptedHeader, StringComparison.Ordinal) == true;

    private async Task EnsureEncryptedConfigAsync(string exe, string configKey, CancellationToken cancellationToken)
    {
        var file = _paths.RcloneConfig;
        if (IsEncrypted(file)) return;
        if (!File.Exists(file)) await File.WriteAllTextAsync(file, "", Encoding.ASCII, cancellationToken);
        else Log.Warn("Engine", "Found an unencrypted rclone.conf - encrypting it now.");

        // rclone asks for the new key through a command; the key itself travels only in an environment variable.
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _paths.Home,
        };
        foreach (var argument in new[] { "config", "encryption", "set", "--config", file, "--password-command", "cmd /c echo %CLOUDDRIVE_SYNC_NEW_CONFIG_PASS%" })
            start.ArgumentList.Add(argument);
        start.Environment["CLOUDDRIVE_SYNC_NEW_CONFIG_PASS"] = configKey;
        using var process = Process.Start(start) ?? throw new CdException("CD-2002", "rclone could not be started");
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || !IsEncrypted(file)) throw new CdException("CD-2002", Log.Redact(await errors));
        Log.Info("Engine", "rclone configuration is encrypted.");
    }

    private async Task StartProcessAsync(string exe, string configKey, CancellationToken cancellationToken)
    {
        var port = FreePort();
        var user = RandomToken(12);
        var password = RandomToken(24);
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _paths.Home,
        };
        foreach (var argument in new[]
                 {
                     "rcd", "--rc-addr", $"127.0.0.1:{port}",
                     "--config", _paths.RcloneConfig,
                     "--cache-dir", _paths.CacheDir,
                     "--log-file", _paths.RcloneLog, "--log-level", "INFO",
                     "--log-file-max-size", "10M", "--log-file-max-backups", "5",
                     "--rc-job-expire-duration", "10m",
                 })
            start.ArgumentList.Add(argument);
        // Secrets only through the environment of the child process, never on its command line.
        start.Environment["RCLONE_CONFIG_PASS"] = configKey;
        start.Environment["RCLONE_RC_USER"] = user;
        start.Environment["RCLONE_RC_PASS"] = password;

        var process = Process.Start(start) ?? throw new CdException("CD-5002", "Process.Start returned nothing");
        _job ??= new JobObject();
        try
        {
            _job.Add(process);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Log.Warn("Engine", $"The engine could not be tied to CloudDrive-Sync: {e.Message}");
        }
        _process = process;
        _rc = new RcClient(port, user, password);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) throw new CdException("CD-5002", $"rclone ended with code {process.ExitCode}");
            try
            {
                await _rc.CallAsync("rc/noop", timeout: TimeSpan.FromSeconds(2), cancellationToken: cancellationToken);
                Log.Info("Engine", $"Engine started (process {process.Id}, port {port}).");
                return;
            }
            catch (CdException)
            {
                await Task.Delay(250, cancellationToken);
            }
        }
        throw new CdException("CD-5002", "rclone did not answer within 20 s");
    }

    public async Task StopAsync()
    {
        await _startGate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        var process = _process;
        var rc = _rc;
        _process = null;
        _rc = null;
        if (process is not null && !process.HasExited)
        {
            try
            {
                if (rc is not null) await rc.CallAsync("core/quit", timeout: TimeSpan.FromSeconds(3));
            }
            catch (CdException)
            {
                // It may already be on its way out.
            }
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            Log.Info("Engine", "Engine stopped.");
        }
        process?.Dispose();
        rc?.Dispose();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string RandomToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _job?.Dispose();
        _startGate.Dispose();
    }
}
