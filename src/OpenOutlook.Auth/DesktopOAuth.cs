using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenOutlook.Auth;

public enum OAuthProvider { MicrosoftConsumers, Google }

/// <summary>A single-use, in-memory desktop authorization transaction. Do not persist or log its secrets.</summary>
public sealed class DesktopAuthorization
{
    private int _used;
    internal DesktopAuthorization(OAuthProvider provider, string clientId, Uri redirectUri, string verifier, string state, Uri authorizationUri)
    {
        Provider = provider;
        ClientId = clientId;
        RedirectUri = redirectUri;
        CodeVerifier = verifier;
        State = state;
        AuthorizationUri = authorizationUri;
    }

    public OAuthProvider Provider { get; }
    public string ClientId { get; }
    public Uri RedirectUri { get; }
    public string CodeVerifier { get; }
    public string State { get; }
    public Uri AuthorizationUri { get; }
    internal bool TryConsume() => Interlocked.Exchange(ref _used, 1) == 0;
}

public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt);

/// <summary>Deliberately generic OAuth desktop PKCE; no password flow, secret, token storage, browser or listener.</summary>
public static class DesktopOAuth
{
    private static readonly Uri MicrosoftAuthorization = new("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize");
    private static readonly Uri MicrosoftToken = new("https://login.microsoftonline.com/consumers/oauth2/v2.0/token");
    private static readonly Uri GoogleAuthorization = new("https://accounts.google.com/o/oauth2/v2/auth");
    private static readonly Uri GoogleToken = new("https://oauth2.googleapis.com/token");

    /// <summary>Creates an authorization URL for a registered, caller-supplied public desktop client ID.</summary>
    public static DesktopAuthorization Begin(OAuthProvider provider, string clientId, Uri redirectUri, IEnumerable<string> scopes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (clientId.Length > 1024 || clientId.Any(char.IsControl)) throw new ArgumentException("Invalid client ID.", nameof(clientId));
        ValidateRedirect(redirectUri);
        ArgumentNullException.ThrowIfNull(scopes);
        var scopeList = scopes.ToArray();
        if (scopeList.Length == 0 || scopeList.Length > 32 || scopeList.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 256 || s.Any(char.IsWhiteSpace) || s.Any(char.IsControl)))
            throw new ArgumentException("Invalid scopes.", nameof(scopes));
        var authorizationEndpoint = provider switch
        {
            OAuthProvider.MicrosoftConsumers => MicrosoftAuthorization,
            OAuthProvider.Google => GoogleAuthorization,
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', scopeList),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        // A refresh token is provider-dependent; callers must supply the scopes their app requires.
        var url = new Uri(authorizationEndpoint.AbsoluteUri + "?" + Encode(parameters));
        return new DesktopAuthorization(provider, clientId, redirectUri, verifier, state, url);
    }

    /// <summary>Creates the default transport with redirects disabled. Custom HttpClients used by tests must also disable handler-level redirects.</summary>
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false });

    /// <summary>Consumes the transaction exactly once, verifies callback, then exchanges the code at a pinned HTTPS endpoint.</summary>
    /// <remarks>Injected clients must not auto-follow redirects: HttpClient cannot inspect or override a supplied handler's redirect policy.</remarks>
    public static async Task<OAuthTokens> ExchangeAsync(DesktopAuthorization authorization, Uri callbackUri, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (!authorization.TryConsume()) throw new InvalidOperationException("Authorization response already consumed.");
        ValidateCallback(authorization.RedirectUri, callbackUri);
        var parameters = ParseQuery(callbackUri.Query);
        if (!parameters.TryGetValue("state", out var receivedState) || !FixedTimeEquals(authorization.State, receivedState))
            throw new InvalidOperationException("Invalid authorization state.");
        if (parameters.ContainsKey("error")) throw new InvalidOperationException("Authorization denied by provider.");
        if (!parameters.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code) || code.Length > 4096)
            throw new InvalidOperationException("Invalid authorization response.");

        var endpoint = authorization.Provider switch
        {
            OAuthProvider.MicrosoftConsumers => MicrosoftToken,
            OAuthProvider.Google => GoogleToken,
            _ => throw new InvalidOperationException("Invalid provider.")
        };
        // Pinned absolute endpoint; do not derive token URL from the callback, response or HttpClient.BaseAddress.
        if (endpoint.Scheme != Uri.UriSchemeHttps || endpoint.IsDefaultPort == false || endpoint.UserInfo.Length != 0)
            throw new InvalidOperationException("Invalid token endpoint.");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = authorization.ClientId,
            ["code"] = code,
            ["redirect_uri"] = authorization.RedirectUri.AbsoluteUri,
            ["code_verifier"] = authorization.CodeVerifier
        });
        HttpResponseMessage response;
        try { response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new InvalidOperationException("Token endpoint request failed."); }
        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new InvalidOperationException("Token endpoint redirected unexpectedly.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Token endpoint rejected the authorization code.");
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > 65536) throw new InvalidOperationException("Invalid token response.");
                    buffer.Write(chunk, 0, count);
                }
                buffer.Position = 0;
                using var json = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(access.GetString()) ||
                    !root.TryGetProperty("token_type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "Bearer")
                    throw new InvalidOperationException("Invalid token response.");
                string? refresh = null;
                if (root.TryGetProperty("refresh_token", out var refreshElement))
                {
                    if (refreshElement.ValueKind != JsonValueKind.String) throw new InvalidOperationException("Invalid token response.");
                    refresh = refreshElement.GetString();
                }
                DateTimeOffset? expires = null;
                if (root.TryGetProperty("expires_in", out var seconds))
                {
                    if (!seconds.TryGetInt64(out var lifetime) || lifetime < 0 || lifetime > 315360000)
                        throw new InvalidOperationException("Invalid token response.");
                    expires = DateTimeOffset.UtcNow.AddSeconds(lifetime);
                }
                return new OAuthTokens(access.GetString()!, refresh, expires);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { throw new InvalidOperationException("Invalid token response."); }
        }
    }

    private static void ValidateRedirect(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttp || uri.UserInfo.Length != 0 ||
            uri.Host is not ("127.0.0.1" or "[::1]") || uri.Port is < 1 or > 65535 || uri.IsDefaultPort ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("%5c", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Redirect URI must be an HTTP loopback URI with an explicit port, path and no query, fragment or credentials.", nameof(uri));
    }

    private static void ValidateCallback(Uri redirect, Uri callback)
    {
        if (callback is null || !callback.IsAbsoluteUri || callback.Scheme != Uri.UriSchemeHttp ||
            callback.UserInfo.Length != 0 || callback.Fragment.Length != 0 ||
            !string.Equals(callback.Host, redirect.Host, StringComparison.Ordinal) || callback.Port != redirect.Port ||
            !string.Equals(callback.AbsolutePath, redirect.AbsolutePath, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid authorization redirect.");
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.Length > 8192 || query.Length < 2 || query[0] != '?') throw new InvalidOperationException("Invalid authorization response.");
        foreach (var part in query[1..].Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || pair[0].Length == 0 || !result.TryAdd(Decode(pair[0]), Decode(pair[1])))
                throw new InvalidOperationException("Invalid authorization response.");
        }
        return result;
    }

    private static string Decode(string value)
    {
        try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
        catch (Exception) { throw new InvalidOperationException("Invalid authorization response."); }
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var x = Encoding.UTF8.GetBytes(expected);
        var y = Encoding.UTF8.GetBytes(actual);
        return y.Length == x.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    private static string Encode(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join('&', parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
