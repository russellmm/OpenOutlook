namespace OpenOutlook.Auth;

/// <summary>Completes an already authorized transaction only after identity confirmation and keyring storage.</summary>
public static class AccountConnection
{
    public static async Task<VerifiedAccountIdentity> CompleteAsync(OAuthProvider provider, OAuthTokens tokens,
        HttpClient identityHttpClient, ISecretStore secrets,
        Func<VerifiedAccountIdentity, CancellationToken, Task<bool>> confirmIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(identityHttpClient);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(confirmIdentity);
        SecretStoreKeys.ValidateToken(tokens.AccessToken);
        if (tokens.RefreshToken is null)
            throw new InvalidOperationException("The provider did not issue offline access; account connection was not saved.");
        SecretStoreKeys.ValidateToken(tokens.RefreshToken);
        cancellationToken.ThrowIfCancellationRequested();
        var identity = await AccountIdentityVerifier.VerifyAsync(provider, tokens.AccessToken, identityHttpClient, cancellationToken)
            .ConfigureAwait(false);
        if (!await confirmIdentity(identity, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Account identity was not confirmed; connection was not saved.");
        cancellationToken.ThrowIfCancellationRequested();
        await secrets.StoreRefreshTokenAsync(provider, identity.AccountId, tokens.RefreshToken, cancellationToken)
            .ConfigureAwait(false);
        return identity;
    }
}
