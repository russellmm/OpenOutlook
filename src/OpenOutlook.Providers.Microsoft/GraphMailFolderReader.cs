using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenOutlook.Providers.Microsoft;

public sealed record GraphMailboxFolder(string Id, string DisplayName, int TotalCount, int UnreadCount,
    IReadOnlyList<GraphMailboxFolder> Children);

/// <summary>Bounded read-only traversal of one verified Microsoft mailbox's visible folders.</summary>
public sealed class GraphMailFolderReader(HttpClient httpClient, string expectedAccountId)
{
    /// <summary>Use this transport, or another that disables redirects for bearer-token requests.</summary>
    public static HttpClient CreateSecureHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false });

    private const string Origin = "https://graph.microsoft.com";
    private const int MaxFolders = 200;
    private const int MaxDepth = 10;
    private const int MaxResponseBytes = 1024 * 1024;
    private const int MaxRequests = 250;
    private int _requests;
    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<GraphMailboxFolder>> GetFoldersAsync(string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedAccountId) || expectedAccountId.Length > 256 ||
            string.IsNullOrWhiteSpace(accessToken) || accessToken.Any(char.IsControl))
            throw new GraphMailException("A verified account and access token are required.");
        _requests = 0;
        _seenIds.Clear();
        using var me = await GetJsonAsync(new Uri(Origin + "/v1.0/me?$select=id"), accessToken,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(RequiredString(me.RootElement, "id", 256), expectedAccountId, StringComparison.Ordinal))
            throw new GraphMailException("Graph token belongs to a different account.");
        return await ReadLevelAsync(null, 0, accessToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<GraphMailboxFolder>> ReadLevelAsync(string? parentId, int depth,
        string token, CancellationToken cancellationToken)
    {
        if (depth > MaxDepth) throw new GraphMailException("Mailbox folder nesting exceeds the preview limit.");
        var path = parentId is null ? "/v1.0/me/mailFolders" :
            "/v1.0/me/mailFolders/" + Uri.EscapeDataString(parentId) + "/childFolders";
        Uri? next = new(Origin + path + "?$top=100&$select=id,displayName,childFolderCount,totalItemCount,unreadItemCount,isHidden");
        var pending = new List<(string Id, string Name, int Total, int Unread, int ChildCount)>();
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        while (next is not null)
        {
            if (!seenPages.Add(next.AbsoluteUri) || ++_requests > MaxRequests)
                throw new GraphMailException("Mailbox folder pagination exceeds the preview limit.");
            using var json = await GetJsonAsync(next, token, cancellationToken).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array ||
                value.GetArrayLength() > 100)
                throw new GraphMailException("Graph returned an invalid folder page.");
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new GraphMailException("Graph returned an invalid mail folder.");
                if (item.TryGetProperty("isHidden", out var hidden))
                {
                    if (hidden.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new GraphMailException("Graph returned an invalid folder visibility flag.");
                    if (hidden.ValueKind == JsonValueKind.True) continue;
                }
                var id = RequiredString(item, "id", 2048);
                var name = RequiredString(item, "displayName", 256);
                if (id.Any(char.IsControl) || name.Any(char.IsControl) || !_seenIds.Add(id))
                    throw new GraphMailException("Graph returned an invalid or duplicate mail folder.");
                var children = NonnegativeInt(item, "childFolderCount");
                var total = NonnegativeInt(item, "totalItemCount");
                var unread = NonnegativeInt(item, "unreadItemCount");
                pending.Add((id, name, total, unread, children));
                if (_seenIds.Count > MaxFolders)
                    throw new GraphMailException("Mailbox has more folders than this preview supports.");
            }
            next = null;
            if (json.RootElement.TryGetProperty("@odata.nextLink", out var link))
            {
                if (link.ValueKind != JsonValueKind.String || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var candidate) ||
                    candidate.Scheme != Uri.UriSchemeHttps || candidate.Host != "graph.microsoft.com" ||
                    !candidate.IsDefaultPort || candidate.UserInfo.Length != 0 || candidate.Fragment.Length != 0 ||
                    candidate.AbsolutePath != path || candidate.AbsoluteUri.Length > 4096)
                    throw new GraphMailException("Graph returned an unsafe folder page link.");
                next = candidate;
            }
        }
        var result = new List<GraphMailboxFolder>(pending.Count);
        foreach (var folder in pending)
        {
            IReadOnlyList<GraphMailboxFolder> children = folder.ChildCount == 0 ? [] :
                await ReadLevelAsync(folder.Id, depth + 1, token, cancellationToken).ConfigureAwait(false);
            result.Add(new GraphMailboxFolder(folder.Id, folder.Name, folder.Total, folder.Unread, children));
        }
        return result;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != uri || (int)response.StatusCode is >= 300 and < 400)
            throw new GraphMailException("Graph redirected a folder request.");
        if (!response.IsSuccessStatusCode)
            throw new GraphMailException($"Graph folder read failed with HTTP {(int)response.StatusCode}.", response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new GraphMailException("Graph folder response exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxResponseBytes)
                throw new GraphMailException("Graph folder response exceeds the size limit.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { throw new GraphMailException("Graph returned invalid folder JSON."); }
    }

    private static string RequiredString(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: > 0 } text || text.Length > maximum)
            throw new GraphMailException("Graph omitted required folder data.");
        return text;
    }

    private static int NonnegativeInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var count) || count < 0)
            throw new GraphMailException("Graph returned an invalid folder count.");
        return count;
    }
}
