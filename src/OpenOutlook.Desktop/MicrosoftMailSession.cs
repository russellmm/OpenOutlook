using OpenOutlook.Auth;

namespace OpenOutlook.Desktop;

/// <summary>Reuses one short-lived access token in memory and refreshes it from the persistent keyring.</summary>
public sealed class MicrosoftMailSession(ConnectedAccount account, ISecretStore secrets, HttpClient tokenHttpClient)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset? _expiresAt;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return _accessToken;
            var refreshed = await OAuthAccountRefresh.RefreshAsync(account.Provider, account.AccountId,
                account.ClientId, secrets, tokenHttpClient, cancellationToken).ConfigureAwait(false);
            _accessToken = refreshed.AccessToken;
            _expiresAt = refreshed.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30);
            return _accessToken;
        }
        finally { _gate.Release(); }
    }
}
