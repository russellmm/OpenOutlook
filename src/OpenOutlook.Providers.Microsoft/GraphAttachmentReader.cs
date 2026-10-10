using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenOutlook.Providers.Microsoft;

public enum GraphAttachmentKind { File, Item, Reference, Unknown }

public sealed record GraphAttachment(string Id, string Name, int SizeBytes, bool IsInline,
    GraphAttachmentKind Kind, string? ContentId = null);

/// <summary>Lists attachment metadata and streams bounded file content from a verified mailbox.</summary>
public sealed class GraphAttachmentReader(HttpClient httpClient, string expectedAccountId)
{
    public const long MaximumFileBytes = 64L * 1024 * 1024;
    private const int MaximumListBytes = 1024 * 1024;
    private const int MaximumAttachments = 100;
    private const string Origin = "https://graph.microsoft.com/v1.0";

    /// <summary>Use this transport, or another that disables redirects for bearer-token requests.</summary>
    public static HttpClient CreateSecureHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false });

    public async Task<IReadOnlyList<GraphAttachment>> ListAsync(string accessToken, string messageId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(messageId, nameof(messageId));
        await VerifyAccountAsync(accessToken, cancellationToken).ConfigureAwait(false);
        var path = "/me/messages/" + Uri.EscapeDataString(messageId) + "/attachments";
        // contentId is defined on fileAttachment, not the base attachment type. Select it through the
        // derived type so cid: images can be matched without returning every file's contentBytes.
        Uri? next = new(Origin + path + "?$top=50&$select=id,name,size,isInline,microsoft.graph.fileAttachment/contentId");
        var pages = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var attachments = new List<GraphAttachment>();
        while (next is not null)
        {
            if (!pages.Add(next.AbsoluteUri) || pages.Count > 4)
                throw new GraphMailException("Attachment listing exceeds the preview limit.");
            using var json = await GetJsonAsync(next, accessToken, cancellationToken).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("value", out var array) || array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() > 50)
                throw new GraphMailException("Graph returned an invalid attachment page.");
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new GraphMailException("Graph returned an invalid attachment.");
                var id = RequiredString(item, "id", 2048);
                var name = RequiredString(item, "name", 255);
                if (!ids.Add(id) || attachments.Count >= MaximumAttachments)
                    throw new GraphMailException("Graph returned duplicate or too many attachments.");
                var size = NonnegativeInt(item, "size");
                var type = OptionalString(item, "@odata.type", 64);
                var kind = type switch
                {
                    "#microsoft.graph.fileAttachment" => GraphAttachmentKind.File,
                    "#microsoft.graph.itemAttachment" => GraphAttachmentKind.Item,
                    "#microsoft.graph.referenceAttachment" => GraphAttachmentKind.Reference,
                    _ => GraphAttachmentKind.Unknown
                };
                var hasInline = item.TryGetProperty("isInline", out var inlineValue);
                var inline = hasInline && inlineValue.ValueKind == JsonValueKind.True;
                if (hasInline &&
                    inlineValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                    throw new GraphMailException("Graph returned an invalid attachment flag.");
                attachments.Add(new GraphAttachment(id, name, size, inline, kind,
                    OptionalString(item, "contentId", 255)));
            }
            next = null;
            if (json.RootElement.TryGetProperty("@odata.nextLink", out var link))
            {
                if (link.ValueKind != JsonValueKind.String ||
                    !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var candidate) ||
                    candidate.Scheme != Uri.UriSchemeHttps || candidate.Host != "graph.microsoft.com" ||
                    !candidate.IsDefaultPort || candidate.UserInfo.Length != 0 || candidate.Fragment.Length != 0 ||
                    candidate.AbsolutePath != new Uri(Origin + path).AbsolutePath || candidate.AbsoluteUri.Length > 4096)
                    throw new GraphMailException("Graph returned an unsafe attachment page link.");
                next = candidate;
            }
        }
        return attachments;
    }

    public async Task<long> CopyFileAsync(string accessToken, string messageId, GraphAttachment attachment,
        Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateId(messageId, nameof(messageId));
        ValidateId(attachment.Id, nameof(attachment));
        if (attachment.Kind != GraphAttachmentKind.File)
            throw new NotSupportedException("Only file attachments can be saved.");
        if (attachment.SizeBytes < 0 || attachment.SizeBytes > MaximumFileBytes)
            throw new InvalidDataException("Attachment exceeds the size limit.");
        if (!destination.CanWrite) throw new ArgumentException("Destination must be writable.", nameof(destination));
        await VerifyAccountAsync(accessToken, cancellationToken).ConfigureAwait(false);
        var uri = new Uri(Origin + "/me/messages/" + Uri.EscapeDataString(messageId) +
            "/attachments/" + Uri.EscapeDataString(attachment.Id) + "/$value");
        using var response = await SendGetAsync(uri, accessToken, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaximumFileBytes)
            throw new GraphMailException("Attachment exceeds the size limit.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (total + count > MaximumFileBytes)
                throw new GraphMailException("Attachment exceeds the size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            total += count;
        }
        return total;
    }

    private async Task VerifyAccountAsync(string token, CancellationToken cancellationToken)
    {
        if (GraphAccountVerification.IsVerified(expectedAccountId, token)) return;
        if (string.IsNullOrWhiteSpace(expectedAccountId) || expectedAccountId.Length > 256)
            throw new GraphMailException("A verified account ID is required.");
        using var json = await GetJsonAsync(new Uri(Origin + "/me?$select=id"), token,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(RequiredString(json.RootElement, "id", 256), expectedAccountId,
            StringComparison.Ordinal))
            throw new GraphMailException("Graph token belongs to a different account.");
        GraphAccountVerification.Mark(expectedAccountId, token);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, string token, CancellationToken cancellationToken)
    {
        using var response = await SendGetAsync(uri, token, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaximumListBytes)
            throw new GraphMailException("Attachment metadata exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaximumListBytes)
                throw new GraphMailException("Attachment metadata exceeds the size limit.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { throw new GraphMailException("Graph returned invalid attachment JSON."); }
    }

    private async Task<HttpResponseMessage> SendGetAsync(Uri uri, string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new GraphMailException("A valid access token is required.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != uri || (int)response.StatusCode is >= 300 and < 400)
        { response.Dispose(); throw new GraphMailException("Graph redirected an attachment request."); }
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new GraphMailException($"Graph attachment request failed with HTTP {(int)status}.", status);
        }
        return response;
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl))
            throw new ArgumentException("Invalid message or attachment ID.", name);
    }

    private static string RequiredString(JsonElement root, string name, int limit) =>
        OptionalString(root, name, limit) is { Length: > 0 } value ? value :
            throw new GraphMailException("Graph omitted required attachment metadata.");

    private static string? OptionalString(JsonElement root, string name, int limit)
    {
        if (!root.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } value ||
            value.Length > limit || value.Any(char.IsControl))
            throw new GraphMailException("Graph returned invalid attachment metadata.");
        return value;
    }

    private static int NonnegativeInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Number ||
            !field.TryGetInt32(out var value) || value < 0)
            throw new GraphMailException("Graph returned an invalid attachment size.");
        return value;
    }

}
