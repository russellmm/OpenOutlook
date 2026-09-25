using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenOutlook.Providers.Microsoft;

public sealed record GraphInboxMessage(string Id, string Subject, string From, string To,
    DateTimeOffset? Received, int? SizeBytes, bool HasAttachments, bool IsRead, string Preview);

public sealed record GraphInboxPage(string FolderName, int TotalCount, int UnreadCount,
    IReadOnlyList<GraphInboxMessage> Messages, bool HasMore);

public sealed record GraphMessageBody(string ContentType, string Content);

/// <summary>Bounded, read-only Microsoft folder access for a previously verified Graph account.</summary>
public sealed class GraphInboxReader
{
    private const string Origin = "https://graph.microsoft.com/v1.0";
    private const int MaxJsonBytes = 1024 * 1024;
    private readonly HttpClient _http;
    private readonly string _expectedAccountId;

    public GraphInboxReader(HttpClient httpClient, string expectedAccountId)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _expectedAccountId = !string.IsNullOrWhiteSpace(expectedAccountId) && expectedAccountId.Length <= 256
            ? expectedAccountId : throw new ArgumentException("A verified Graph account ID is required.", nameof(expectedAccountId));
    }

    /// <summary>The caller must use this transport, or another with redirects disabled.</summary>
    public static HttpClient CreateSecureHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false });

    public Task<GraphInboxPage> GetInboxAsync(string accessToken, CancellationToken cancellationToken = default) =>
        GetFolderCoreAsync(accessToken, "inbox", null, cancellationToken);

    public Task<GraphInboxPage> GetFolderAsync(string accessToken, string folderId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderId) || folderId.Length > 2048 || folderId.Any(char.IsControl))
            throw new ArgumentException("Invalid folder ID.", nameof(folderId));
        return GetFolderCoreAsync(accessToken, Uri.EscapeDataString(folderId), folderId, cancellationToken);
    }

    private async Task<GraphInboxPage> GetFolderCoreAsync(string accessToken, string pathId,
        string? expectedFolderId, CancellationToken cancellationToken)
    {
        await VerifyAccountAsync(accessToken, cancellationToken).ConfigureAwait(false);
        using var folder = await GetJsonAsync(new Uri(Origin + "/me/mailFolders/" + pathId +
            "?$select=id,displayName,totalItemCount,unreadItemCount"),
            accessToken, cancellationToken).ConfigureAwait(false);
        if (expectedFolderId is not null &&
            !string.Equals(RequiredString(folder.RootElement, "id", 2048), expectedFolderId, StringComparison.Ordinal))
            throw new GraphMailException("Graph returned a different mail folder.");
        var folderName = OptionalString(folder.RootElement, "displayName", 256) ?? "Inbox";
        var total = NonnegativeInt(folder.RootElement, "totalItemCount");
        var unread = NonnegativeInt(folder.RootElement, "unreadItemCount");
        var uri = new Uri(Origin + "/me/mailFolders/" + pathId + "/messages?$top=50&$orderby=receivedDateTime%20desc&" +
            "$select=id,subject,from,toRecipients,receivedDateTime,hasAttachments,isRead,bodyPreview");
        using var page = await GetJsonAsync(uri, accessToken, cancellationToken).ConfigureAwait(false);
        if (!page.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() > 50)
            throw new GraphMailException("Graph returned an invalid inbox page.");
        var messages = new List<GraphInboxMessage>(value.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new GraphMailException("Graph returned an invalid inbox message.");
            var id = RequiredString(item, "id", 2048);
            if (!ids.Add(id)) throw new GraphMailException("Graph returned duplicate inbox messages.");
            var address = Address(item, "from");
            var to = RecipientAddresses(item);
            DateTimeOffset? received = null;
            var date = OptionalString(item, "receivedDateTime", 64);
            if (date is not null)
            {
                if (!DateTimeOffset.TryParse(date, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                    throw new GraphMailException("Graph returned an invalid message date.");
                received = parsed;
            }
            messages.Add(new GraphInboxMessage(id, OptionalString(item, "subject", 4096) ?? "(no subject)",
                address, to, received, null,
                Boolean(item, "hasAttachments"), Boolean(item, "isRead"),
                OptionalString(item, "bodyPreview", 4096) ?? ""));
        }
        var hasMore = page.RootElement.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String;
        return new GraphInboxPage(folderName, total, unread, messages, hasMore);
    }

    public async Task<string?> GetPlainTextBodyAsync(string accessToken, string messageId,
        CancellationToken cancellationToken = default)
    {
        var body = await GetMessageBodyCoreAsync(accessToken, messageId, preferText: true, cancellationToken)
            .ConfigureAwait(false);
        return body is not null && string.Equals(body.ContentType, "text", StringComparison.OrdinalIgnoreCase)
            ? body.Content : null;
    }

    public Task<GraphMessageBody?> GetMessageBodyAsync(string accessToken, string messageId,
        CancellationToken cancellationToken = default) =>
        GetMessageBodyCoreAsync(accessToken, messageId, preferText: false, cancellationToken);

    private async Task<GraphMessageBody?> GetMessageBodyCoreAsync(string accessToken, string messageId,
        bool preferText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 2048 || messageId.Any(char.IsControl))
            throw new ArgumentException("Invalid message ID.", nameof(messageId));
        await VerifyAccountAsync(accessToken, cancellationToken).ConfigureAwait(false);
        var uri = new Uri(Origin + "/me/messages/" + Uri.EscapeDataString(messageId) + "?$select=id,body");
        using var json = await GetJsonAsync(uri, accessToken, cancellationToken, preferText).ConfigureAwait(false);
        if (!string.Equals(RequiredString(json.RootElement, "id", 2048), messageId, StringComparison.Ordinal))
            throw new GraphMailException("Graph returned a different message.");
        if (!json.RootElement.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            return null;
        var contentType = OptionalString(body, "contentType", 16);
        if (!string.Equals(contentType, "html", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(contentType, "text", StringComparison.OrdinalIgnoreCase))
            return null;
        var content = OptionalString(body, "content", 512 * 1024);
        return content is null ? null : new GraphMessageBody(contentType!, content);
    }

    private async Task VerifyAccountAsync(string token, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(new Uri(Origin + "/me?$select=id"), token, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(RequiredString(json.RootElement, "id", 256), _expectedAccountId, StringComparison.Ordinal))
            throw new GraphMailException("Graph token belongs to a different account.");
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, string token, CancellationToken cancellationToken,
        bool preferText = false)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new GraphMailException("A valid access token is required.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (preferText) request.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\"");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != uri || (int)response.StatusCode is >= 300 and < 400)
            throw new GraphMailException("Graph redirected an inbox request.");
        if (!response.IsSuccessStatusCode)
            throw new GraphMailException($"Graph inbox read failed with HTTP {(int)response.StatusCode}.", response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxJsonBytes)
            throw new GraphMailException("Graph inbox response exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxJsonBytes)
                throw new GraphMailException("Graph inbox response exceeds the size limit.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { throw new GraphMailException("Graph returned invalid inbox JSON."); }
    }

    private static string RequiredString(JsonElement root, string name, int maximum) =>
        OptionalString(root, name, maximum) ?? throw new GraphMailException("Graph omitted required inbox data.");

    private static string? OptionalString(JsonElement root, string name, int maximum)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var field) ||
            field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.String)
            throw new GraphMailException("Graph returned invalid inbox data.");
        var text = field.GetString();
        if (text is null || text.Length > maximum || text.Any(char.IsControl) && name != "content" && name != "bodyPreview")
            throw new GraphMailException("Graph returned invalid inbox data.");
        return text;
    }

    private static int NonnegativeInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Number ||
            !field.TryGetInt32(out var value) || value < 0)
            throw new GraphMailException("Graph returned invalid inbox count or size.");
        return value;
    }

    private static bool Boolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var field) || field.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new GraphMailException("Graph returned invalid inbox flag.");
        return field.GetBoolean();
    }

    private static string Address(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var wrapper) || wrapper.ValueKind != JsonValueKind.Object ||
            !wrapper.TryGetProperty("emailAddress", out var email) || email.ValueKind != JsonValueKind.Object)
            return "";
        return OptionalString(email, "name", 320) ?? OptionalString(email, "address", 320) ?? "";
    }

    private static string RecipientAddresses(JsonElement root)
    {
        if (!root.TryGetProperty("toRecipients", out var recipients) || recipients.ValueKind != JsonValueKind.Array)
            return "";
        if (recipients.GetArrayLength() > 100) throw new GraphMailException("Graph returned too many recipients.");
        return string.Join(", ", recipients.EnumerateArray().Select(recipient =>
        {
            if (recipient.ValueKind != JsonValueKind.Object ||
                !recipient.TryGetProperty("emailAddress", out var email) || email.ValueKind != JsonValueKind.Object)
                return "";
            return OptionalString(email, "address", 320) ?? "";
        }).Where(address => address.Length > 0));
    }
}
