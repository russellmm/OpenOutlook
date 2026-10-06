using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenOutlook.Providers.Microsoft;

/// <summary>Writes one verified mailbox through Microsoft Graph. Callers must use a no-redirect transport.</summary>
public sealed class GraphMailWriter(HttpClient http, string expectedAccountId)
{
    private const string Origin = "https://graph.microsoft.com/v1.0";
    private const int MaxResponseBytes = 1024 * 1024;
    public const long MaximumComposeAttachmentBytes = 150_000_000;
    private const int SmallAttachmentLimit = 3 * 1024 * 1024;
    private const int UploadChunkBytes = 3 * 1024 * 1024;

    public sealed record DraftDetails(string Subject, string To, string Body, string ContentType,
        string Cc = "", string Bcc = "", bool HasAttachments = false);
    public sealed record DraftContent(string To, string Cc, string Bcc, string Subject,
        string Body, string ContentType);
    public sealed record DraftAttachment(string Id, string Name, int SizeBytes);

    public async Task<DraftDetails> GetDraftAsync(string token, string draftId, CancellationToken ct = default)
    {
        await VerifyAsync(token, ct).ConfigureAwait(false);
        using var result = await RequestJsonAsync(HttpMethod.Get, MessageUri(draftId) +
            "?$select=id,isDraft,subject,toRecipients,ccRecipients,bccRecipients,body,hasAttachments", token, null,
            HttpStatusCode.OK, ct).ConfigureAwait(false);
        var root = result.RootElement;
        if (RequiredId(root) != draftId || !root.TryGetProperty("isDraft", out var isDraft) ||
            isDraft.ValueKind != JsonValueKind.True)
            throw new GraphMailException("Graph did not return the requested draft.");
        var subject = String(root, "subject", 4096);
        var body = root.TryGetProperty("body", out var bodyObject) && bodyObject.ValueKind == JsonValueKind.Object
            ? String(bodyObject, "content", 512 * 1024) : "";
        var contentType = bodyObject.ValueKind == JsonValueKind.Object ? String(bodyObject, "contentType", 16) : "text";
        if (!contentType.Equals("text", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Equals("html", StringComparison.OrdinalIgnoreCase))
            throw new GraphMailException("Graph returned an unsupported draft body.");
        var hasAttachments = root.TryGetProperty("hasAttachments", out var attached) &&
            attached.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("hasAttachments", out attached) &&
            attached.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new GraphMailException("Graph returned an invalid draft attachment flag.");
        return new DraftDetails(subject, ReadRecipients(root, "toRecipients"), body, contentType,
            ReadRecipients(root, "ccRecipients"), ReadRecipients(root, "bccRecipients"), hasAttachments);
    }

    public Task SetReadAsync(string token, string messageId, bool isRead, CancellationToken ct = default) =>
        PatchAsync(token, messageId, new { isRead }, ct);

    public Task SetFlagAsync(string token, string messageId, bool flagged, CancellationToken ct = default) =>
        PatchAsync(token, messageId, new { flag = new { flagStatus = flagged ? "flagged" : "notFlagged" } }, ct);

    public async Task<bool> IsInDeletedItemsAsync(string token, string messageId, CancellationToken ct = default)
    {
        await VerifyAsync(token, ct).ConfigureAwait(false);
        using var message = await RequestJsonAsync(HttpMethod.Get, MessageUri(messageId) +
            "?$select=id,parentFolderId", token, null, HttpStatusCode.OK, ct).ConfigureAwait(false);
        if (RequiredId(message.RootElement) != messageId)
            throw new GraphMailException("Graph returned a different message.");
        var parent = String(message.RootElement, "parentFolderId", 2048);
        if (parent.Length == 0) throw new GraphMailException("Graph omitted the message folder.");
        using var deleted = await RequestJsonAsync(HttpMethod.Get, Origin +
            "/me/mailFolders/deleteditems?$select=id", token, null, HttpStatusCode.OK, ct).ConfigureAwait(false);
        return string.Equals(parent, RequiredId(deleted.RootElement), StringComparison.Ordinal);
    }

    /// <summary>Moves a message to a folder (an id or a well-known name such as "deleteditems"). The moved message has a new id, which is returned.</summary>
    public async Task<string> MoveAsync(string token, string messageId, string destinationId, CancellationToken ct = default)
    {
        ValidateId(destinationId);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        using var result = await RequestJsonAsync(HttpMethod.Post, MessageUri(messageId) + "/move", token,
            new { destinationId }, HttpStatusCode.Created, ct).ConfigureAwait(false);
        return RequiredId(result.RootElement);
    }

    public async Task CopyAsync(string token, string messageId, string destinationId, CancellationToken ct = default)
    {
        ValidateId(destinationId);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        using var result = await RequestJsonAsync(HttpMethod.Post, MessageUri(messageId) + "/copy", token,
            new { destinationId }, HttpStatusCode.Created, ct).ConfigureAwait(false);
        RequiredId(result.RootElement);
    }

    /// <summary>Creates a mail folder at the top of the mailbox (parentFolderId null) or under another folder; returns its id and name.</summary>
    public async Task<(string Id, string Name)> CreateFolderAsync(string token, string? parentFolderId, string name, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 255 || name.Any(char.IsControl)) throw new ArgumentException("Invalid folder name.", nameof(name));
        if (parentFolderId is not null) ValidateId(parentFolderId);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var path = Origin + "/me/mailFolders" +
                   (parentFolderId is null ? "" : "/" + Uri.EscapeDataString(parentFolderId) + "/childFolders");
        using var result = await RequestJsonAsync(HttpMethod.Post, path, token, new { displayName = name }, HttpStatusCode.Created, ct).ConfigureAwait(false);
        var id = RequiredId(result.RootElement);
        var shown = String(result.RootElement, "displayName", 255);
        return (id, shown.Length > 0 ? shown : name);
    }

