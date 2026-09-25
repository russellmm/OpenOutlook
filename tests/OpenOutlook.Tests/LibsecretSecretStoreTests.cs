using OpenOutlook.Auth;

namespace OpenOutlook.Tests;

public sealed class LibsecretSecretStoreTests
{
    [Fact]
    public async Task RejectsInvalidInputBeforeContactingKeyring()
    {
        ISecretStore store = new LibsecretSecretStore();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.StoreRefreshTokenAsync(OAuthProvider.Google, "bad\naccount", "synthetic"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.StoreRefreshTokenAsync(OAuthProvider.Google, "account@example.test", "bad\ntoken"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.GetRefreshTokenAsync((OAuthProvider)999, "account@example.test"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.DeleteRefreshTokenAsync(OAuthProvider.Google, ""));
    }

    [Fact]
    public async Task CanceledCallsDoNotContactKeyring()
    {
        ISecretStore store = new LibsecretSecretStore();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.StoreRefreshTokenAsync(OAuthProvider.Google, "account@example.test", "synthetic", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.GetRefreshTokenAsync(OAuthProvider.Google, "account@example.test", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.DeleteRefreshTokenAsync(OAuthProvider.Google, "account@example.test", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ((LibsecretSecretStore)store).CheckAvailabilityAsync(canceled.Token));
    }
}
