using System.Net;
using OpenOutlook.Auth;

namespace OpenOutlook.Tests;

public sealed class AccountConnectionTests
{
    [Theory]
    [InlineData(OAuthProvider.Google, "gmail.googleapis.com", "/gmail/v1/users/me/profile", """{"emailAddress":"User@Example.test"}""", "user@example.test", "User@Example.test")]
    [InlineData(OAuthProvider.MicrosoftConsumers, "graph.microsoft.com", "/v1.0/me", """{"id":"graph-user-id","mail":"owner@example.test"}""", "graph-user-id", "owner@example.test")]
    public async Task VerifiesIdentityAndConfirmsBeforeSaving(OAuthProvider provider, string host, string path,
        string profile, string expectedId, string expectedDisplay)
    {
        var events = new List<string>();
        using var client = Client(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal(host, request.RequestUri.Host);
            Assert.Equal(path, request.RequestUri.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-access", request.Headers.Authorization?.Parameter);
            events.Add("verify");
            return Json(profile);
        });
        var store = new FakeStore((account, token) =>
        {
            Assert.Equal(expectedId, account);
            Assert.Equal("synthetic-refresh", token);
            events.Add("save");
        });
        var result = await AccountConnection.CompleteAsync(provider,
            new OAuthTokens("synthetic-access", "synthetic-refresh", null), client, store,
            (identity, _) =>
            {
                Assert.Equal(expectedId, identity.AccountId);
                Assert.Equal(expectedDisplay, identity.DisplayAddress);
                events.Add("confirm");
                return Task.FromResult(true);
            });
        Assert.Equal(expectedId, result.AccountId);
        Assert.Equal(["verify", "confirm", "save"], events);
    }

    [Fact]
    public async Task MissingRefreshTokenDoesNotContactProviderOrStore()
    {
        var calls = 0;
        using var client = Client(_ => { calls++; throw new Exception("Unexpected request"); });
        var store = new FakeStore((_, _) => calls++);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccountConnection.CompleteAsync(OAuthProvider.Google,
            new OAuthTokens("synthetic-access", null, null), client, store, (_, _) => Task.FromResult(true)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task IdentityDeclineAndBadProfileDoNotStoreToken()
    {
        var writes = 0;
        var store = new FakeStore((_, _) => writes++);
        using var client = Client(_ => Json("""{"emailAddress":"first@example.test"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccountConnection.CompleteAsync(OAuthProvider.Google,
            new OAuthTokens("synthetic-access", "synthetic-refresh", null), client, store,
            (_, _) => Task.FromResult(false)));
        Assert.Equal(0, writes);

        using var malformed = Client(_ => Json("""{"emailAddress":"\nunsafe@example.test"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccountConnection.CompleteAsync(OAuthProvider.Google,
            new OAuthTokens("synthetic-access", "synthetic-refresh", null), malformed, store,
            (_, _) => throw new Exception("Must not confirm")));
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task RedirectAndTransportErrorsAreSanitized()
    {
        using var redirect = Client(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri("https://attacker.example/steal?token=synthetic-access") } });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AccountIdentityVerifier.VerifyAsync(
            OAuthProvider.Google, "synthetic-access", redirect));
        Assert.DoesNotContain("synthetic-access", error.ToString());
        Assert.DoesNotContain("attacker.example", error.ToString());

        using var throwing = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("synthetic-access")));
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => AccountIdentityVerifier.VerifyAsync(
            OAuthProvider.Google, "synthetic-access", throwing));
        Assert.DoesNotContain("synthetic-access", error.ToString());
    }

    [Fact]
    public async Task OversizedIdentityAndCanceledRequestFailClosed()
    {
        using var large = Client(_ => Json("{\"emailAddress\":\"" + new string('x', 70000) + "@example.test\"}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccountIdentityVerifier.VerifyAsync(
            OAuthProvider.Google, "synthetic-access", large));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var client = Client(_ => throw new Exception("Request after cancellation"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AccountIdentityVerifier.VerifyAsync(
            OAuthProvider.Google, "synthetic-access", client, canceled.Token));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new FakeHandler(respond));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
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
