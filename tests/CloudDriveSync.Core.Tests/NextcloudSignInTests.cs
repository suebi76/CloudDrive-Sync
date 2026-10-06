using System.Net;
using System.Text;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Errors;

namespace CloudDriveSync.Core.Tests;

/// <summary>Nextcloud's sign-in against a simulated server.</summary>
public class NextcloudSignInTests
{
    private sealed record SeenRequest(string Path, string UserAgent, long? ContentLength, string Body);

    private sealed class FakeNextcloud : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];
        public int PendingPolls { get; set; } = 2;
        public HttpStatusCode StartStatus { get; set; } = HttpStatusCode.OK;
        public string PollEndpoint { get; set; } = "https://cloud.example.com/login/v2/poll";
        public HttpStatusCode ExchangeStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode UserStatus { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new SeenRequest(path, request.Headers.UserAgent.ToString(), request.Content?.Headers.ContentLength,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (path.EndsWith("/index.php/login/v2"))
                return Json(StartStatus, $$"""{"poll":{"token":"poll-token-1","endpoint":"{{PollEndpoint}}"},"login":"https://cloud.example.com/login/v2/flow/abc"}""");
            if (path.EndsWith("/login/v2/poll"))
            {
                if (PendingPolls-- > 0) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return Json(HttpStatusCode.OK, """{"server":"https://cloud.example.com","loginName":"max@example.com","appPassword":"app-password-1"}""");
            }
            if (path.EndsWith("/core/getapppassword"))
                return Json(ExchangeStatus, """{"ocs":{"data":{"apppassword":"exchanged-app-password"}}}""");
            if (path.EndsWith("/cloud/user"))
                return Json(UserStatus, """{"ocs":{"data":{"id":"Max Mustermann"}}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string text) =>
            new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task The_browser_sign_in_yields_an_app_password_and_the_WebDAV_address_with_the_user_ID()
    {
        var server = new FakeNextcloud();
        using var signIn = new NextcloudSignIn(server);
        var pending = await signIn.StartBrowserLoginAsync("https://cloud.example.com/");
        Assert.Equal("https://cloud.example.com/login/v2/flow/abc", pending.LoginUrl);
        var credential = await signIn.WaitForGrantAsync(pending, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1));
        Assert.Equal("https://cloud.example.com/remote.php/dav/files/Max%20Mustermann", credential.Url);
        Assert.Equal("max@example.com", credential.User);
        Assert.Equal("app-password-1", credential.Password);
        Assert.Equal("nextcloud", credential.Vendor);

        var start = server.Requests[0];
        Assert.Equal($"CloudDrive-Sync ({Environment.MachineName})", start.UserAgent);
        Assert.Equal(0, start.ContentLength);
        var polls = server.Requests.Where(r => r.Path.EndsWith("/poll")).ToList();
        Assert.Equal(3, polls.Count);
        Assert.All(polls, poll => Assert.Equal("token=poll-token-1", poll.Body));
    }

    [Fact]
    public async Task A_server_without_browser_sign_in_is_reported_for_the_password_way()
    {
        using var signIn = new NextcloudSignIn(new FakeNextcloud { StartStatus = HttpStatusCode.Forbidden });
        var error = await Assert.ThrowsAsync<CdException>(() => signIn.StartBrowserLoginAsync("https://cloud.example.com"));
        Assert.Equal("CD-3014", error.Code);
    }

    [Fact]
    public async Task The_token_never_goes_to_another_server()
    {
        using var signIn = new NextcloudSignIn(new FakeNextcloud { PollEndpoint = "https://evil.example.org/poll" });
        Assert.Equal("CD-3014", (await Assert.ThrowsAsync<CdException>(() => signIn.StartBrowserLoginAsync("https://cloud.example.com"))).Code);
    }

    [Fact]
    public async Task Waiting_ends_after_the_time_limit()
    {
        using var signIn = new NextcloudSignIn(new FakeNextcloud { PendingPolls = 1000 });
        var pending = await signIn.StartBrowserLoginAsync("https://cloud.example.com");
        Assert.Equal("CD-3005", (await Assert.ThrowsAsync<CdException>(() => signIn.WaitForGrantAsync(pending, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(5)))).Code);
    }

    [Fact]
    public async Task A_normal_password_is_exchanged_for_an_app_password()
    {
        using var signIn = new NextcloudSignIn(new FakeNextcloud());
        var credential = await signIn.SignInWithPasswordAsync("https://cloud.example.com", null, "max", "normal-password");
        Assert.Equal("exchanged-app-password", credential.Password);
        Assert.Equal("https://cloud.example.com/remote.php/dav/files/Max%20Mustermann", credential.Url);
    }

    [Fact]
    public async Task An_app_password_is_kept_and_a_given_address_taken_over()
    {
        var server = new FakeNextcloud { ExchangeStatus = HttpStatusCode.Forbidden };
        using var signIn = new NextcloudSignIn(server);
        var credential = await signIn.SignInWithPasswordAsync("https://cloud.example.com", "https://cloud.example.com/remote.php/dav/files/X", "max", "app-password-2");
        Assert.Equal("app-password-2", credential.Password);
        Assert.Equal("https://cloud.example.com/remote.php/dav/files/X", credential.Url);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_refused_password_stops_at_once()
    {
        var server = new FakeNextcloud { ExchangeStatus = HttpStatusCode.Unauthorized };
        using var signIn = new NextcloudSignIn(server);
        Assert.Equal("CD-3012", (await Assert.ThrowsAsync<CdException>(() => signIn.SignInWithPasswordAsync("https://cloud.example.com", null, "max", "wrong"))).Code);
        Assert.Single(server.Requests);
    }
}
