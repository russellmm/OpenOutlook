using OpenOutlook.Auth;

namespace OpenOutlook.Tests;

public sealed class SecretStoreTests
{
    [Fact]
    public async Task MockStoreSupportsAccountAndProviderIsolationWithoutKeyring()
    {
        ISecretStore store = new MemoryStore();
        await store.StoreRefreshTokenAsync(OAuthProvider.Google, "alpha@example.test", "synthetic-a");
        await store.StoreRefreshTokenAsync(OAuthProvider.MicrosoftConsumers, "alpha@example.test", "synthetic-b");
        await store.StoreRefreshTokenAsync(OAuthProvider.Google, "beta@example.test", "synthetic-c");
        Assert.Equal("synthetic-a", await store.GetRefreshTokenAsync(OAuthProvider.Google, "alpha@example.test"));
        Assert.Equal("synthetic-b", await store.GetRefreshTokenAsync(OAuthProvider.MicrosoftConsumers, "alpha@example.test"));
        await store.DeleteRefreshTokenAsync(OAuthProvider.Google, "alpha@example.test");
        Assert.Null(await store.GetRefreshTokenAsync(OAuthProvider.Google, "alpha@example.test"));
        Assert.Equal("synthetic-c", await store.GetRefreshTokenAsync(OAuthProvider.Google, "beta@example.test"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" lead")]
    [InlineData("trailing ")]
    [InlineData("newline\nunsafe")]
    [InlineData("embedded\0null")]
    public async Task InvalidAccountIsRejectedBeforeUnavailableKeyring(string account)
    {
        var store = new UnavailableSecretStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.StoreRefreshTokenAsync(OAuthProvider.Google, account, "synthetic"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetRefreshTokenAsync(OAuthProvider.Google, account));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteRefreshTokenAsync(OAuthProvider.Google, account));
    }

    [Fact]
    public async Task InvalidProviderAndTokenAreRejected()
    {
        var store = new UnavailableSecretStore();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetRefreshTokenAsync((OAuthProvider)777, "alpha@example.test"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.StoreRefreshTokenAsync(OAuthProvider.Google, "alpha@example.test", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => store.StoreRefreshTokenAsync(OAuthProvider.Google, "alpha@example.test", "line\nbreak"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetRefreshTokenAsync(OAuthProvider.Google, new string('x', 321)));
    }

    [Fact]
    public async Task UnavailableStoreFailsClosedAndNeverLeaksSecretInError()
    {
        ISecretStore store = new UnavailableSecretStore();
        const string marker = "synthetic-refresh-marker-not-a-real-token";
        var error = await Assert.ThrowsAsync<SecretStoreException>(() => store.StoreRefreshTokenAsync(OAuthProvider.Google, "alpha@example.test", marker));
        Assert.Contains("persistent", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(marker, error.ToString());
        Assert.Null(error.InnerException);
        await Assert.ThrowsAsync<SecretStoreException>(() => store.GetRefreshTokenAsync(OAuthProvider.Google, "alpha@example.test"));
        await Assert.ThrowsAsync<SecretStoreException>(() => store.DeleteRefreshTokenAsync(OAuthProvider.Google, "alpha@example.test"));
    }

    private sealed class MemoryStore : ISecretStore
    {
        private readonly Dictionary<(OAuthProvider, string), string> _tokens = new();
        public Task StoreRefreshTokenAsync(OAuthProvider provider, string account, string refreshToken, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _tokens[(provider, account)] = refreshToken;
            return Task.CompletedTask;
        }
        public Task<string?> GetRefreshTokenAsync(OAuthProvider provider, string account, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_tokens.GetValueOrDefault((provider, account)));
        }
        public Task DeleteRefreshTokenAsync(OAuthProvider provider, string account, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _tokens.Remove((provider, account));
            return Task.CompletedTask;
        }
    }
}
