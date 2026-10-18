using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenOutlook.Providers.Microsoft;

/// <summary>What the light new-mail check learns about one folder: its counts and its newest message (null when the folder is empty).</summary>
public sealed record GraphFolderProbe(string FolderId, int TotalCount, int UnreadCount, string? NewestId, DateTimeOffset? NewestReceived)
{
    /// <summary>Equal fingerprints mean nothing arrived, left or changed read state in the folder since the last check.</summary>
    public string Fingerprint => $"{TotalCount}/{UnreadCount}/{NewestId}/{NewestReceived?.UtcTicks}";
}

/// <summary>
/// Outlook's scheduled send/receive in its cheapest form: two small Graph requests per folder (the folder's counts, its newest message id) instead of a
/// mailbox sync. Same safety rules as <see cref="GraphMailFolderReader"/> (verified account, no redirects, bounded responses); the account is verified
/// once per access token, so a check every minute costs two requests per folder.
/// </summary>
public sealed class GraphNewMailProbe(HttpClient httpClient, string expectedAccountId)
{
    private const string Origin = "https://graph.microsoft.com";
    private const int MaxResponseBytes = 64 * 1024;
    private string? _verifiedToken;

    /// <summary>Probes the given folders ("inbox" or a folder id) in order; at most 8.</summary>
    public async Task<IReadOnlyList<GraphFolderProbe>> ProbeAsync(string accessToken, IReadOnlyList<string> folderIds, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedAccountId) || expectedAccountId.Length > 256 ||
            string.IsNullOrWhiteSpace(accessToken) || accessToken.Any(char.IsControl))
            throw new GraphMailException("A verified account and access token are required.");
        if (folderIds.Count is 0 or > 8 || folderIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 2048 || id.Any(char.IsControl)))
            throw new GraphMailException("Between one and eight valid folder ids are required.");
        if (!string.Equals(_verifiedToken, accessToken, StringComparison.Ordinal))
        {
            using var me = await GetJsonAsync(new Uri(Origin + "/v1.0/me?$select=id"), accessToken, cancellationToken).ConfigureAwait(false);
            if (!me.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                !string.Equals(id.GetString(), expectedAccountId, StringComparison.Ordinal))
                throw new GraphMailException("Graph token belongs to a different account.");
            _verifiedToken = accessToken;
        }
        var result = new List<GraphFolderProbe>(folderIds.Count);
        foreach (var folderId in folderIds)
        {
            var folderPath = "/v1.0/me/mailFolders/" + Uri.EscapeDataString(folderId);
            int total, unread;
            using (var folder = await GetJsonAsync(new Uri(Origin + folderPath + "?$select=id,totalItemCount,unreadItemCount"), accessToken, cancellationToken).ConfigureAwait(false))
            {
                total = NonnegativeInt(folder.RootElement, "totalItemCount");
                unread = NonnegativeInt(folder.RootElement, "unreadItemCount");
            }
            string? newestId = null;
            DateTimeOffset? newestReceived = null;
            using (var newest = await GetJsonAsync(new Uri(Origin + folderPath + "/messages?$top=1&$select=id,receivedDateTime&$orderby=receivedDateTime%20desc"),
                       accessToken, cancellationToken).ConfigureAwait(false))
            {
                if (!newest.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 1)
                    throw new GraphMailException("Graph returned an invalid message page.");
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var mid) || mid.ValueKind != JsonValueKind.String ||
                        mid.GetString() is not { Length: > 0 and <= 2048 } text || text.Any(char.IsControl))
                        throw new GraphMailException("Graph returned an invalid message id.");
                    newestId = text;
                    if (item.TryGetProperty("receivedDateTime", out var received) && received.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(received.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                        newestReceived = at;
                }
            }
            result.Add(new GraphFolderProbe(folderId, total, unread, newestId, newestReceived));
        }
        return result;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != uri || (int)response.StatusCode is >= 300 and < 400)
            throw new GraphMailException("Graph redirected a new-mail check.");
        if (!response.IsSuccessStatusCode)
            throw new GraphMailException($"Graph new-mail check failed with HTTP {(int)response.StatusCode}.", response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new GraphMailException("Graph new-mail response exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxResponseBytes) throw new GraphMailException("Graph new-mail response exceeds the size limit.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { throw new GraphMailException("Graph returned invalid JSON."); }
    }

    private static int NonnegativeInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0)
            throw new GraphMailException("Graph returned an invalid folder count.");
        return count;
    }
}
