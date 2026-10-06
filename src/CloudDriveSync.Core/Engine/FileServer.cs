using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Errors;

namespace CloudDriveSync.Core.Engine;

/// <summary>Part of a file in the cloud, being read: the data and the size of the whole file the server reports.</summary>
public sealed class RemoteRead(HttpResponseMessage response, Stream content, long? totalSize) : IAsyncDisposable
{
    public Stream Content { get; } = content;
    /// <summary>Size of the whole file in the cloud, when the server tells it.</summary>
    public long? TotalSize { get; } = totalSize;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        response.Dispose();
    }
}

/// <summary>
/// Reads file data from the cloud for files on demand. The engine runs an "rclone serve http" per account (RC call
/// serve/start): only on 127.0.0.1, with random credentials of its own, without a cache and without a directory cache,
/// so every request sees the current state of the server. CloudDrive-Sync reads from it with HTTP range requests and
/// passes the data on to Windows piece by piece. The servers live inside the engine and end with it; after a restart
/// of the engine they are started again on the next request.
/// </summary>
public sealed class FileServer : IDisposable
{
    private sealed record Server(int Generation, Uri Address, HttpClient Http);

    private readonly RcloneEngine _engine;
    private readonly Dictionary<string, Server> _servers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileServer(RcloneEngine engine) => _engine = engine;

    /// <summary>
    /// Opens the bytes [offset, offset + length) of a file. <paramref name="remote"/> is the account's remote ("cd-…:"),
    /// <paramref name="path"/> the file's path below it in rclone's standard encoding.
    /// </summary>
    public async Task<RemoteRead> OpenAsync(string remote, string path, long offset, long length, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var server = await ServerForAsync(remote, cancellationToken);
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.Address, string.Join('/', path.Split('/').Select(Uri.EscapeDataString))));
            if (length > 0) request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
            HttpResponseMessage response;
            try
            {
                response = await server.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException e) when (attempt == 1)
            {
                // The engine may have restarted since: start the server again once.
                Log.Warn("Engine", $"File server for {remote} did not answer ({e.Message}); starting it again.");
                await ForgetAsync(remote);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new CdException(status == 404 ? "CD-4604" : "CD-5001", $"{path}: the file server answered {status}");
            }
            var total = response.Content.Headers.ContentRange?.Length ?? (offset == 0 ? response.Content.Headers.ContentLength : null);
            return new RemoteRead(response, await response.Content.ReadAsStreamAsync(cancellationToken), total);
        }
    }

    /// <summary>
    /// Size, modification time (UTC ticks) and - where the server has one - checksum ("sha1:…") of a file in the cloud;
    /// null when it is not there.
    /// </summary>
    public async Task<(long Size, long Ticks, string? Hash)?> StatAsync(string remote, string path, bool withHash, CancellationToken cancellationToken)
    {
        var rc = await _engine.EnsureRunningAsync(cancellationToken);
        var options = new JsonObject { ["noMimeType"] = true };
        if (withHash) options["showHash"] = true;
        var result = await rc.CallAsync("operations/stat", new JsonObject { ["fs"] = remote, ["remote"] = path, ["opt"] = options },
            TimeSpan.FromSeconds(30), cancellationToken);
        if (result["item"] is not JsonObject item) return null;
        var size = item["Size"] is JsonValue s && s.TryGetValue<long>(out var bytes) ? bytes : 0;
        var ticks = DateTimeOffset.TryParse(item["ModTime"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var modified) ? modified.UtcTicks : 0;
        return (size, ticks, HashOf(item));
    }

    /// <summary>The strongest checksum rclone reported for an entry ("sha1:…" before "md5:…"), or null.</summary>
    internal static string? HashOf(JsonObject item)
    {
        if (item["Hashes"] is not JsonObject hashes) return null;
        foreach (var type in new[] { "sha1", "md5" })
            if (hashes[type]?.GetValue<string>() is { Length: > 0 } value) return $"{type}:{value}";
        return null;
    }

    private async Task<Server> ServerForAsync(string remote, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var rc = await _engine.EnsureRunningAsync(cancellationToken);
            if (_servers.TryGetValue(remote, out var server) && server.Generation == _engine.Generation) return server;
            server?.Http.Dispose();
            var user = Token(12);
            var password = Token(24);
            var started = await rc.CallAsync("serve/start", new JsonObject
            {
                ["type"] = "http",
                ["fs"] = remote,
                ["addr"] = "127.0.0.1:0",
                ["user"] = user,
                ["pass"] = password,
                // Every request looks at the server again: an old size must never decide what is delivered.
                ["dir_cache_time"] = "0s",
            }, cancellationToken: cancellationToken);
            var address = started["addr"]?.GetValue<string>() ?? throw new CdException("CD-5002", "serve/start returned no address");
            var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
            server = new Server(_engine.Generation, new Uri($"http://{address}/"), http);
            _servers[remote] = server;
            Log.Info("Engine", $"File server for {remote} started on {address}.");
            return server;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ForgetAsync(string remote)
    {
        await _gate.WaitAsync();
        try
        {
            if (_servers.Remove(remote, out var server)) server.Http.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Token(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        foreach (var server in _servers.Values) server.Http.Dispose();
        _servers.Clear();
        _gate.Dispose();
    }
}
