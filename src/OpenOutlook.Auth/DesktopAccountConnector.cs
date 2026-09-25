namespace OpenOutlook.Auth;

/// <summary>
/// Browser authorization, provider identity verification, explicit identity confirmation and
/// keyring handoff for one account. The desktop must still supply a real system-browser launcher,
/// user-owned client ID, confirmation UI and a verified persistent secret store.
/// </summary>
public static class DesktopAccountConnector
{
    private static readonly HashSet<string> GmailProfileScopes = new(StringComparer.Ordinal)
    {
        "https://mail.google.com/", "https://www.googleapis.com/auth/gmail.modify",
        "https://www.googleapis.com/auth/gmail.compose", "https://www.googleapis.com/auth/gmail.readonly",
        "https://www.googleapis.com/auth/gmail.metadata"
    };

    public static async Task<VerifiedAccountIdentity> ConnectAsync(OAuthProvider provider, string clientId,
        IEnumerable<string> scopes, Func<Uri, CancellationToken, Task> launchBrowser,
        Func<VerifiedAccountIdentity, CancellationToken, Task<bool>> confirmIdentity,
        ISecretStore secrets, HttpClient? tokenHttpClient = null, HttpClient? identityHttpClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(launchBrowser);
        ArgumentNullException.ThrowIfNull(confirmIdentity);
        ArgumentNullException.ThrowIfNull(secrets);
        cancellationToken.ThrowIfCancellationRequested();
        var requested = scopes.ToArray();
        if (provider == OAuthProvider.MicrosoftConsumers &&
            (!requested.Contains("offline_access", StringComparer.Ordinal) ||
             !requested.Contains("User.Read", StringComparer.Ordinal)))
            throw new ArgumentException("Microsoft account setup requires offline_access and User.Read scopes.", nameof(scopes));
        if (provider == OAuthProvider.Google && !requested.Any(GmailProfileScopes.Contains))
            throw new ArgumentException("Google account setup requires a Gmail profile-capable scope.", nameof(scopes));

        using var ownedTokenClient = tokenHttpClient is null ? DesktopOAuth.CreateHttpClient() : null;
        using var ownedIdentityClient = identityHttpClient is null ? AccountIdentityVerifier.CreateHttpClient() : null;
        var tokens = await DesktopOAuthCoordinator.ConnectAsync(provider, clientId, requested, launchBrowser,
            tokenHttpClient ?? ownedTokenClient!, cancellationToken).ConfigureAwait(false);
        return await AccountConnection.CompleteAsync(provider, tokens, identityHttpClient ?? ownedIdentityClient!,
            secrets, confirmIdentity, cancellationToken).ConfigureAwait(false);
    }
}
