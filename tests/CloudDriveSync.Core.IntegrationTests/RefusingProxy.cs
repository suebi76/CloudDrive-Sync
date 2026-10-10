using System.Net;
using System.Net.Sockets;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>
/// Sits in front of the test server and passes everything on - except that it can refuse to list one folder with
/// "403 Forbidden", as Nextcloud does with a share to upload only. rclone's own WebDAV server cannot do that: a folder it
/// may not read, it serves as empty. It also counts folder listings and can answer slowly, like a server far away.
/// </summary>
internal sealed class RefusingProxy : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly HttpClient _client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false });
    private readonly Uri _target;
    private readonly string _basePath;
    private readonly Task _loop;
    private volatile string? _refused;

    /// <param name="serverUrl">The test server's address, e.g. "http://127.0.0.1:53000/remote.php/dav/files/lehrer/".</param>
    public RefusingProxy(string serverUrl)
    {
        var server = new Uri(serverUrl);
        _target = new Uri(server.GetLeftPart(UriPartial.Authority));
        _basePath = Uri.UnescapeDataString(server.AbsolutePath).TrimEnd('/');
        var origin = $"http://127.0.0.1:{FreePort()}";
        _listener.Prefixes.Add(origin + "/");
        _listener.Start();
        Url = origin + server.AbsolutePath;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>The address to give CloudDrive-Sync instead of the server's.</summary>
    public string Url { get; }

    private int _listings;

    /// <summary>Folder listings so far (PROPFIND with "Depth: 1").</summary>
    public int Listings => Volatile.Read(ref _listings);

    /// <summary>How long every answer takes in addition, like a server far away; none by default.</summary>
    public TimeSpan Delay { get; set; }

    public void ResetCount() => Interlocked.Exchange(ref _listings, 0);

    /// <summary>
    /// Told about every change that went through (upload, deletion, move, copy, new folder), with the path relative to the
    /// served root - to make the test server behave like Nextcloud (see <see cref="SyncWorld"/>).
    /// </summary>
    public Action<string>? Changed { get; set; }

    /// <summary>Refuses to list this folder from now on (relative to the served root, "/" separated); null refuses nothing.</summary>
    public void RefuseListing(string? folder) => _refused = folder is null ? null : $"{_basePath}/{folder.Trim('/')}";

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var path = Uri.UnescapeDataString(request.Url!.AbsolutePath).TrimEnd('/');
            if (request.HttpMethod == "PROPFIND" && request.Headers["Depth"] == "1") Interlocked.Increment(ref _listings);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
            if (request.HttpMethod == "PROPFIND" && _refused is { } refused && string.Equals(path, refused, StringComparison.Ordinal))
            {
                response.StatusCode = 403;
                response.ContentType = "text/html";
                var page = "<!DOCTYPE html>\n<html><head><title>403 Forbidden</title></head>\n<body><h1>Forbidden</h1></body></html>\n"u8.ToArray();
                await response.OutputStream.WriteAsync(page);
                return;
            }
            using var forward = new HttpRequestMessage(new HttpMethod(request.HttpMethod), new Uri(_target, request.RawUrl));
            if (request.HasEntityBody)
            {
                forward.Content = new StreamContent(request.InputStream);
                if (request.ContentLength64 >= 0) forward.Content.Headers.ContentLength = request.ContentLength64;
            }
            foreach (var name in request.Headers.AllKeys.OfType<string>())
            {
                if (name is "Host" or "Connection" or "Content-Length" or "Transfer-Encoding" or "Expect" or "Keep-Alive") continue;
                var value = request.Headers[name]!;
                // MOVE and COPY name their target with the address the client used.
                if (name == "Destination") value = value.Replace(request.Url.GetLeftPart(UriPartial.Authority), _target.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);
                if (!forward.Headers.TryAddWithoutValidation(name, value)) forward.Content?.Headers.TryAddWithoutValidation(name, value);
            }
            using var answer = await _client.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead);
            response.StatusCode = (int)answer.StatusCode;
            foreach (var header in answer.Headers.Concat(answer.Content.Headers))
            {
                if (header.Key is "Transfer-Encoding" or "Connection" or "Keep-Alive" or "Content-Length") continue;
                if (header.Key == "Content-Type") response.ContentType = string.Join(", ", header.Value);
                else response.AddHeader(header.Key, string.Join(", ", header.Value));
            }
            if (answer.Content.Headers.ContentLength is { } length) response.ContentLength64 = length;
            if (request.HttpMethod != "HEAD") await answer.Content.CopyToAsync(response.OutputStream);
            if (answer.IsSuccessStatusCode && request.HttpMethod is "PUT" or "DELETE" or "MOVE" or "COPY" or "MKCOL" && Changed is { } changed)
            {
                changed(Relative(path));
                if (request.Headers["Destination"] is { } destination && Uri.TryCreate(destination, UriKind.Absolute, out var target))
                    changed(Relative(Uri.UnescapeDataString(target.AbsolutePath).TrimEnd('/')));
            }
        }
        catch (Exception e) when (e is HttpRequestException or HttpListenerException or IOException)
        {
            try
            {
                response.StatusCode = 502;
            }
            catch (InvalidOperationException)
            {
                // The answer had begun already.
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    private string Relative(string path) => path.StartsWith(_basePath, StringComparison.Ordinal) ? path[_basePath.Length..].TrimStart('/') : path.TrimStart('/');

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
        _listener.Stop();
        _listener.Close();
        await _loop;
        _client.Dispose();
    }
}
