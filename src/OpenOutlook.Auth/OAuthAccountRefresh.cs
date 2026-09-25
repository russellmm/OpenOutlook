namespace OpenOutlook.Auth;

/// <summary>
/// Refresh-token handoff for a caller-verified account. This has no account registry or desktop UI.
/// A rotated secret must be durably stored before the new access token is released to the caller.
/// </summary>
public static class OAuthAccountRefresh
{
    public static async Task<OAuthTokens> RefreshAsync(OAuthProvider provider, string accountId, string clientId,
        ISecretStore secrets, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(httpClient);
        SecretStoreKeys.Validate(provider, accountId);
        cancellationToken.ThrowIfCancellationRequested();

        var existing = await secrets.GetRefreshTokenAsync(provider, accountId, cancellationToken).ConfigureAwait(false);
        if (existing is null) throw new InvalidOperationException("Account needs sign-in before mail can refresh.");
        SecretStoreKeys.ValidateToken(existing);
        var refreshed = await DesktopOAuth.RefreshAsync(provider, clientId, existing, httpClient, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(refreshed.RefreshToken, existing, StringComparison.Ordinal))
            await secrets.StoreRefreshTokenAsync(provider, accountId, refreshed.RefreshToken!, cancellationToken).ConfigureAwait(false);
        return refreshed;
    }
}
