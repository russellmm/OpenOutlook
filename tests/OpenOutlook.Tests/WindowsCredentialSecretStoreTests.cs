using OpenOutlook.Auth;

namespace OpenOutlook.Tests;

/// <summary>Real Windows Credential Manager round trips under a throwaway prefix (skipped on other systems).</summary>
public sealed class WindowsCredentialSecretStoreTests
{
    private static WindowsCredentialSecretStore Store() => new("OpenOutlookTest-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task Stores_reads_replaces_and_deletes_a_token()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = Store();
        const string account = "tester@example.test";
        try
        {
            Assert.Null(await store.GetRefreshTokenAsync(OAuthProvider.MicrosoftConsumers, account));
            await store.StoreRefreshTokenAsync(OAuthProvider.MicrosoftConsumers, account, "short-token-1");
            Assert.Equal("short-token-1", await store.GetRefreshTokenAsync(OAuthProvider.MicrosoftConsumers, account));
            Assert.Null(await store.GetRefreshTokenAsync(OAuthProvider.Google, account));       // providers are separate

            var long1 = new string('a', 4500) + "é€" + new string('b', 1000);           // several chunks, multi-byte characters
            await store.StoreRefreshTokenAsync(OAuthProvider.MicrosoftConsumers, account, long1);
            Assert.Equal(long1, await store.GetRefreshTokenAsync(OAuthProvider.MicrosoftConsumers, account));

            await store.StoreRefreshTokenAsync(OAuthProvider.MicrosoftConsumers, account, "tiny");   // a shorter token must not leave the old tail behind
            Assert.Equal("tiny", await store.GetRefreshTokenAsync(OAuthProvider.MicrosoftConsumers, account));
        }
        finally { await store.DeleteRefreshTokenAsync(OAuthProvider.MicrosoftConsumers, account); }
        Assert.Null(await store.GetRefreshTokenAsync(OAuthProvider.MicrosoftConsumers, account));
    }

    [Fact]
    public async Task Rejects_invalid_account_and_token_input()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = Store();
        await Assert.ThrowsAsync<ArgumentException>(() => store.StoreRefreshTokenAsync(OAuthProvider.Google, " ", "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.StoreRefreshTokenAsync(OAuthProvider.Google, "a@b.test", "bad\ntoken"));
    }

    [Fact]
    public void Default_store_matches_the_platform()
    {
        var store = SecretStores.CreateDefault();
        if (OperatingSystem.IsWindows()) Assert.IsType<WindowsCredentialSecretStore>(store);
        else if (OperatingSystem.IsLinux()) Assert.IsType<LibsecretSecretStore>(store);
        else Assert.IsType<UnavailableSecretStore>(store);
    }
}