    public async Task DeletePermanentlyAsync(string token, string messageId, CancellationToken ct = default)
    {
        if (!await IsInDeletedItemsAsync(token, messageId, ct).ConfigureAwait(false))
            throw new GraphMailException("Permanent deletion is allowed only in Deleted Items.");
        ValidateId(messageId);
        await RequestEmptyAsync(HttpMethod.Post, Origin + "/users/" + Uri.EscapeDataString(expectedAccountId) +
            "/messages/" + Uri.EscapeDataString(messageId) + "/permanentDelete", token, null,
            HttpStatusCode.NoContent, ct).ConfigureAwait(false);
    }

    public async Task<string> CreateDraftAsync(string token, string? to, string subject, string body,
        CancellationToken ct = default) => await CreateDraftAsync(token,
            new DraftContent(to ?? "", "", "", subject, body, "Text"), ct).ConfigureAwait(false);

    public async Task<string> CreateDraftAsync(string token, DraftContent content,
        CancellationToken ct = default)
    {
        ValidateDraftContent(content);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        using var result = await RequestJsonAsync(HttpMethod.Post, Origin + "/me/messages", token,
            DraftPayload(content),
            HttpStatusCode.Created, ct).ConfigureAwait(false);
        return RequiredId(result.RootElement);
    }

    public async Task<string> CreateResponseDraftAsync(string token, string sourceId, string kind,
        CancellationToken ct = default)
    {
        if (kind is not ("reply" or "replyAll" or "forward")) throw new ArgumentException("Unknown response type.", nameof(kind));
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var action = kind switch { "reply" => "createReply", "replyAll" => "createReplyAll", _ => "createForward" };
        using var result = await RequestJsonAsync(HttpMethod.Post, MessageUri(sourceId) + "/" + action,
            token, null, HttpStatusCode.Created, ct).ConfigureAwait(false);
        return RequiredId(result.RootElement);
    }

