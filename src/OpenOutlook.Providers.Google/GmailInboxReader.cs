using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenOutlook.Providers.Google;

/// <summary>Read-only Gmail access. The caller provides a gmail.readonly-scoped bearer token; this class does not obtain tokens.</summary>
/// <remarks>Supply a client whose handler has AllowAutoRedirect=false (see CreateNoRedirectHttpClient).
/// An arbitrary injected HttpClient with automatic redirects cannot be made redirect-safe by this class.</remarks>
public sealed class GmailInboxReader
{
    private const string Root = "https://gmail.googleapis.com/gmail/v1/users/me";
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, ValueTask<string>> _accessToken;
    private readonly string _expectedEmail;

    public GmailInboxReader(HttpClient httpClient, Func<CancellationToken, ValueTask<string>> accessToken, string expectedEmail)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        _expectedEmail = !string.IsNullOrWhiteSpace(expectedEmail) && expectedEmail == expectedEmail.Trim()
            ? expectedEmail : throw new ArgumentException("An expected account email is required.", nameof(expectedEmail));
    }

    /// <summary>Create a client that will not forward requests to redirects. Dispose it when finished.</summary>
    public static HttpClient CreateNoRedirectHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false });

    /// <summary>List up to maxMessages inbox IDs, requesting no more than pageSize IDs per page.</summary>
    public async Task<IReadOnlyList<string>> ListInboxMessageIdsAsync(int pageSize = 50, int maxMessages = 500,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (maxMessages is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxMessages));
        var tokenForOperation = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        await VerifyAccountAsync(tokenForOperation, cancellationToken).ConfigureAwait(false);
        var result = new List<string>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        var pages = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++pages > maxMessages) throw new GmailReadException("Gmail pagination exceeds the scan limit.");
            var query = $"?labelIds=INBOX&maxResults={Math.Min(pageSize, maxMessages - result.Count)}";
            if (token is not null) query += "&pageToken=" + Uri.EscapeDataString(token);
            using var json = await GetJsonAsync("/messages" + query, tokenForOperation, cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new GmailReadException("Gmail returned an invalid message page.");
            if (root.TryGetProperty("messages", out var messages))
            {
                if (messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() > pageSize)
                    throw new GmailReadException("Gmail returned an invalid message page.");
                foreach (var item in messages.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) throw new GmailReadException("Gmail returned an invalid message ID.");
                    var id = RequiredString(item, "id");
                    if (!ValidId(id)) throw new GmailReadException("Gmail returned an invalid message ID.");
                    if (result.Count < maxMessages) result.Add(id);
                }
            }
            if (result.Count == maxMessages || !root.TryGetProperty("nextPageToken", out var next)) break;
            token = next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (!ValidPageToken(token) || !seenTokens.Add(token!))
                throw new GmailReadException("Gmail returned an invalid pagination token.");
        } while (true);
        return result;
    }

    /// <summary>Fetch metadata or full content for one ID, always after verifying the account anew.</summary>
    public async Task<GmailMessage> GetMessageAsync(string messageId, GmailMessageFormat format = GmailMessageFormat.Metadata,
        CancellationToken cancellationToken = default)
    {
        if (!ValidId(messageId)) throw new ArgumentException("An invalid Gmail message ID was supplied.", nameof(messageId));
        if (format is not (GmailMessageFormat.Metadata or GmailMessageFormat.Full)) throw new ArgumentOutOfRangeException(nameof(format));
        var tokenForOperation = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        await VerifyAccountAsync(tokenForOperation, cancellationToken).ConfigureAwait(false);
        var query = format == GmailMessageFormat.Metadata
            ? "?format=metadata&metadataHeaders=From&metadataHeaders=To&metadataHeaders=Subject&metadataHeaders=Date"
            : "?format=full";
        using var json = await GetJsonAsync("/messages/" + messageId + query, tokenForOperation, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || RequiredString(root, "id") != messageId)
            throw new GmailReadException("Gmail returned an invalid message.");
        var headers = new List<GmailHeader>();
        var bodies = new List<string>();
        if (root.TryGetProperty("payload", out var payload))
        {
            if (payload.ValueKind != JsonValueKind.Object) throw new GmailReadException("Gmail returned an invalid message payload.");
            if (payload.TryGetProperty("headers", out var headerArray))
            {
                if (headerArray.ValueKind != JsonValueKind.Array || headerArray.GetArrayLength() > 256)
                    throw new GmailReadException("Gmail returned invalid message headers.");
                foreach (var header in headerArray.EnumerateArray())
                {
                    if (header.ValueKind != JsonValueKind.Object) throw new GmailReadException("Gmail returned invalid message headers.");
                    headers.Add(new GmailHeader(RequiredString(header, "name"), RequiredString(header, "value")));
                }
            }
            if (format == GmailMessageFormat.Full) ReadPlainTextParts(payload, bodies, 0, new PartCounter());
        }
        return new GmailMessage(messageId, OptionalString(root, "threadId"), OptionalString(root, "snippet"), headers, bodies);
    }

    private async Task VerifyAccountAsync(string token, CancellationToken ct)
    {
        using var json = await GetJsonAsync("/profile", token, ct).ConfigureAwait(false);
        var actual = RequiredString(json.RootElement, "emailAddress");
        if (!string.Equals(actual, _expectedEmail, StringComparison.OrdinalIgnoreCase))
            throw new GmailReadException("Gmail token belongs to a different account.");
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var token = await _accessToken(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new GmailReadException("A valid access token is required.");
        return token;
    }

    private async Task<JsonDocument> GetJsonAsync(string relativePath, string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Get, Root + relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // Defense in depth for custom clients. The injected handler MUST disable automatic redirects to prevent a leaked request.
        if (response.RequestMessage?.RequestUri != request.RequestUri)
            throw new GmailReadException("Gmail redirected the request.");
        if (!response.IsSuccessStatusCode)
            throw new GmailReadException($"Gmail read failed with HTTP {(int)response.StatusCode}.", response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new GmailReadException("Gmail response exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaxResponseBytes) throw new GmailReadException("Gmail response exceeds the size limit.");
            buffer.Write(bytes, 0, count);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: ct).ConfigureAwait(false); }
        catch (JsonException) { throw new GmailReadException("Gmail returned invalid JSON."); }
    }

    private static bool ValidId(string? id) => id is { Length: >= 1 and <= 256 } &&
        id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');

    private static bool ValidPageToken(string? token) => token is { Length: >= 1 and <= 1024 } &&
        token.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '+' or '/' or '=');

    private static string RequiredString(JsonElement obj, string key) =>
        OptionalString(obj, key) is { Length: > 0 } value ? value : throw new GmailReadException($"Gmail omitted required {key}.");

    private static string? OptionalString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static void ReadPlainTextParts(JsonElement part, List<string> bodies, int depth, PartCounter counter)
    {
        if (depth > 8 || ++counter.Count > 64) throw new GmailReadException("Gmail message has too many MIME parts.");
        if (OptionalString(part, "mimeType") == "text/plain" && part.TryGetProperty("body", out var body))
        {
            if (body.ValueKind != JsonValueKind.Object) throw new GmailReadException("Gmail returned an invalid message body.");
            if (body.TryGetProperty("data", out var data))
            {
                if (data.ValueKind != JsonValueKind.String) throw new GmailReadException("Gmail returned an invalid message body.");
                var encoded = data.GetString()!;
                if (encoded.Length > MaxBodyBytes * 2) throw new GmailReadException("Gmail message body exceeds the size limit.");
                try
                {
                    var bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/').PadRight((encoded.Length + 3) / 4 * 4, '='));
                    if (bytes.Length > MaxBodyBytes) throw new GmailReadException("Gmail message body exceeds the size limit.");
                    bodies.Add(new UTF8Encoding(false, true).GetString(bytes));
                }
                catch (FormatException) { throw new GmailReadException("Gmail returned an invalid message body."); }
                catch (DecoderFallbackException) { throw new GmailReadException("Gmail returned an invalid message body."); }
            }
        }
        if (!part.TryGetProperty("parts", out var children)) return;
        if (children.ValueKind != JsonValueKind.Array || children.GetArrayLength() > 64)
            throw new GmailReadException("Gmail returned invalid MIME parts.");
        foreach (var child in children.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.Object) throw new GmailReadException("Gmail returned invalid MIME parts.");
            ReadPlainTextParts(child, bodies, depth + 1, counter);
        }
    }

    private sealed class PartCounter { public int Count; }
}

public enum GmailMessageFormat { Metadata, Full }
public sealed record GmailHeader(string Name, string Value);
public sealed record GmailMessage(string Id, string? ThreadId, string? Snippet,
    IReadOnlyList<GmailHeader> Headers, IReadOnlyList<string> PlainTextBodies);
public sealed class GmailReadException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
