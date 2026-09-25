using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenOutlook.Auth;

public sealed record VerifiedAccountIdentity(OAuthProvider Provider, string AccountId, string DisplayAddress);

/// <summary>Reads the identity tied to one OAuth access token before any refresh token is stored.</summary>
public static class AccountIdentityVerifier
{
    private const int MaxResponseBytes = 64 * 1024;
    private static readonly Uri MicrosoftProfile = new("https://graph.microsoft.com/v1.0/me?$select=id,mail,userPrincipalName");
    private static readonly Uri GoogleProfile = new("https://gmail.googleapis.com/gmail/v1/users/me/profile");

    /// <summary>Production transport; injected test transports must also disable automatic redirects.</summary>
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false });

    public static async Task<VerifiedAccountIdentity> VerifyAsync(OAuthProvider provider, string accessToken,
        HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        SecretStoreKeys.ValidateToken(accessToken);
        ArgumentNullException.ThrowIfNull(httpClient);
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = provider switch
        {
            OAuthProvider.MicrosoftConsumers => MicrosoftProfile,
            OAuthProvider.Google => GoogleProfile,
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != endpoint || (int)response.StatusCode is >= 300 and < 400)
                throw new InvalidOperationException("Account identity request redirected.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Account identity could not be verified.");
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new InvalidOperationException("Account identity response is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (bytes.Length + read > MaxResponseBytes)
                    throw new InvalidOperationException("Account identity response is too large.");
                bytes.Write(chunk, 0, read);
            }
            bytes.Position = 0;
            using var json = await JsonDocument.ParseAsync(bytes, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid account identity response.");
            if (provider == OAuthProvider.Google)
            {
                var address = Required(root, "emailAddress", 320);
                if (!address.Contains('@') || address.Any(char.IsWhiteSpace))
                    throw new InvalidOperationException("Invalid account identity response.");
                return new VerifiedAccountIdentity(provider, address.ToLowerInvariant(), address);
            }
            var id = Required(root, "id", 256);
            var display = Optional(root, "mail", 320) ?? Optional(root, "userPrincipalName", 320) ?? id;
            return new VerifiedAccountIdentity(provider, id, display);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new InvalidOperationException("Account identity could not be verified."); }
    }

    private static string Required(JsonElement root, string name, int maximum) =>
        Optional(root, name, maximum) ?? throw new InvalidOperationException("Invalid account identity response.");

    private static string? Optional(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.String) throw new InvalidOperationException("Invalid account identity response.");
        var value = field.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value != value.Trim() || value.Any(char.IsControl))
            throw new InvalidOperationException("Invalid account identity response.");
        return value;
    }
}