    public Task UpdateDraftAsync(string token, string draftId, string? to, string subject, string body,
        string contentType = "Text", CancellationToken ct = default) => UpdateDraftAsync(token, draftId,
            new DraftContent(to ?? "", "", "", subject, body, contentType), ct);

    public Task UpdateDraftAsync(string token, string draftId, DraftContent content,
        CancellationToken ct = default)
    {
        ValidateDraftContent(content);
        return PatchAsync(token, draftId, DraftPayload(content), ct);
    }

    private static void ValidateDraftContent(DraftContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateText(content.Subject, 4096);
        ValidateText(content.Body, 512 * 1024);
        if (content.ContentType is not ("Text" or "HTML"))
            throw new ArgumentException("Invalid mail body type.");
    }

    private static object DraftPayload(DraftContent content) => new
    {
        subject = content.Subject,
        body = new { contentType = content.ContentType, content = content.Body },
        toRecipients = ParseRecipients(content.To),
        ccRecipients = ParseRecipients(content.Cc),
        bccRecipients = ParseRecipients(content.Bcc)
    };

    public async Task SendDraftAsync(string token, string draftId, CancellationToken ct = default)
    {
        await VerifyAsync(token, ct).ConfigureAwait(false);
        await RequestEmptyAsync(HttpMethod.Post, MessageUri(draftId) + "/send", token, null,
            HttpStatusCode.Accepted, ct).ConfigureAwait(false);
    }

