using System.Net;
using System.Net.Sockets;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>
/// Sits in front of the test server and passes everything on - except that it can refuse to list one folder with
/// "403 Forbidden", as Nextcloud does with a share to upload only. rclone's own WebDAV server cannot do that: a folder it
/// may not read, it serves as empty. It also counts folder listings and can answer slowly, like a server far away. And it
/// plays shares to read only and files someone has open in Nextcloud Office (see <see cref="ReadOnly"/>, <see cref="Lock"/>).
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

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _listed = new();

    /// <summary>The folders listed since the count was reset, relative to the served root.</summary>
    public IReadOnlyList<string> Listed => _listed.ToList();

    public void ResetCount()
    {
        Interlocked.Exchange(ref _listings, 0);
        _listed.Clear();
    }

    /// <summary>
    /// Told about every change that went through (upload, deletion, move, copy, new folder), with the path relative to the
    /// served root - to make the test server behave like Nextcloud (see <see cref="SyncWorld"/>).
    /// </summary>
    public Action<string>? Changed { get; set; }

    /// <summary>
    /// Uploads (PUT) this refuses with "404 Not Found" and an error page over several lines - like a Nextcloud whose
    /// filter blocks some types of files. Gets the path relative to the served root.
    /// </summary>
    public Func<string, bool>? RefuseUploads { get; set; }

    /// <summary>The status refused uploads get; 404 by default, 500 for trouble that is no refusal.</summary>
    public int RefusalStatus { get; set; } = 404;

    private int _refusedUploads;

    /// <summary>Uploads refused so far.</summary>
    public int RefusedUploads => Volatile.Read(ref _refusedUploads);

    /// <summary>Refuses to list this folder from now on (relative to the served root, "/" separated); null refuses nothing.</summary>
    public void RefuseListing(string? folder) => _refused = folder is null ? null : $"{_basePath}/{folder.Trim('/')}";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _readOnly = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _locked = new(StringComparer.Ordinal);
    private int _forbidden;

    /// <summary>
    /// A folder (relative to the served root) shared to read only, as Nextcloud has it: what is in it can be read, but
    /// nothing in it can be uploaded, deleted, created, moved or renamed - "403 Forbidden" with Nextcloud's error document.
    /// The folder itself, the share's place, can be removed (that ends the share for this user) and renamed.
    /// </summary>
    public void ReadOnly(string folder, bool readOnly = true)
    {
        if (readOnly) _readOnly[folder.Trim('/')] = true;
        else _readOnly.TryRemove(folder.Trim('/'), out _);
    }

    /// <summary>A file (relative to the served root) someone has open, e.g. in Nextcloud Office: changing it gets "423 Locked".</summary>
    public void Lock(string file, bool locked = true)
    {
        if (locked) _locked[file.Trim('/')] = true;
        else _locked.TryRemove(file.Trim('/'), out _);
    }

    /// <summary>Changes refused so far with "403 Forbidden" or "423 Locked".</summary>
    public int Forbidden => Volatile.Read(ref _forbidden);

    private bool InReadOnly(string relative, bool itself) =>
        _readOnly.Keys.Any(folder => relative.StartsWith(folder + "/", StringComparison.Ordinal) || (itself && relative == folder));

    /// <summary>How Nextcloud refuses a change: "403 Forbidden" or "423 Locked" with a Sabre error document.</summary>
    private string? Refusal(HttpListenerRequest request, string relative)
    {
        var method = request.HttpMethod;
        if (method is not ("PUT" or "DELETE" or "MKCOL" or "MOVE" or "COPY" or "PROPPATCH")) return null;
        string? target = null;
        if (request.Headers["Destination"] is { } destination && Uri.TryCreate(destination, UriKind.Absolute, out var uri))
            target = Relative(Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/'));
        if (method is "PUT" or "DELETE" or "MOVE" && _locked.ContainsKey(relative)) return "423";
        // What is in the share cannot be changed, nothing can be added to it; the share's place itself can go or be renamed.
        if (method != "COPY" && InReadOnly(relative, itself: method is "PUT" or "MKCOL" or "PROPPATCH")) return "403";
        if (target is not null && InReadOnly(target, itself: true)) return "403";
        return null;
    }

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
            if (request.HttpMethod == "PROPFIND" && request.Headers["Depth"] == "1")
            {
                Interlocked.Increment(ref _listings);
                _listed.Enqueue(path.StartsWith(_basePath + "/", StringComparison.Ordinal) ? path[(_basePath.Length + 1)..] : path);
            }
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
            if (request.HttpMethod == "PROPFIND" && _refused is { } refused && string.Equals(path, refused, StringComparison.Ordinal))
            {
                response.StatusCode = 403;
                response.ContentType = "text/html";
                var page = "<!DOCTYPE html>\n<html><head><title>403 Forbidden</title></head>\n<body><h1>Forbidden</h1></body></html>\n"u8.ToArray();
                await response.OutputStream.WriteAsync(page);
                return;
            }
            if (Refusal(request, Relative(path)) is { } status)
            {
                Interlocked.Increment(ref _forbidden);
                var (code, exception) = status == "423"
                    ? (423, @"OCA\DAV\Connector\Sabre\Exception\FileLocked")
                    : (403, @"Sabre\DAV\Exception\Forbidden");
                response.StatusCode = code;
                response.ContentType = "application/xml; charset=utf-8";
                var message = code == 423 ? $"\"{Path.GetFileName(path)}\" is locked" : "";
                var document = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<d:error xmlns:d=\"DAV:\" xmlns:s=\"http://sabredav.org/ns\">\n"
                    + $"  <s:exception>{exception}</s:exception>\n  <s:message>{message}</s:message>\n</d:error>\n";
                await response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(document));
                return;
            }
            if (request.HttpMethod == "PATCH" && request.Headers["X-Recalculate-Hash"] is { } hashName)
            {
                // Nextcloud works out a file's checksum on request after an upload (rclone asks for it); the test server
                // cannot - without the answer every upload looked failed.
                using var get = new HttpRequestMessage(HttpMethod.Get, new Uri(_target, request.RawUrl));
                if (request.Headers["Authorization"] is { } authorization) get.Headers.TryAddWithoutValidation("Authorization", authorization);
                using var file = await _client.SendAsync(get);
                response.StatusCode = (int)file.StatusCode;
                if (!file.IsSuccessStatusCode) return;
                var bytes = await file.Content.ReadAsByteArrayAsync();
                var sum = hashName.Equals("md5", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(bytes))
                    : Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(bytes));
                response.AddHeader("OC-Checksum", $"{hashName.ToUpperInvariant()}:{sum}");
                return;
            }
            if (request.HttpMethod == "PUT" && RefuseUploads is { } refuse && refuse(Relative(path)))
            {
                Interlocked.Increment(ref _refusedUploads);
                response.StatusCode = RefusalStatus;
                response.ContentType = "text/html";
                var page = "<!DOCTYPE html>\n<html lang=\"de\">\n<head><title>Schule</title></head>\n<body>\n<h2>Seite nicht gefunden</h2>\n<p>Die Seite konnte nicht gefunden werden.</p>\n</body>\n</html>\n"u8.ToArray();
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
