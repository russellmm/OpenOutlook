namespace OpenOutlook.Auth;

/// <summary>Persistent refresh-token storage. Implementations must never write tokens to ordinary files or command-line arguments.</summary>
public interface ISecretStore
{
    Task StoreRefreshTokenAsync(OAuthProvider provider, string account, string refreshToken, CancellationToken cancellationToken = default);
    Task<string?> GetRefreshTokenAsync(OAuthProvider provider, string account, CancellationToken cancellationToken = default);
    Task DeleteRefreshTokenAsync(OAuthProvider provider, string account, CancellationToken cancellationToken = default);
}

/// <summary>Raised without an inner exception so backend errors cannot disclose a token.</summary>
public sealed class SecretStoreException : InvalidOperationException
{
    public SecretStoreException(string message) : base(message) { }
}

/// <summary>Explicit fail-closed choice for unsupported platforms or disabled keyrings; never falls back to plaintext.</summary>
public sealed class UnavailableSecretStore : ISecretStore
{
    private const string Error = "Persistent Secret Service keyring is unavailable. Unlock/configure a persistent default keyring; refresh tokens were not saved.";
    public Task StoreRefreshTokenAsync(OAuthProvider provider, string account, string refreshToken, CancellationToken cancellationToken = default)
    {
        SecretStoreKeys.Validate(provider, account);
        SecretStoreKeys.ValidateToken(refreshToken);
        cancellationToken.ThrowIfCancellationRequested();
        throw new SecretStoreException(Error);
    }
    public Task<string?> GetRefreshTokenAsync(OAuthProvider provider, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreKeys.Validate(provider, account);
        cancellationToken.ThrowIfCancellationRequested();
        throw new SecretStoreException(Error);
    }
    public Task DeleteRefreshTokenAsync(OAuthProvider provider, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreKeys.Validate(provider, account);
        cancellationToken.ThrowIfCancellationRequested();
        throw new SecretStoreException(Error);
    }
}

internal static class SecretStoreKeys
{
    public static void Validate(OAuthProvider provider, string account)
    {
        if (provider is not (OAuthProvider.Google or OAuthProvider.MicrosoftConsumers))
            throw new ArgumentOutOfRangeException(nameof(provider), "Unsupported OAuth provider.");
        if (string.IsNullOrWhiteSpace(account) || account.Length > 320 || account.Any(char.IsControl) ||
            account != account.Trim() || account.Contains('\0'))
            throw new ArgumentException("Invalid account identifier.", nameof(account));
    }

    public static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 65536 || token.Any(char.IsControl))
            throw new ArgumentException("Invalid refresh token.", nameof(token));
    }
}
