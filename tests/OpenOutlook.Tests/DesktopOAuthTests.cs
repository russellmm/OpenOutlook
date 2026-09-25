using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenOutlook.Auth;

namespace OpenOutlook.Tests;

public class DesktopOAuthTests
{
    private static readonly Uri Callback = new("http://127.0.0.1:54231/callback");

    [Theory]
    [InlineData(OAuthProvider.MicrosoftConsumers, "login.microsoftonline.com", "/consumers/oauth2/v2.0/authorize", "login.microsoftonline.com", "/consumers/oauth2/v2.0/token")]
    [InlineData(OAuthProvider.Google, "accounts.google.com", "/o/oauth2/v2/auth", "oauth2.googleapis.com", "/token")]
    public async Task UsesPinnedProviderEndpointsAndS256(OAuthProvider provider, string authHost, string authPath, string tokenHost, string tokenPath)
    {
        var pending = DesktopOAuth.Begin(provider, "public-client-id", Callback, ["email", "openid"]);
        var authorization = pending.AuthorizationUri;
        Assert.Equal("https", authorization.Scheme);
        Assert.Equal(authHost, authorization.Host);
        Assert.Equal(authPath, authorization.AbsolutePath);
        var values = Query(authorization);
        Assert.Equal("public-client-id", values["client_id"]);
        Assert.Equal(Callback.AbsoluteUri, values["redirect_uri"]);
        Assert.Equal("code", values["response_type"]);
        Assert.Equal("S256", values["code_challenge_method"]);
        Assert.Equal("email openid", values["scope"]);
        Assert.Equal(43, pending.State.Length);
        Assert.Equal(43, pending.CodeVerifier.Length);
        Assert.NotEqual(pending.State, DesktopOAuth.Begin(provider, "public-client-id", Callback, ["email"]).State);
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(pending.CodeVerifier));
        Assert.Equal(Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_'), values["code_challenge"]);
        Assert.Equal(pending.State, values["state"]);

        var fake = new FakeHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal(tokenHost, request.RequestUri.Host);
            Assert.Equal(tokenPath, request.RequestUri.AbsolutePath);
            Assert.Equal(443, request.RequestUri.Port);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("code_verifier=" + pending.CodeVerifier, body);
            Assert.Contains("client_id=public-client-id", body);
            Assert.Contains("grant_type=authorization_code", body);
            Assert.DoesNotContain("client_secret", body);
            Assert.DoesNotContain("password", body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"opaque\",\"refresh_token\":\"refresh\",\"token_type\":\"Bearer\",\"expires_in\":3600}") };
        });
        using var client = new HttpClient(fake);
        var tokens = await DesktopOAuth.ExchangeAsync(pending, ResponseUri(pending, "code=abc"), client);
        Assert.Equal("opaque", tokens.AccessToken);
        Assert.Equal("refresh", tokens.RefreshToken);
        Assert.NotNull(tokens.ExpiresAt);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task StateMismatchConsumesAttemptAndCannotReplay()
    {
        var pending = NewAuthorization();
        var fake = new FakeHandler((Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)((_, _) => throw new Exception("Should never exchange")));
        using var client = new HttpClient(fake);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopOAuth.ExchangeAsync(pending, new Uri(Callback + "?code=abc&state=wrong"), client));
        Assert.DoesNotContain("wrong", error.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopOAuth.ExchangeAsync(pending, ResponseUri(pending, "code=abc"), client));
        Assert.Equal(0, fake.Calls);
    }

    [Theory]
    [InlineData("http://localhost:54231/callback")]
    [InlineData("http://127.0.0.2:54231/callback")]
    [InlineData("http://evil.example:54231/callback")]
    [InlineData("https://127.0.0.1:54231/callback")]
    [InlineData("http://user@127.0.0.1:54231/callback")]
    [InlineData("http://127.0.0.1:54231/callback?x=y")]
    [InlineData("http://127.0.0.1:54231/callback#fragment")]
    [InlineData("http://127.0.0.1/callback")]
    public void RejectsNonExactLoopbackRegistration(string address) =>
        Assert.Throws<ArgumentException>(() => DesktopOAuth.Begin(OAuthProvider.Google, "public-client", new Uri(address), ["openid"]));

    [Theory]
    [InlineData("http://localhost:54231/callback")]
    [InlineData("http://127.0.0.1:54232/callback")]
    [InlineData("http://127.0.0.1:54231/other")]
    [InlineData("https://127.0.0.1:54231/callback")]
    [InlineData("http://127.0.0.1:54231/callback#fragment")]
    [InlineData("http://127.0.0.1:54231/callback?code=abc&state=bad&state=bad")]
    public async Task RejectsModifiedCallbackWithoutNetwork(string address)
    {
        var fake = new FakeHandler((Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)((_, _) => throw new Exception("Should never exchange")));
        using var client = new HttpClient(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopOAuth.ExchangeAsync(NewAuthorization(), new Uri(address), client));
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task RejectsRedirectAndSanitizesSensitiveErrors()
    {
        var pending = NewAuthorization();
        var fake = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://attacker.example/steal?secret=opaque") },
            Content = new StringContent("access_token=opaque&client_secret=private")
        });
        using var client = new HttpClient(fake);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopOAuth.ExchangeAsync(pending, ResponseUri(pending, "code=private-code"), client));
        Assert.DoesNotContain("opaque", error.ToString());
        Assert.DoesNotContain("private", error.ToString());
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task DoesNotExposeProviderResponseOrTransportException()
    {
        var pending = NewAuthorization();
        using var client = new HttpClient(new FakeHandler((Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)((_, _) => throw new HttpRequestException("secret password bearer token"))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopOAuth.ExchangeAsync(pending, ResponseUri(pending, "code=private-code"), client));
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("private", error.ToString());
    }

    private static DesktopAuthorization NewAuthorization() => DesktopOAuth.Begin(OAuthProvider.Google, "desktop-public", Callback, ["openid"]);
    private static Uri ResponseUri(DesktopAuthorization pending, string remainder) => new(Callback + "?state=" + pending.State + "&" + remainder);
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(s => s.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _callback;
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> callback) =>
            _callback = (request, cancellationToken) => Task.FromResult(callback(request, cancellationToken));
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) => _callback = callback;
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return _callback(request, cancellationToken);
        }
    }
}
