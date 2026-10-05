using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenOutlook.Providers.Microsoft;

/// <summary>What the mirror needs to know about one server message to decide whether to download it, update it or remove it.</summary>
public sealed record GraphSyncMessage(string Id, string? ChangeKey, DateTimeOffset? Received, bool IsRead, bool Flagged, bool IsDraft, bool HasAttachments);

/// <summary>
/// Read-only listing for the local mailbox copy: every message of a folder (id, change key, read and flag state, date) newest first within a date window, and
/// the ids of the well-known folders. Same safety rules as the other Graph readers: bearer token only to graph.microsoft.com, no redirects, bounded
/// responses, page links pinned to the same path.
/// </summary>
public sealed class GraphMailboxSyncReader(HttpClient httpClient, string expectedAccountId)
{
    private const string Origin = "https://graph.microsoft.com";
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int PageSize = 100;
    private const int MaxPages = 2000;
    public static readonly string[] WellKnownNames = ["inbox", "sentitems", "deleteditems", "drafts", "junkemail", "archive", "outbox"];

    /// <summary>The server ids of the well-known folders that exist in this mailbox, keyed by Graph's well-known name.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetWellKnownFolderIdsAsync(string token, CancellationToken ct = default)
    {
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in WellKnownNames)
        {
            try
            {
                using var json = await GetJsonAsync(new Uri(Origin + "/v1.0/me/mailFolders/" + name + "?$select=id"), token, ct).ConfigureAwait(false);
                if (json.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= 2048 } value)
                    result[name] = value;
            }
            catch (GraphMailException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { /* this mailbox has no such folder */ }
        }
        return result;
    }

    /// <summary>All messages of a folder received on or after <paramref name="since"/> (null = all), newest first.</summary>
    public async Task<IReadOnlyList<GraphSyncMessage>> ListMessagesAsync(string token, string folderId, DateTimeOffset? since, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderId) || folderId.Length > 2048 || folderId.Any(char.IsControl)) throw new ArgumentException("Invalid folder ID.", nameof(folderId));
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var path = "/v1.0/me/mailFolders/" + Uri.EscapeDataString(folderId) + "/messages";
        var query = "?$top=" + PageSize + "&$orderby=receivedDateTime%20desc&$select=id,changeKey,receivedDateTime,isRead,flag,isDraft,hasAttachments";
        if (since is { } s) query += "&$filter=" + Uri.EscapeDataString("receivedDateTime ge " + s.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
        Uri? next = new(Origin + path + query);
        var list = new List<GraphSyncMessage>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var pages = 0; next is not null; pages++)
        {
            if (pages >= MaxPages) throw new GraphMailException("Graph message paging exceeds the limit.");
            using var json = await GetJsonAsync(next, token, ct).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array) throw new GraphMailException("Graph returned an invalid message page.");
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String || idEl.GetString() is not { Length: > 0 and <= 2048 } id)
                    throw new GraphMailException("Graph returned an invalid message.");
                if (!seenIds.Add(id)) continue;
                DateTimeOffset? received = item.TryGetProperty("receivedDateTime", out var r) && r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var rd) ? rd : null;
                var flagged = item.TryGetProperty("flag", out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty("flagStatus", out var fs) && fs.GetString() == "flagged";
                list.Add(new GraphSyncMessage(id, item.TryGetProperty("changeKey", out var ck) && ck.ValueKind == JsonValueKind.String ? ck.GetString() : null, received,
                    Bool(item, "isRead"), flagged, Bool(item, "isDraft"), Bool(item, "hasAttachments")));
            }
            next = null;
            if (json.RootElement.TryGetProperty("@odata.nextLink", out var link))
            {
                if (link.ValueKind != JsonValueKind.String || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var candidate) || candidate.Scheme != Uri.UriSchemeHttps ||
                    candidate.Host != "graph.microsoft.com" || !candidate.IsDefaultPort || candidate.UserInfo.Length != 0 || candidate.Fragment.Length != 0 ||
                    !(candidate.AbsolutePath.StartsWith("/v1.0/me/mailFolders", StringComparison.Ordinal) && candidate.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)) ||
                    candidate.AbsoluteUri.Length > 4096)       // Graph writes the folder as mailFolders('id') in page links, so the path is checked by shape, not equality
                    throw new GraphMailException("Graph returned an unsafe message page link.");
                next = candidate;
            }
        }
        return list;
    }

    private static bool Bool(JsonElement item, string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private async Task VerifyAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedAccountId) || expectedAccountId.Length > 256 || string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new GraphMailException("A verified account and access token are required.");
        using var me = await GetJsonAsync(new Uri(Origin + "/v1.0/me?$select=id"), token, ct).ConfigureAwait(false);
        if (!me.RootElement.TryGetProperty("id", out var id) || id.GetString() != expectedAccountId) throw new GraphMailException("Graph token belongs to a different account.");
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != uri || (int)response.StatusCode is >= 300 and < 400) throw new GraphMailException("Graph redirected a mailbox request.");
        if (!response.IsSuccessStatusCode) throw new GraphMailException($"Graph mailbox read failed with HTTP {(int)response.StatusCode}.", response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new GraphMailException("Graph response exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxResponseBytes) throw new GraphMailException("Graph response exceeds the size limit.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: ct).ConfigureAwait(false); }
        catch (JsonException) { throw new GraphMailException("Graph returned invalid JSON."); }
    }
}
