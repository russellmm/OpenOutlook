namespace OpenOutlook.Auth;

/// <summary>Coordinates one desktop OAuth authorization with an OS-assigned, loopback-only callback.</summary>
public static class DesktopOAuthCoordinator
{
    /// <summary>
    /// Opens the provider authorization page and exchanges exactly one captured callback for tokens.
    /// The caller owns an injected client and must configure its handler not to follow redirects.
    /// Tokens remain in memory; this method does not store or log them.
    /// </summary>
    public static async Task<OAuthTokens> ConnectAsync(
        OAuthProvider provider,
        string clientId,
        IEnumerable<string> scopes,
        Func<Uri, CancellationToken, Task> launcher,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        cancellationToken.ThrowIfCancellationRequested();

        // This reservation is private to the transaction: never accept a caller-supplied redirect URI.
        using var callback = new LoopbackAuthorizationCallback(
            useLocalhostRedirect: provider == OAuthProvider.MicrosoftConsumers);
        var authorization = DesktopOAuth.Begin(provider, clientId, callback.RedirectUri, scopes);
        // Enter the listener's accept loop before handing the authorization URI to the browser.
        var capture = callback.CaptureAsync(cancellationToken);
        try
        {
            await launcher(authorization.AuthorizationUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) // A browser integration may include the authorization URL in its exception.
        {
            callback.Dispose();
            try { await capture.ConfigureAwait(false); }
            catch (Exception) { /* Observe the stopped listener without exposing callback details. */ }
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw new InvalidOperationException("Could not launch the authorization browser.");
        }

        var capturedUri = await capture.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var ownedClient = httpClient is null ? DesktopOAuth.CreateHttpClient() : null;
        return await DesktopOAuth.ExchangeAsync(authorization, capturedUri, httpClient ?? ownedClient!, cancellationToken).ConfigureAwait(false);
    }
}