    public async Task AddFileAttachmentAsync(string token, string draftId, string path,
        CancellationToken ct = default)
    {
        ValidateId(draftId);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a file to attach.", nameof(path));
        var name = Path.GetFileName(path);
        if (name.Length is 0 or > 255 || name is "." or ".." || name.Any(char.IsControl))
            throw new ArgumentException("The attachment filename is not supported.", nameof(path));
        await using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        var size = file.Length;
        if (size > MaximumComposeAttachmentBytes)
            throw new ArgumentException("Attachments must be 150 MB or smaller.", nameof(path));
        if (size < SmallAttachmentLimit)
        {
            var bytes = new byte[checked((int)size)];
            await file.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            await VerifyAsync(token, ct).ConfigureAwait(false);
            await RequestEmptyAsync(HttpMethod.Post, MessageUri(draftId) + "/attachments",
                token, new Dictionary<string, object>
                {
                    ["@odata.type"] = "#microsoft.graph.fileAttachment",
                    ["name"] = name,
                    ["contentBytes"] = Convert.ToBase64String(bytes)
                }, HttpStatusCode.Created, ct).ConfigureAwait(false);
            return;
        }

        await VerifyAsync(token, ct).ConfigureAwait(false);
        using var session = await RequestJsonAsync(HttpMethod.Post,
            MessageUri(draftId) + "/attachments/createUploadSession", token,
            new { AttachmentItem = new { attachmentType = "file", name, size } },
            HttpStatusCode.Created, ct).ConfigureAwait(false);
        var uploadUrl = String(session.RootElement, "uploadUrl", 4096);
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uploadUri) ||
            uploadUri.Scheme != Uri.UriSchemeHttps || uploadUri.Host != "outlook.office.com" ||
            !uploadUri.IsDefaultPort || uploadUri.UserInfo.Length != 0 || uploadUri.Fragment.Length != 0)
            throw new GraphMailException("Graph returned an unsafe attachment upload address.");
        var buffer = new byte[UploadChunkBytes];
        long offset = 0;
        while (offset < size)
        {
            var length = checked((int)Math.Min(buffer.Length, size - offset));
            await file.ReadExactlyAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUri);
            request.Content = new ByteArrayContent(buffer, 0, length);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.TryAddWithoutValidation("Content-Range",
                $"bytes {offset}-{offset + length - 1}/{size}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != uploadUri ||
                (int)response.StatusCode is >= 300 and < 400)
                throw new GraphMailException("Attachment upload was redirected.");
            offset += length;
            if (offset == size)
            {
                if (response.StatusCode != HttpStatusCode.Created)
                    throw new GraphMailException($"Attachment upload failed with HTTP {(int)response.StatusCode}.",
                        response.StatusCode);
            }
            else
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new GraphMailException($"Attachment upload failed with HTTP {(int)response.StatusCode}.",
                        response.StatusCode);
                using var progress = await ReadBoundedJsonAsync(response, ct).ConfigureAwait(false);
                if (!progress.RootElement.TryGetProperty("nextExpectedRanges", out var ranges) ||
                    ranges.ValueKind != JsonValueKind.Array || ranges.GetArrayLength() == 0 ||
                    ranges[0].ValueKind != JsonValueKind.String ||
                    !long.TryParse(ranges[0].GetString()?.Split('-')[0], NumberStyles.None,
                        CultureInfo.InvariantCulture, out var next) || next != offset)
                    throw new GraphMailException("Graph returned inconsistent attachment upload progress.");
            }
        }
    }

    public async Task RemoveAttachmentAsync(string token, string draftId, string attachmentId,
        CancellationToken ct = default)
    {
        ValidateId(attachmentId);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        await RequestEmptyAsync(HttpMethod.Delete, MessageUri(draftId) + "/attachments/" +
            Uri.EscapeDataString(attachmentId), token, null, HttpStatusCode.NoContent, ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DraftAttachment>> ListDraftAttachmentsAsync(string token, string draftId,
        CancellationToken ct = default)
    {
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var path = MessageUri(draftId) + "/attachments";
        var next = path + "?$top=50&$select=id,name,size";
        var pages = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var attachments = new List<DraftAttachment>();
        while (next is not null)
        {
            if (!pages.Add(next) || pages.Count > 4)
                throw new GraphMailException("Draft has too many attachment pages.");
            using var response = await RequestJsonAsync(HttpMethod.Get, next, token, null,
                HttpStatusCode.OK, ct).ConfigureAwait(false);
            var root = response.RootElement;
            if (!root.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array ||
                values.GetArrayLength() > 50)
                throw new GraphMailException("Graph returned invalid draft attachments.");
            foreach (var item in values.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new GraphMailException("Graph returned invalid draft attachment metadata.");
                var id = RequiredId(item);
                var name = String(item, "name", 255);
                if (name.Length == 0 || !item.TryGetProperty("size", out var sizeField) ||
                    sizeField.ValueKind != JsonValueKind.Number || !sizeField.TryGetInt32(out var size) || size < 0 ||
                    !ids.Add(id) || attachments.Count >= 100)
                    throw new GraphMailException("Graph returned invalid draft attachment metadata.");
                attachments.Add(new DraftAttachment(id, name, size));
            }
            next = null;
            if (root.TryGetProperty("@odata.nextLink", out var link))
            {
                if (link.ValueKind != JsonValueKind.String ||
                    !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.Host != "graph.microsoft.com" ||
                    !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
                    uri.AbsolutePath != new Uri(path).AbsolutePath || uri.AbsoluteUri.Length > 4096)
                    throw new GraphMailException("Graph returned an unsafe draft attachment page.");
                next = uri.AbsoluteUri;
            }
        }
        return attachments;
    }

    private async Task PatchAsync(string token, string messageId, object payload, CancellationToken ct)
    {
        await VerifyAsync(token, ct).ConfigureAwait(false);
        using var result = await RequestJsonAsync(HttpMethod.Patch, MessageUri(messageId), token,
            payload, HttpStatusCode.OK, ct).ConfigureAwait(false);
        if (RequiredId(result.RootElement) != messageId)
            throw new GraphMailException("Graph returned a different message.");
    }

    private async Task VerifyAsync(string token, CancellationToken ct)
    {
        if (GraphAccountVerification.IsVerified(expectedAccountId, token)) return;
        if (string.IsNullOrWhiteSpace(expectedAccountId) || expectedAccountId.Length > 256 ||
            expectedAccountId.Any(char.IsControl)) throw new GraphMailException("Invalid account identity.");
        using var result = await RequestJsonAsync(HttpMethod.Get, Origin + "/me?$select=id", token,
            null, HttpStatusCode.OK, ct).ConfigureAwait(false);
        if (RequiredId(result.RootElement) != expectedAccountId)
            throw new GraphMailException("Graph token belongs to a different account.");
        GraphAccountVerification.Mark(expectedAccountId, token);
    }

    private async Task<JsonDocument> RequestJsonAsync(HttpMethod method, string path, string token,
        object? payload, HttpStatusCode expected, CancellationToken ct, bool preferText = false)
    {
        using var response = await SendAsync(method, path, token, payload, expected, ct, preferText).ConfigureAwait(false);
        return await ReadBoundedJsonAsync(response, ct).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadBoundedJsonAsync(HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new GraphMailException("Graph response exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxResponseBytes)
                throw new GraphMailException("Graph response exceeds the size limit.");
            buffer.Write(bytes, 0, count);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: ct).ConfigureAwait(false); }
        catch (JsonException) { throw new GraphMailException("Graph returned invalid JSON."); }
    }

    private async Task RequestEmptyAsync(HttpMethod method, string path, string token, object? payload,
        HttpStatusCode expected, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, token, payload, expected, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token,
        object? payload, HttpStatusCode expected, CancellationToken ct, bool preferText = false)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new GraphMailException("A valid access token is required.");
        var uri = new Uri(path);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (preferText) request.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\"");
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != uri || (int)response.StatusCode is >= 300 and < 400)
        {
            response.Dispose();
            throw new GraphMailException("Graph redirected a mail action.");
        }
        if (response.StatusCode != expected)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new GraphMailException($"Mail action failed with HTTP {(int)status}.", status);
        }
        return response;
    }

    private static string MessageUri(string id)
    {
        ValidateId(id);
        return Origin + "/me/messages/" + Uri.EscapeDataString(id);
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 2048 || id.Any(char.IsControl))
            throw new ArgumentException("Invalid message or folder ID.");
    }

    private static string RequiredId(JsonElement json)
    {
        if (!json.TryGetProperty("id", out var field) || field.ValueKind != JsonValueKind.String ||
            field.GetString() is not { } id) throw new GraphMailException("Graph omitted the message ID.");
        ValidateId(id);
        return id;
    }

    private static string String(JsonElement json, string field, int maximum)
    {
        if (!json.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return "";
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text ||
            text.Length > maximum) throw new GraphMailException("Graph returned invalid draft data.");
        return text;
    }

    private static void ValidateText(string text, int maximum)
    {
        if (text.Length > maximum || text.Contains('\0')) throw new ArgumentException("Mail text exceeds the supported limit.");
    }

    private static string ReadRecipients(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null)
            return "";
        if (field.ValueKind != JsonValueKind.Array || field.GetArrayLength() > 100)
            throw new GraphMailException("Graph returned invalid draft recipients.");
        var recipients = new List<string>();
        foreach (var item in field.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("emailAddress", out var email) ||
                email.ValueKind != JsonValueKind.Object)
                throw new GraphMailException("Graph returned invalid draft recipients.");
            var address = String(email, "address", 320);
            if (address.Length == 0) throw new GraphMailException("Graph returned invalid draft recipients.");
            recipients.Add(address);
        }
        return string.Join(", ", recipients);
    }

    private static object[] ParseRecipients(string? recipients)
    {
        if (string.IsNullOrWhiteSpace(recipients)) return [];
        var addresses = recipients.Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (addresses.Length > 100 || addresses.Any(address => address.Length > 320 ||
            address.Any(char.IsControl) || !System.Net.Mail.MailAddress.TryCreate(address, out var parsed) ||
            !string.Equals(parsed.Address, address, StringComparison.Ordinal)))
            throw new ArgumentException("Enter valid email addresses separated by commas.");
        return addresses.Select(address => (object)new { emailAddress = new { address } }).ToArray();
    }
}
