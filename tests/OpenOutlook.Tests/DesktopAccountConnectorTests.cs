using System.Net;
using OpenOutlook.Auth;

namespace OpenOutlook.Tests;

public sealed class DesktopAccountConnectorTests
{
    [Fact]
    public async Task BrowserCallbackIdentityConfirmationAndSecretStorageRunInOrder()
    {
        var steps = new List<string>();
        using var tokenClient = new HttpClient(new FakeHandler(request =>
        {
            Assert.Equal("oauth2.googleapis.com", request.RequestUri!.Host);
            steps.Add("exchange");
            return Json("""{"access_token":"fake-access","refresh_token":"fake-refresh","token_type":"Bearer"}""");
        }));
        using var identityClient = new HttpClient(new FakeHandler(request =>
        {
            Assert.Equal("gmail.googleapis.com", request.RequestUri!.Host);
            Assert.Equal("fake-access", request.Headers.Authorization?.Parameter);
            steps.Add("identity");
            return Json("""{"emailAddress":"Owner@Example.test"}""");
        }));
        var store = new FakeStore((account, token) =>
        {
            Assert.Equal("owner@example.test", account);
            Assert.Equal("fake-refresh", token);
            steps.Add("store");
        });
        var result = await DesktopAccountConnector.ConnectAsync(OAuthProvider.Google, "public-client",
            ["https://www.googleapis.com/auth/gmail.readonly"],
            async (authorization, token) =>
            {
                steps.Add("browser");
                var query = Query(authorization);
                Assert.Equal("offline", query["access_type"]);
                var callback = new Uri(query["redirect_uri"] + "?state=" + query["state"] + "&code=fake-code");
                using var browser = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
                using var response = await browser.GetAsync(callback, token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            },
            (identity, _) =>
            {
                Assert.Equal("Owner@Example.test", identity.DisplayAddress);
                steps.Add("confirm");
                return Task.FromResult(true);
            }, store, tokenClient, identityClient);
        Assert.Equal("owner@example.test", result.AccountId);
        Assert.Equal(["browser", "exchange", "identity", "confirm", "store"], steps);
    }

    [Fact]
    public async Task MicrosoftDesktopFlowUsesLocalhostAndVerifiedGraphIdentity()
    {
        var steps = new List<string>();
        using var tokenClient = new HttpClient(new FakeHandler(request =>
        {
            Assert.Equal("login.microsoftonline.com", request.RequestUri!.Host);
            Assert.Equal("/consumers/oauth2/v2.0/token", request.RequestUri.AbsolutePath);
            steps.Add("exchange");
            return Json("""{"access_token":"fake-access","refresh_token":"fake-refresh","token_type":"Bearer"}""");
        }));
        using var identityClient = new HttpClient(new FakeHandler(request =>
        {
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            Assert.Equal("fake-access", request.Headers.Authorization?.Parameter);
            steps.Add("identity");
            return Json("""{"id":"a67f24fa-f3cf-46f3-b4ae-3451c51a64f0","userPrincipalName":"person@outlook.com"}""");
        }));
        var store = new FakeStore((account, token) =>
        {
            Assert.Equal("a67f24fa-f3cf-46f3-b4ae-3451c51a64f0", account);
            Assert.Equal("fake-refresh", token);
            steps.Add("store");
        });
        var result = await DesktopAccountConnector.ConnectAsync(OAuthProvider.MicrosoftConsumers, "public-client",
            ["offline_access", "User.Read", "Mail.Read"],
            async (authorization, token) =>
            {
                steps.Add("browser");
                var query = Query(authorization);
                Assert.Equal("localhost", new Uri(query["redirect_uri"]).Host);
                using var browser = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
                var callback = new Uri(query["redirect_uri"] + "?state=" + query["state"] + "&code=fake-code");
                using var response = await browser.GetAsync(callback, token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            },
            (identity, _) =>
            {
                Assert.Equal("person@outlook.com", identity.DisplayAddress);
                steps.Add("confirm");
                return Task.FromResult(true);
            }, store, tokenClient, identityClient);
        Assert.Equal("a67f24fa-f3cf-46f3-b4ae-3451c51a64f0", result.AccountId);
        Assert.Equal(["browser", "exchange", "identity", "confirm", "store"], steps);
    }

    [Theory]
    [InlineData(OAuthProvider.Google, "openid")]
    [InlineData(OAuthProvider.MicrosoftConsumers, "Mail.ReadWrite")]
    public async Task MissingRequiredScopesStopBeforeBrowser(OAuthProvider provider, string scope)
    {
        var store = new FakeStore((_, _) => throw new Exception("Unexpected storage"));
        await Assert.ThrowsAsync<ArgumentException>(() => DesktopAccountConnector.ConnectAsync(provider, "public-client",
            [scope], (_, _) => throw new Exception("Unexpected browser"), (_, _) => Task.FromResult(true), store));
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2))
        .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
    private sealed class FakeStore(Action<string, string> store) : ISecretStore
    {
        public Task StoreRefreshTokenAsync(OAuthProvider provider, string account, string refreshToken, CancellationToken cancellationToken = default)
        {
            store(account, refreshToken);
            return Task.CompletedTask;
        }
        public Task<string?> GetRefreshTokenAsync(OAuthProvider provider, string account, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task DeleteRefreshTokenAsync(OAuthProvider provider, string account, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
