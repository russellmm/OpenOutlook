using System.Net;
using OpenOutlook.Auth;

namespace OpenOutlook.Tests;

public sealed class DesktopOAuthRefreshTests
{
    [Theory]
    [InlineData(OAuthProvider.MicrosoftConsumers, "login.microsoftonline.com", "/consumers/oauth2/v2.0/token")]
    [InlineData(OAuthProvider.Google, "oauth2.googleapis.com", "/token")]
    public async Task RefreshUsesPinnedEndpointAndReplacesRotatedToken(OAuthProvider provider, string host, string path)
    {
        using var client = Client(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal(host, request.RequestUri.Host);
            Assert.Equal(path, request.RequestUri.AbsolutePath);
            var form = ParseForm(await request.Content!.ReadAsStringAsync());
            Assert.Equal("refresh_token", form["grant_type"]);
            Assert.Equal("public-client", form["client_id"]);
            Assert.Equal("synthetic-old", form["refresh_token"]);
            Assert.Equal(3, form.Count);
            return Json("{\"access_token\":\"new-access\",\"refresh_token\":\"synthetic-new\",\"token_type\":\"Bearer\",\"expires_in\":3600}");
        });
        var result = await DesktopOAuth.RefreshAsync(provider, "public-client", "synthetic-old", client);
        Assert.Equal("new-access", result.AccessToken);
        Assert.Equal("synthetic-new", result.RefreshToken);
        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(59));
    }

    [Fact]
    public async Task MissingRotationRetainsOriginalRefreshToken()
    {
        using var client = Client(_ => Task.FromResult(Json("{\"access_token\":\"new-access\",\"token_type\":\"Bearer\",\"expires_in\":3600}")));
        var result = await DesktopOAuth.RefreshAsync(OAuthProvider.Google, "public-client", "synthetic-old", client);
        Assert.Equal("synthetic-old", result.RefreshToken);
    }

    [Fact]
    public async Task RedirectAndProviderErrorsDoNotRevealTokens()
    {
        using var redirect = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://attacker.example/?token=synthetic-old") }
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DesktopOAuth.RefreshAsync(OAuthProvider.Google, "public-client", "synthetic-old", redirect));
        Assert.DoesNotContain("synthetic-old", error.ToString());

        using var denied = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("synthetic-old")
        }));
        error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DesktopOAuth.RefreshAsync(OAuthProvider.Google, "public-client", "synthetic-old", denied));
        Assert.DoesNotContain("synthetic-old", error.ToString());
    }

    [Theory]
    [InlineData("{\"access_token\":\"new\",\"token_type\":\"Bearer\",\"refresh_token\":\"\"}")]
    [InlineData("{\"access_token\":\"new\",\"token_type\":\"mac\"}")]
    [InlineData("{\"access_token\":\"\",\"token_type\":\"Bearer\"}")]
    public async Task RejectsInvalidRefreshResponses(string json)
    {
        using var client = Client(_ => Task.FromResult(Json(json)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DesktopOAuth.RefreshAsync(OAuthProvider.Google, "public-client", "synthetic-old", client));
    }

    [Fact]
    public async Task InvalidInputAndCancellationDoNotSend()
    {
        var calls = 0;
        using var client = Client(_ => { calls++; throw new Exception("should not send"); });
        await Assert.ThrowsAsync<ArgumentException>(() => DesktopOAuth.RefreshAsync(OAuthProvider.Google, "public-client", "bad\ntoken", client));
        await Assert.ThrowsAsync<ArgumentException>(() => DesktopOAuth.RefreshAsync(OAuthProvider.Google, "", "synthetic-old", client));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DesktopOAuth.RefreshAsync(OAuthProvider.Google, "public-client", "synthetic-old", client, canceled.Token));
        Assert.Equal(0, calls);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static Dictionary<string, string> ParseForm(string body) => body.Split('&')
        .Select(part => part.Split('=', 2))
        .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1].Replace('+', ' ')));
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) =>
        new(new FakeHandler(responder));

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => responder(request);
    }
}
