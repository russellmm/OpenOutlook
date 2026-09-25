using System.Net;
using OpenOutlook.Auth;

namespace OpenOutlook.Tests;

public class DesktopOAuthCoordinatorTests
{
    [Fact]
    public async Task ConnectCapturesCallbackBeforeLaunchingAndExchangesCode()
    {
        var handler = new TokenHandler();
        using var tokensClient = new HttpClient(handler);
        Uri? redirect = null;
        var result = await DesktopOAuthCoordinator.ConnectAsync(OAuthProvider.Google, "desktop-client", ["openid"],
            async (authorization, token) =>
            {
                Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", authorization.GetLeftPart(UriPartial.Path));
                var query = Query(authorization);
                redirect = new Uri(query["redirect_uri"]);
                Assert.Equal(IPAddress.Loopback.ToString(), redirect.Host);
                Assert.True(redirect.Port > 0);
                // A completed HTTP callback here proves capture was started before the launcher was invoked.
                await SendCallbackAsync(redirect, query["state"], "one-time-code", token);
            }, tokensClient);
        Assert.Equal("fake-access", result.AccessToken);
        Assert.Equal("fake-refresh", result.RefreshToken);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("one-time-code", handler.Code);
        Assert.Equal(redirect, handler.Redirect);
    }

    [Fact]
    public async Task CancellationStopsCaptureWithoutCallingTokenEndpoint()
    {
        using var source = new CancellationTokenSource();
        var handler = new TokenHandler();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DesktopOAuthCoordinator.ConnectAsync(
            OAuthProvider.Google, "desktop-client", ["openid"], (_, _) =>
            {
                source.Cancel();
                return Task.CompletedTask;
            }, client, source.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task BadStateFailsClosedWithoutTokenRequest()
    {
        var handler = new TokenHandler();
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopOAuthCoordinator.ConnectAsync(
            OAuthProvider.Google, "desktop-client", ["openid"], async (authorization, token) =>
            {
                await SendCallbackAsync(new Uri(Query(authorization)["redirect_uri"]), "invalid-state", "secret-code", token);
            }, client));
        Assert.DoesNotContain("secret-code", error.ToString());
        Assert.DoesNotContain("invalid-state", error.ToString());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PreviousTransactionsStateCannotBeReplayedOnANewListener()
    {
        var handler = new TokenHandler();
        using var client = new HttpClient(handler);
        string? oldState = null;
        await DesktopOAuthCoordinator.ConnectAsync(OAuthProvider.Google, "desktop-client", ["openid"],
            async (authorization, token) =>
            {
                var query = Query(authorization);
                oldState = query["state"];
                await SendCallbackAsync(new Uri(query["redirect_uri"]), oldState, "first-code", token);
            }, client);
        Assert.Equal(1, handler.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopOAuthCoordinator.ConnectAsync(
            OAuthProvider.Google, "desktop-client", ["openid"], async (authorization, token) =>
            {
                var query = Query(authorization);
                Assert.NotEqual(oldState, query["state"]);
                await SendCallbackAsync(new Uri(query["redirect_uri"]), oldState!, "replayed-code", token);
            }, client));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task LaunchFailureClosesListenerAndSuppressesSensitiveException()
    {
        var handler = new TokenHandler();
        using var client = new HttpClient(handler);
        Uri? redirect = null;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopOAuthCoordinator.ConnectAsync(
            OAuthProvider.Google, "desktop-client", ["openid"], (authorization, _) =>
            {
                redirect = new Uri(Query(authorization)["redirect_uri"]);
                throw new InvalidOperationException("private authorization URL");
            }, client));
        Assert.DoesNotContain("private", error.ToString());
        Assert.Equal(0, handler.Calls);
        using var localClient = LocalClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => localClient.GetAsync(redirect));
    }

    private static async Task SendCallbackAsync(Uri redirect, string state, string code, CancellationToken token)
    {
        using var browser = LocalClient();
        var target = new Uri(redirect + "?state=" + Uri.EscapeDataString(state) + "&code=" + Uri.EscapeDataString(code));
        using var response = await browser.GetAsync(target, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpClient LocalClient() => new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    private sealed class TokenHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Code { get; private set; }
        public Uri? Redirect { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://oauth2.googleapis.com/token", request.RequestUri!.AbsoluteUri);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var fields = body.Split('&').Select(part => part.Split('=', 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
            Code = fields["code"];
            Redirect = new Uri(fields["redirect_uri"]);
            Assert.Equal("authorization_code", fields["grant_type"]);
            Assert.False(fields.ContainsKey("client_secret"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"fake-access\",\"refresh_token\":\"fake-refresh\",\"token_type\":\"Bearer\"}")
            };
        }
    }
}
