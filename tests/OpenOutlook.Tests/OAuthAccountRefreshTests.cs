using System.Net;
using OpenOutlook.Auth;

namespace OpenOutlook.Tests;

public sealed class OAuthAccountRefreshTests
{
    [Fact]
    public async Task RotatedTokenIsStoredBeforeAccessTokenIsReturned()
    {
        var store = new FakeStore { Token = "old" };
        using var client = Client("{\"access_token\":\"access\",\"refresh_token\":\"new\",\"token_type\":\"Bearer\"}");
        var result = await OAuthAccountRefresh.RefreshAsync(OAuthProvider.Google, "verified-account", "public-client", store, client);
        Assert.Equal("access", result.AccessToken);
        Assert.Equal("new", store.Token);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task MissingRotationKeepsStoredToken()
    {
        var store = new FakeStore { Token = "old" };
        using var client = Client("{\"access_token\":\"access\",\"token_type\":\"Bearer\"}");
        var result = await OAuthAccountRefresh.RefreshAsync(OAuthProvider.Google, "verified-account", "public-client", store, client);
        Assert.Equal("old", result.RefreshToken);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task StorageFailureDoesNotReleaseRotatedAccessToken()
    {
        var store = new FakeStore { Token = "old", FailWrite = true };
        using var client = Client("{\"access_token\":\"access\",\"refresh_token\":\"new\",\"token_type\":\"Bearer\"}");
        await Assert.ThrowsAsync<SecretStoreException>(() =>
            OAuthAccountRefresh.RefreshAsync(OAuthProvider.Google, "verified-account", "public-client", store, client));
        Assert.Equal("old", store.Token);
    }

    [Fact]
    public async Task MissingStoredTokenRequiresReconnectWithoutNetwork()
    {
        var store = new FakeStore();
        using var client = new HttpClient(new FakeHandler(_ => throw new Exception("Should not send")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OAuthAccountRefresh.RefreshAsync(OAuthProvider.Google, "verified-account", "public-client", store, client));
        Assert.Contains("sign-in", error.Message);
    }

    private static HttpClient Client(string json) => new(new FakeHandler(_ => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) })));

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class FakeStore : ISecretStore
    {
        public string? Token { get; set; }
        public bool FailWrite { get; set; }
        public int Writes { get; private set; }
        public Task StoreRefreshTokenAsync(OAuthProvider provider, string account, string refreshToken, CancellationToken cancellationToken = default)
        {
            if (FailWrite) throw new SecretStoreException("Synthetic storage failure.");
            Token = refreshToken;
            Writes++;
            return Task.CompletedTask;
        }
        public Task<string?> GetRefreshTokenAsync(OAuthProvider provider, string account, CancellationToken cancellationToken = default) => Task.FromResult(Token);
        public Task DeleteRefreshTokenAsync(OAuthProvider provider, string account, CancellationToken cancellationToken = default)
        {
            Token = null;
            return Task.CompletedTask;
        }
    }
}
