using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Accounts;

/// <summary>A started Nextcloud browser sign-in: the page to open and what is needed to wait for the result.</summary>
public sealed record NextcloudPendingLogin(string Server, string LoginUrl, string PollEndpoint, string PollToken);

/// <summary>
/// Sign-in to a Nextcloud. Preferred: the browser sign-in (Login Flow v2) - Nextcloud shows its own page, also with
/// two-factor authentication, and hands CloudDrive-Sync an app password of its own that the user can revoke under
/// Settings > Security. Where an operator does not allow it for programs, user name and app password are entered
/// in CloudDrive-Sync instead; a normal password is then exchanged for an app password where Nextcloud allows it.
/// </summary>
public sealed class NextcloudSignIn : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _clientName;

    public NextcloudSignIn(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
        // Nextcloud names the app password after the client, so the user recognises it later.
        _clientName = $"CloudDrive-Sync ({Environment.MachineName})";
    }

    /// <summary>
    /// Starts the browser sign-in. Throws CD-3014 when the server does not offer it (then use
    /// <see cref="SignInWithPasswordAsync"/>); the sign-in page and the poll token must belong to the server.
    /// </summary>
    public async Task<NextcloudPendingLogin> StartBrowserLoginAsync(string server, CancellationToken cancellationToken = default)
    {
        server = server.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, server + "/index.php/login/v2")
        {
            // An empty POST still says so (Content-Length: 0): some servers refuse it otherwise.
            Content = new ByteArrayContent([]),
        };
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", _clientName);
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName));
        var (status, text) = await SendAsync(request, cancellationToken);
        JsonObject? flow = null;
        if (status == HttpStatusCode.OK) flow = TryParse(text);
        var token = flow?["poll"]?["token"]?.GetValue<string>();
        var endpoint = flow?["poll"]?["endpoint"]?.GetValue<string>();
        var login = flow?["login"]?.GetValue<string>();
        if (token is null || endpoint is null || login is null)
            throw new CdException("CD-3014", $"no Nextcloud sign-in at {server} (HTTP {(int)status})");

        var serverHost = new Uri(server).Host;
        foreach (var address in new[] { login, endpoint })
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.Host.Equals(serverHost, StringComparison.OrdinalIgnoreCase) ||
                (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
                throw new CdException("CD-3014", "the server answered with a foreign address");
        }
        Log.Info("Nextcloud", $"Browser sign-in started at {serverHost}.");
        return new NextcloudPendingLogin(server, login, endpoint, token);
    }

    /// <summary>Waits until the user granted access in the browser (Nextcloud answers 404 until then).</summary>
    public async Task<WebDavCredential> WaitForGrantAsync(NextcloudPendingLogin pending, TimeSpan timeout, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        var failures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline) throw new CdException("CD-3005");
            using var request = new HttpRequestMessage(HttpMethod.Post, pending.PollEndpoint)
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", pending.PollToken)]),
            };
            try
            {
                var (status, text) = await SendAsync(request, cancellationToken);
                if (status == HttpStatusCode.OK)
                {
                    var granted = TryParse(text);
                    var loginName = granted?["loginName"]?.GetValue<string>();
                    var appPassword = granted?["appPassword"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(loginName) || string.IsNullOrEmpty(appPassword))
                        throw new CdException("CD-3014", "the sign-in answer of Nextcloud is incomplete");
                    Log.Info("Nextcloud", "Browser sign-in received (app password created).");
                    var url = await GetDavUrlAsync(pending.Server, loginName, appPassword, cancellationToken);
                    return new WebDavCredential { Url = url, Kind = WebDavKind.Nextcloud, User = loginName, Password = appPassword };
                }
                if (status != HttpStatusCode.NotFound) throw new CdException("CD-3014", $"sign-in answer HTTP {(int)status}");
                failures = 0;
            }
            catch (CdException e) when (e.Code == "CD-5001" && ++failures < 10)
            {
                // A short network hiccup while the user signs in: keep asking for a while.
                Log.Debug("Nextcloud", $"Poll failed: {e.Detail}");
            }
            await Task.Delay(interval, cancellationToken);
        }
    }

    /// <summary>
    /// Sign-in with user name and (app) password. A normal password is exchanged for an app password of CloudDrive-Sync
    /// where Nextcloud allows it, so only a revocable app password is stored. A refused password ends at once
    /// (CD-3012), before more attempts count against the account.
    /// </summary>
    public async Task<WebDavCredential> SignInWithPasswordAsync(string server, string? davUrl, string user, string password, CancellationToken cancellationToken = default)
    {
        server = server.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, server + "/ocs/v2.php/core/getapppassword?format=json");
        request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", _clientName);
        request.Headers.Authorization = Basic(user, password);
        HttpStatusCode? status = null;
        string text = "";
        try
        {
            (status, text) = await SendAsync(request, cancellationToken);
        }
        catch (CdException e) when (e.Code == "CD-5001")
        {
            Log.Debug("Nextcloud", $"App password exchange unavailable: {e.Detail}");
        }
        if (status == HttpStatusCode.Unauthorized) throw new CdException("CD-3012", "Nextcloud refused the user name or the password (401)");
        if (status == HttpStatusCode.OK && TryParse(text)?["ocs"]?["data"]?["apppassword"]?.GetValue<string>() is { Length: > 0 } appPassword)
        {
            password = appPassword;
            Log.Info("Nextcloud", "Password exchanged for an app password of CloudDrives.");
        }
        // Any other answer (403: it already is an app password) keeps the password as entered.
        var url = string.IsNullOrEmpty(davUrl) ? await GetDavUrlAsync(server, user, password, cancellationToken) : davUrl;
        return new WebDavCredential { Url = url, Kind = WebDavKind.Nextcloud, User = user, Password = password };
    }

    /// <summary>The WebDAV address contains the user ID, which may differ from the login name (e.g. an e-mail).</summary>
    private async Task<string> GetDavUrlAsync(string server, string user, string password, CancellationToken cancellationToken)
    {
        var userId = user;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, server + "/ocs/v1.php/cloud/user?format=json");
            request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");
            request.Headers.Authorization = Basic(user, password);
            var (status, text) = await SendAsync(request, cancellationToken);
            if (status == HttpStatusCode.OK && TryParse(text)?["ocs"]?["data"]?["id"]?.GetValue<string>() is { Length: > 0 } id) userId = id;
        }
        catch (CdException e)
        {
            Log.Debug("Nextcloud", $"User ID unavailable, using the login name: {e.Detail}");
        }
        return $"{server}/remote.php/dav/files/{Uri.EscapeDataString(userId)}";
    }

    private async Task<(HttpStatusCode Status, string Text)> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (HttpRequestException e)
        {
            throw new CdException("CD-5001", $"{request.Method} {request.RequestUri?.GetLeftPart(UriPartial.Path)}: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CdException("CD-5001", $"{request.Method} {request.RequestUri?.GetLeftPart(UriPartial.Path)}: timeout", e);
        }
    }

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    private static JsonObject? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
