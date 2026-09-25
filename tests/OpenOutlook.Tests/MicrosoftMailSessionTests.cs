using System.Net;
using OpenOutlook.Auth;
using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class MicrosoftMailSessionTests
{
    [Fact]
    public async Task SavedRefreshTokenWorksAcrossNewDesktopSessions()
    {
        var account = new ConnectedAccount(OAuthProvider.MicrosoftConsumers, "verified-id",
            "owner@hotmail.test", "app-client-id", DateTimeOffset.UtcNow);
        var store = new PersistentFakeStore("saved-refresh-token");
        var exchanges = 0;
        using var tokenHttp = new HttpClient(new Handler(request =>
        {
            exchanges++;
            Assert.Equal("login.microsoftonline.com", request.RequestUri!.Host);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"new-access-token","token_type":"Bearer","expires_in":3600}"""),
                RequestMessage = request
            };
        }));
        var firstRun = new MicrosoftMailSession(account, store, tokenHttp);
        Assert.Equal("new-access-token", await firstRun.GetAccessTokenAsync());
        Assert.Equal("new-access-token", await firstRun.GetAccessTokenAsync());
        Assert.Equal(1, exchanges);

        var restartedApp = new MicrosoftMailSession(account, store, tokenHttp);
        Assert.Equal("new-access-token", await restartedApp.GetAccessTokenAsync());
        Assert.Equal(2, exchanges);
        Assert.Equal("saved-refresh-token", store.Token);
    }

    private sealed class PersistentFakeStore(string token) : ISecretStore
    {
        public string Token { get; private set; } = token;
        public Task StoreRefreshTokenAsync(OAuthProvider provider, string account, string refreshToken,
            CancellationToken cancellationToken = default)
        { Token = refreshToken; return Task.CompletedTask; }
        public Task<string?> GetRefreshTokenAsync(OAuthProvider provider, string account,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(Token);
        public Task DeleteRefreshTokenAsync(OAuthProvider provider, string account,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
