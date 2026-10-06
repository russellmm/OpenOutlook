using System.Security.Cryptography;
using System.Text;

namespace OpenOutlook.Providers.Microsoft;

/// <summary>
/// Remembers that an access token was already checked against /me for an account, so each read or write does not repeat that request.
/// Off by default (tests count requests); the application turns it on. Tokens are kept only as hashes, and entries expire well before a token does.
/// </summary>
public static class GraphAccountVerification
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(45);
    private static readonly Dictionary<string, DateTime> Verified = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static bool CacheEnabled { get; set; }

    private static string Key(string accountId, string token) =>
        accountId + "|" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>One /me request: opens the connection and records that the token belongs to the account. A wrong answer records nothing.</summary>
    public static async Task WarmUpAsync(HttpClient http, string accountId, string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=id");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || (int)response.StatusCode is >= 300 and < 400) return;
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (json.RootElement.TryGetProperty("id", out var id) && id.GetString() == accountId) Mark(accountId, token);
    }

    internal static bool IsVerified(string accountId, string token)
    {
        if (!CacheEnabled) return false;
        lock (Gate) return Verified.TryGetValue(Key(accountId, token), out var until) && until > DateTime.UtcNow;
    }

    internal static void Mark(string accountId, string token)
    {
        if (!CacheEnabled) return;
        lock (Gate)
        {
            if (Verified.Count > 64)
                foreach (var stale in Verified.Where(e => e.Value <= DateTime.UtcNow).Select(e => e.Key).ToList()) Verified.Remove(stale);
            Verified[Key(accountId, token)] = DateTime.UtcNow + Lifetime;
        }
    }
}
