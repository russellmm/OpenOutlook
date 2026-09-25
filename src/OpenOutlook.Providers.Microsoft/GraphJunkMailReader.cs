using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenOutlook.Providers.Microsoft;

/// <summary>Read-only Graph mail access for one explicitly identified account. The caller supplies a Mail.Read-capable token.</summary>
public sealed class GraphJunkMailReader
{
    private const string Origin = "https://graph.microsoft.com";
    private const string Root = "/v1.0";
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const string MessageSelect = "id,subject,from,sender,toRecipients,ccRecipients,importance,internetMessageHeaders";
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, ValueTask<string>> _accessToken;
    private readonly string _expectedGraphUserId;

    /// <summary>
    /// Construct a reader using a client with automatic redirects disabled. For production, use
    /// <see cref="CreateSecureHttpClient"/> or configure the supplied client's handler with
    /// AllowAutoRedirect = false; HttpClient does not expose its handler for validation here.
    /// Redirect responses are rejected rather than followed with a bearer token.
    /// </summary>
    /// <param name="httpClient">Client whose handler MUST have automatic redirects disabled.</param>
    /// <param name="accessToken">Token callback, invoked once per read operation.</param>
    /// <param name="expectedGraphUserId">Graph /me id, not a folder id or a display name. Verified before accessing mail.</param>
    public GraphJunkMailReader(HttpClient httpClient, Func<CancellationToken, ValueTask<string>> accessToken,
        string expectedGraphUserId)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        _expectedGraphUserId = !string.IsNullOrWhiteSpace(expectedGraphUserId)
            ? expectedGraphUserId : throw new ArgumentException("A Graph user id is required.", nameof(expectedGraphUserId));
    }

    /// <summary>Create a production client that never follows Graph redirects with credentials.</summary>
    /// <remarks>The caller owns and must dispose the returned client.</remarks>
    public static HttpClient CreateSecureHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false });

    /// <summary>Resolve the well-known Junk Email folder of the verified /me account; never accepts a caller-supplied folder.</summary>
    public async Task<GraphMailFolder> GetJunkFolderAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        await VerifyAccountAsync(token, cancellationToken).ConfigureAwait(false);
        return await GetJunkFolderForVerifiedAccountAsync(token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>List at most maxMessages from Junk Email, across at most the required number of pages.</summary>
    public async Task<IReadOnlyList<GraphJunkMessage>> ListJunkMessagesAsync(
        int pageSize = 50, int maxMessages = 500, CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (maxMessages is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxMessages));
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        await VerifyAccountAsync(token, cancellationToken).ConfigureAwait(false);
        var folder = await GetJunkFolderForVerifiedAccountAsync(token, cancellationToken).ConfigureAwait(false);
        var path = $"{Root}/me/mailFolders/{Uri.EscapeDataString(folder.Id)}/messages";
        var next = new Uri(Origin + path + $"?$select={MessageSelect}&$top={Math.Min(pageSize, maxMessages)}");
        var result = new List<GraphJunkMessage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pages = 0;
        while (result.Count < maxMessages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++pages > maxMessages) throw new GraphMailException("Graph pagination exceeds the scan limit.");
            if (!seen.Add(next.AbsoluteUri)) throw new GraphMailException("Graph pagination cycle detected.");
            using var json = await GetJsonAsync(next, token, cancellationToken).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
                throw new GraphMailException("Graph returned an invalid message page.");
            if (values.GetArrayLength() > pageSize) throw new GraphMailException("Graph returned an oversized message page.");
            foreach (var item in values.EnumerateArray())
            {
                if (result.Count == maxMessages) break;
                if (item.ValueKind != JsonValueKind.Object) throw new GraphMailException("Graph returned an invalid message.");
                result.Add(new GraphJunkMessage(
                    RequiredString(item, "id"), OptionalString(item, "subject"),
                    Address(item, "from"), Address(item, "sender"),
                    Addresses(item, "toRecipients"), Addresses(item, "ccRecipients"),
                    OptionalString(item, "importance"), Headers(item), HasToRecipients(item)));
            }
            if (result.Count == maxMessages || !json.RootElement.TryGetProperty("@odata.nextLink", out var link)
                || link.ValueKind == JsonValueKind.Null) break;
            if (link.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var candidate) ||
                candidate.Scheme != Uri.UriSchemeHttps || candidate.Host != "graph.microsoft.com" ||
                !candidate.IsDefaultPort || candidate.UserInfo.Length != 0 || candidate.Fragment.Length != 0 ||
                !string.Equals(candidate.AbsolutePath, path, StringComparison.Ordinal) ||
                !ValidQuery(candidate.Query, pageSize))
                throw new GraphMailException("Graph returned an unsafe pagination link.");
            next = candidate;
        }
        return result;
    }

    private async Task VerifyAccountAsync(string token, CancellationToken ct)
    {
        using var json = await GetJsonAsync(new Uri(Origin + Root + "/me?$select=id"), token, ct).ConfigureAwait(false);
        if (!string.Equals(RequiredString(json.RootElement, "id"), _expectedGraphUserId, StringComparison.Ordinal))
            throw new GraphMailException("Graph token belongs to a different account.");
    }

    private async Task<GraphMailFolder> GetJunkFolderForVerifiedAccountAsync(string token, CancellationToken ct)
    {
        using var json = await GetJsonAsync(new Uri(Origin + Root + "/me/mailFolders/junkemail?$select=id,displayName"), token, ct)
            .ConfigureAwait(false);
        return new GraphMailFolder(RequiredString(json.RootElement, "id"), OptionalString(json.RootElement, "displayName"));
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var token = await _accessToken(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token) || token.Contains('\r') || token.Contains('\n'))
            throw new GraphMailException("A valid access token is required.");
        return token;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, string token, CancellationToken ct)
    {
        // Every URI originates locally or has passed the strict nextLink checks; BaseAddress is never used.
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // Requires AllowAutoRedirect=false on an injected client; the secure factory enforces this.
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new GraphMailException("Graph returned a redirect; redirects are not allowed.", response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            // Never expose response bodies, URLs, headers, or tokens in exceptions.
            var retry = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                ? BoundedRetryAfter(response.Headers.RetryAfter) : null;
            throw new GraphMailException($"Graph read failed with HTTP {(int)response.StatusCode}.", response.StatusCode, retry);
        }
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new GraphMailException("Graph response exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaxResponseBytes) throw new GraphMailException("Graph response exceeds the size limit.");
            buffer.Write(bytes, 0, count);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: ct).ConfigureAwait(false); }
        catch (JsonException) { throw new GraphMailException("Graph returned invalid JSON."); }
    }

    private static TimeSpan? BoundedRetryAfter(RetryConditionHeaderValue? retry)
    {
        var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
        return delay is null ? null : TimeSpan.FromSeconds(Math.Clamp(delay.Value.TotalSeconds, 0, 300));
    }

    private static bool ValidQuery(string query, int pageSize)
    {
        if (string.IsNullOrEmpty(query)) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) return false;
            var key = Uri.UnescapeDataString(pair[0]);
            var value = Uri.UnescapeDataString(pair[1]);
            if (!keys.Add(key) || key is not ("$select" or "$top" or "$skip" or "$skiptoken")) return false;
            if (key == "$select" && value != MessageSelect) return false;
            if (key == "$top" && (!int.TryParse(value, out var top) || top < 1 || top > pageSize)) return false;
            if (key == "$skip" && (!int.TryParse(value, out var skip) || skip < 0)) return false;
            if (value.Length is < 1 or > 2048) return false;
        }
        return keys.Contains("$skip") || keys.Contains("$skiptoken");
    }

    private static string RequiredString(JsonElement obj, string key) =>
        OptionalString(obj, key) is { Length: > 0 } value ? value : throw new GraphMailException($"Graph omitted required {key}.");

    private static string? OptionalString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? Address(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var wrapper) && wrapper.ValueKind == JsonValueKind.Object &&
        wrapper.TryGetProperty("emailAddress", out var address) ? OptionalString(address, "address") : null;

    // Missing/malformed recipients are unknown, never evidence of a missing To address.
    private static bool? HasToRecipients(JsonElement obj)
    {
        if (!obj.TryGetProperty("toRecipients", out var array) || array.ValueKind != JsonValueKind.Array)
            return null;
        if (array.GetArrayLength() == 0) return false;
        foreach (var recipient in array.EnumerateArray())
        {
            if (recipient.ValueKind != JsonValueKind.Object ||
                !recipient.TryGetProperty("emailAddress", out var address) ||
                string.IsNullOrWhiteSpace(OptionalString(address, "address"))) return null;
        }
        return true;
    }

    private static IReadOnlyList<string> Addresses(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => x.TryGetProperty("emailAddress", out var address) ? OptionalString(address, "address") : null)
            .Where(x => x is not null).Select(x => x!).ToArray();
    }

    private static IReadOnlyList<GraphInternetHeader> Headers(JsonElement obj)
    {
        if (!obj.TryGetProperty("internetMessageHeaders", out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<GraphInternetHeader>();
        return array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => new GraphInternetHeader(OptionalString(x, "name") ?? "", OptionalString(x, "value") ?? ""))
            .ToArray();
    }
}

public sealed record GraphMailFolder(string Id, string? DisplayName);
public sealed record GraphInternetHeader(string Name, string Value);
public sealed record GraphJunkMessage(string Id, string? Subject, string? From, string? Sender,
    IReadOnlyList<string> ToRecipients, IReadOnlyList<string> CcRecipients, string? Importance,
    IReadOnlyList<GraphInternetHeader> InternetMessageHeaders, bool? HasToRecipients = null);

public sealed class GraphMailException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
    public GraphMailException(string message, HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null) : base(message)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }
}
