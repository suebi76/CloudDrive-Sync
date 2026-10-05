using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>
/// A WebDAV server for the tests: "rclone serve webdav" over a local folder, with user and password. Like IServ it
/// keeps no modification times of its own and has no recycle bin, and like any Linux server it tells upper and lower
/// case apart (with a folder that is marked case-sensitive). Directory listings are never cached, so files the tests
/// change directly in the folder are seen at once.
/// </summary>
internal sealed class WebDavServer : IAsyncDisposable
{
    // Servers end together with the test run, even when a test breaks off.
    private static readonly Engine.JobObject Job = new();
    private readonly Process _process;
    private readonly StringBuilder _output;

    private WebDavServer(Process process, StringBuilder output, string url)
    {
        _process = process;
        _output = output;
        Url = url;
    }

    public string Url { get; }

    /// <param name="basePath">Path the files are served under, e.g. "/remote.php/dav/files/lehrer" like Nextcloud.</param>
    public static async Task<WebDavServer> StartAsync(string rclone, string root, string user, string password, string configFile, string basePath = "")
    {
        var port = FreePort();
        var start = new ProcessStartInfo(rclone)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     "serve", "webdav", root, "--addr", $"127.0.0.1:{port}", "--user", user, "--pass", password,
                     "--config", configFile, "--dir-cache-time", "0s", "--poll-interval", "0s", "--log-level", "NOTICE",
                     "--local-case-sensitive", "--vfs-case-insensitive=false",
                 })
            start.ArgumentList.Add(argument);
        if (basePath.Length > 0)
        {
            start.ArgumentList.Add("--baseurl");
            start.ArgumentList.Add(basePath);
        }
        var output = new StringBuilder();
        var process = Process.Start(start) ?? throw new InvalidOperationException("rclone serve webdav did not start");
        Job.Add(process);
        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var url = $"http://127.0.0.1:{port}{basePath}/";
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (process.HasExited) throw new InvalidOperationException($"rclone serve webdav ended: {output}");
            try
            {
                using var response = await http.GetAsync(url);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.OK) return new WebDavServer(process, output, url);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
            }
            await Task.Delay(250);
        }
        process.Kill(entireProcessTree: true);
        throw new InvalidOperationException($"rclone serve webdav did not answer: {output}");
    }

    public string Output
    {
        get
        {
            lock (_output) return _output.ToString();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
    }
}
