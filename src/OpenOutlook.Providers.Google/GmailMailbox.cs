using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenOutlook.Providers.Google;

/// <summary>
/// Read-only Gmail browsing for the mail window: labels with counts, the newest messages of any label, list-row summaries and the displayable content of
/// one message. Same safety rules as <see cref="GmailInboxReader"/> (verified account, no redirects, bounded responses), but the account is verified once per
/// access token instead of before every request, so a folder of 100 messages costs about 100 requests rather than 200.
/// </summary>
public sealed class GmailMailbox
{
    private const string Root = "https://gmail.googleapis.com/gmail/v1/users/me";
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, ValueTask<string>> _accessToken;
    private readonly string _expectedEmail;
    private string? _verifiedToken;

    public GmailMailbox(HttpClient httpClient, Func<CancellationToken, ValueTask<string>> accessToken, string expectedEmail)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        _expectedEmail = !string.IsNullOrWhiteSpace(expectedEmail) && expectedEmail == expectedEmail.Trim()
            ? expectedEmail : throw new ArgumentException("An expected account email is required.", nameof(expectedEmail));
    }

    public static HttpClient CreateNoRedirectHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false });

    /// <summary>All labels of the mailbox with message counts (counts need one request per label and are best effort).</summary>
    public async Task<IReadOnlyList<GmailLabel>> ListLabelsAsync(CancellationToken cancellationToken = default)
    {
        var token = await VerifiedTokenAsync(cancellationToken).ConfigureAwait(false);
        using var json = await GetJsonAsync("/labels", token, cancellationToken).ConfigureAwait(false);
        var labels = new List<(string Id, string Name, bool System)>();
        if (json.RootElement.TryGetProperty("labels", out var array))
        {
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 500) throw new GmailReadException("Gmail returned invalid labels.");
            foreach (var item in array.EnumerateArray())
            {
                var id = OptionalString(item, "id");
                var name = OptionalString(item, "name");
                if (id is null || name is null || !ValidLabelId(id)) continue;
                labels.Add((id, name, OptionalString(item, "type") == "system"));
            }
        }
        var result = new GmailLabel[labels.Count];
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(labels.Select(async (label, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int? total = null, unread = null;
                try
                {
                    using var detail = await GetJsonAsync("/labels/" + Uri.EscapeDataString(label.Id), token, cancellationToken).ConfigureAwait(false);
                    total = OptionalInt(detail.RootElement, "messagesTotal");
                    unread = OptionalInt(detail.RootElement, "messagesUnread");
                }
                catch (GmailReadException) { }                                   // counts are decoration; the label itself is still usable
                result[index] = new GmailLabel(label.Id, label.Name, label.System, total, unread);
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        return result;
    }

    /// <summary>The labels without their message counts (one request, for the local mailbox copy).</summary>
    public async Task<IReadOnlyList<GmailLabel>> ListLabelNamesAsync(CancellationToken cancellationToken = default)
    {
        var token = await VerifiedTokenAsync(cancellationToken).ConfigureAwait(false);
        using var json = await GetJsonAsync("/labels", token, cancellationToken).ConfigureAwait(false);
        var labels = new List<GmailLabel>();
        if (json.RootElement.TryGetProperty("labels", out var array) && array.ValueKind == JsonValueKind.Array && array.GetArrayLength() <= 500)
            foreach (var item in array.EnumerateArray())
            {
                var id = OptionalString(item, "id");
                var name = OptionalString(item, "name");
                if (id is null || name is null || !ValidLabelId(id)) continue;
                labels.Add(new GmailLabel(id, name, OptionalString(item, "type") == "system", null, null));
            }
        return labels;
    }

    /// <summary>
    /// Ids of the messages of a label (null = all mail) that match a Gmail search query (for example "after:2026/01/01 is:unread"), newest first, up to
    /// <paramref name="maxMessages"/>. Only ids are read: one request per 500 messages.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListMessageIdsAsync(string? labelId, string? query, int maxMessages = 20_000, CancellationToken cancellationToken = default)
    {
        if (labelId is not null && !ValidLabelId(labelId)) throw new ArgumentException("An invalid Gmail label was supplied.", nameof(labelId));
        if (query is not null && (query.Length > 512 || query.Any(char.IsControl))) throw new ArgumentException("An invalid Gmail query was supplied.", nameof(query));
        var token = await VerifiedTokenAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<string>();
        string? pageToken = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var qs = "?maxResults=" + Math.Min(500, maxMessages - ids.Count);
            if (labelId is not null) qs += "&labelIds=" + Uri.EscapeDataString(labelId);
            if (!string.IsNullOrWhiteSpace(query)) qs += "&q=" + Uri.EscapeDataString(query);
            if (labelId is TrashLabel or SpamLabel || (query?.Contains("in:anywhere") ?? false)) qs += "&includeSpamTrash=true";
            if (pageToken is not null) qs += "&pageToken=" + Uri.EscapeDataString(pageToken);
            using var json = await GetJsonAsync("/messages" + qs, token, cancellationToken).ConfigureAwait(false);
            if (json.RootElement.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                foreach (var item in messages.EnumerateArray())
                    if (OptionalString(item, "id") is { } id && ValidId(id) && ids.Count < maxMessages) ids.Add(id);
            pageToken = OptionalString(json.RootElement, "nextPageToken");
            if (pageToken is not null && (!ValidPageToken(pageToken) || !seen.Add(pageToken)))
                throw new GmailReadException("Gmail returned an invalid pagination token.");
        } while (pageToken is not null && ids.Count < maxMessages);
        return ids;
    }

    private const int MaxRawJsonBytes = 40 * 1024 * 1024;

    /// <summary>The complete message as RFC 822 bytes (an .eml), up to about 30 MB.</summary>
    public async Task<byte[]> GetRawAsync(string messageId, CancellationToken cancellationToken = default)
    {
        if (!ValidId(messageId)) throw new ArgumentException("An invalid Gmail message ID was supplied.", nameof(messageId));
        var token = await VerifiedTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, Root + "/messages/" + messageId + "?format=raw");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendRetryAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != request.RequestUri) throw new GmailReadException("Gmail redirected the request.");
        if (!response.IsSuccessStatusCode) throw new GmailReadException($"Gmail read failed with HTTP {(int)response.StatusCode}.", response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxRawJsonBytes) throw new GmailReadException("The message is larger than the supported size.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + n > MaxRawJsonBytes) throw new GmailReadException("The message is larger than the supported size.");
            buffer.Write(chunk, 0, n);
        }
        buffer.Position = 0;
        try
        {
            using var json = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (OptionalString(json.RootElement, "id") != messageId) throw new GmailReadException("Gmail returned a different message.");
            var raw = OptionalString(json.RootElement, "raw") ?? throw new GmailReadException("Gmail returned a message without data.");
            return GmailMimeBuilder.FromBase64Url(raw);
        }
        catch (JsonException) { throw new GmailReadException("Gmail returned invalid JSON."); }
        catch (FormatException) { throw new GmailReadException("Gmail returned invalid message data."); }
    }

    /// <summary>Newest-first message ids of one label.</summary>
    public async Task<IReadOnlyList<string>> ListLabelMessageIdsAsync(string labelId, int maxMessages = 100, CancellationToken cancellationToken = default)
    {
        if (!ValidLabelId(labelId)) throw new ArgumentException("An invalid Gmail label was supplied.", nameof(labelId));
        if (maxMessages is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxMessages));
        var token = await VerifiedTokenAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<string>();
        string? pageToken = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var query = $"?labelIds={Uri.EscapeDataString(labelId)}&maxResults={Math.Min(100, maxMessages - ids.Count)}";
            if (pageToken is not null) query += "&pageToken=" + Uri.EscapeDataString(pageToken);
            using var json = await GetJsonAsync("/messages" + query, token, cancellationToken).ConfigureAwait(false);
            if (json.RootElement.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                foreach (var item in messages.EnumerateArray())
                    if (OptionalString(item, "id") is { } id && ValidId(id) && ids.Count < maxMessages) ids.Add(id);
            pageToken = OptionalString(json.RootElement, "nextPageToken");
            if (pageToken is not null && (!ValidPageToken(pageToken) || !seen.Add(pageToken)))
                throw new GmailReadException("Gmail returned an invalid pagination token.");
        } while (pageToken is not null && ids.Count < maxMessages);
        return ids;
    }

    /// <summary>List-row data for many messages (metadata only, a few requests at a time); the result keeps the order of <paramref name="ids"/>.</summary>
    public async Task<IReadOnlyList<GmailSummary>> GetSummariesAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        var token = await VerifiedTokenAsync(cancellationToken).ConfigureAwait(false);
        var result = new GmailSummary?[ids.Count];
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(ids.Select(async (id, index) =>
        {
            if (!ValidId(id)) return;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var json = await GetJsonAsync("/messages/" + id +
                    "?format=metadata&metadataHeaders=From&metadataHeaders=To&metadataHeaders=Subject&metadataHeaders=Date", token, cancellationToken).ConfigureAwait(false);
                result[index] = ParseSummary(json.RootElement, id);
            }
            catch (GmailReadException) { }                                       // one unreadable message must not hide the rest
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        return result.Where(r => r is not null).Select(r => r!).ToList();
    }

    /// <summary>The displayable content of one message: HTML and plain-text bodies, the names of its attachments and its headers.</summary>
    public async Task<GmailContent> GetContentAsync(string messageId, CancellationToken cancellationToken = default)
    {
        if (!ValidId(messageId)) throw new ArgumentException("An invalid Gmail message ID was supplied.", nameof(messageId));
        var token = await VerifiedTokenAsync(cancellationToken).ConfigureAwait(false);
        using var json = await GetJsonAsync("/messages/" + messageId + "?format=full", token, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || OptionalString(root, "id") != messageId) throw new GmailReadException("Gmail returned an invalid message.");
        string? html = null, text = null;
        var attachments = new List<GmailAttachmentInfo>();
        var headers = new List<GmailHeader>();
        if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
        {
            ReadContentParts(payload, ref html, ref text, attachments, 0, new PartCounter());
            if (payload.TryGetProperty("headers", out var ha) && ha.ValueKind == JsonValueKind.Array && ha.GetArrayLength() <= 256)
                foreach (var h in ha.EnumerateArray())
                    if (OptionalString(h, "name") is { } n && OptionalString(h, "value") is { } v) headers.Add(new GmailHeader(n, v));
        }
        return new GmailContent(html, text, attachments, headers, OptionalString(root, "snippet"), OptionalString(root, "threadId"));
    }

    // ---- changes (need the gmail.modify scope) ----

    public const string UnreadLabel = "UNREAD", StarredLabel = "STARRED", InboxLabel = "INBOX", TrashLabel = "TRASH", SpamLabel = "SPAM";
    private const int MaxBatch = 1000;

    /// <summary>Adds and removes labels on messages. In Gmail a "move" is exactly this: add the destination label, remove the current one.</summary>
    public async Task ModifyLabelsAsync(IReadOnlyList<string> messageIds, IReadOnlyList<string> addLabels, IReadOnlyList<string> removeLabels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0) return;
        if (messageIds.Any(id => !ValidId(id))) throw new ArgumentException("An invalid Gmail message ID was supplied.", nameof(messageIds));
        if (addLabels.Concat(removeLabels).Any(l => !ValidLabelId(l))) throw new ArgumentException("An invalid Gmail label was supplied.");
        if (addLabels.Count == 0 && removeLabels.Count == 0) return;
        var token = await VerifiedTokenAsync(cancellationToken).ConfigureAwait(false);
        if (messageIds.Count == 1)
        {
            await PostJsonAsync("/messages/" + messageIds[0] + "/modify", new { addLabelIds = addLabels, removeLabelIds = removeLabels }, token, cancellationToken).ConfigureAwait(false);
            return;
        }
        for (var i = 0; i < messageIds.Count; i += MaxBatch)
            await PostJsonAsync("/messages/batchModify",
                new { ids = messageIds.Skip(i).Take(MaxBatch).ToArray(), addLabelIds = addLabels, removeLabelIds = removeLabels }, token, cancellationToken).ConfigureAwait(false);
    }

    public Task SetReadAsync(IReadOnlyList<string> messageIds, bool read, CancellationToken cancellationToken = default) =>
        read ? ModifyLabelsAsync(messageIds, [], [UnreadLabel], cancellationToken) : ModifyLabelsAsync(messageIds, [UnreadLabel], [], cancellationToken);

    public Task SetStarredAsync(IReadOnlyList<string> messageIds, bool starred, CancellationToken cancellationToken = default) =>
        starred ? ModifyLabelsAsync(messageIds, [StarredLabel], [], cancellationToken) : ModifyLabelsAsync(messageIds, [], [StarredLabel], cancellationToken);

    /// <summary>Takes messages out of the inbox (they stay in All Mail and keep their other labels).</summary>
    public Task ArchiveAsync(IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default) =>
        ModifyLabelsAsync(messageIds, [], [InboxLabel], cancellationToken);

    /// <summary>Moves messages to Trash (Gmail deletes them for good after 30 days). Permanent deletion needs a broader scope and is not offered.</summary>
    public async Task TrashAsync(IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default)
    {
        if (messageIds.Any(id => !ValidId(id))) throw new ArgumentException("An invalid Gmail message ID was supplied.", nameof(messageIds));
        if (messageIds.Count == 0) return;
        var token = await VerifiedTokenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in messageIds)
            await PostJsonAsync("/messages/" + id + "/trash", null, token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a label (a "folder"); a name with slashes nests it under its parent label.</summary>
    public async Task<GmailLabel> CreateLabelAsync(string name, CancellationToken cancellationToken = default)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 225 || name.Any(char.IsControl) || name.StartsWith('/') || name.EndsWith('/') || name.Contains("//", StringComparison.Ordinal))
            throw new ArgumentException("Invalid label name.", nameof(name));
        var token = await VerifiedTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, Root + "/labels");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(new { name, labelListVisibility = "labelShow", messageListVisibility = "show" }), Encoding.UTF8, "application/json");
        using var response = await SendRetryAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != request.RequestUri) throw new GmailReadException("Gmail redirected the request.");
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict) throw new GmailReadException("A Gmail label with that name already exists.", response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new GmailReadException(response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
                ? "Gmail did not allow this change. Sign in again from Account setup to allow organizing mail."
                : $"Gmail could not create the label (HTTP {(int)response.StatusCode}).", response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (text.Length > MaxResponseBytes) throw new GmailReadException("Gmail response exceeds the size limit.");
        try
        {
            using var json = JsonDocument.Parse(text);
            var id = OptionalString(json.RootElement, "id");
            if (id is null || !ValidLabelId(id)) throw new GmailReadException("Gmail returned an invalid label.");
            return new GmailLabel(id, OptionalString(json.RootElement, "name") ?? name, false, 0, 0);
        }
        catch (JsonException) { throw new GmailReadException("Gmail returned invalid JSON."); }
    }

    private async Task PostJsonAsync(string relativePath, object? body, string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Post, Root + relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body is null ? "" : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await SendRetryAsync(request, ct).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != request.RequestUri) throw new GmailReadException("Gmail redirected the request.");
        if (!response.IsSuccessStatusCode)
            throw new GmailReadException(response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
                ? "Gmail did not allow this change. Sign in again from Account setup to allow organizing mail."
                : $"Gmail change failed with HTTP {(int)response.StatusCode}.", response.StatusCode);
    }

    // ---- attachments, sending and drafts ----

    private const int MaxAttachmentJsonBytes = 40 * 1024 * 1024;         // a 25 MB attachment is about 34 MB of base64 in JSON

    /// <summary>Downloads one attachment of a message (up to Gmail's own 25 MB limit).</summary>
    public async Task<byte[]> GetAttachmentAsync(string messageId, string attachmentId, CancellationToken cancellationToken = default)
    {
        if (!ValidId(messageId)) throw new ArgumentException("An invalid Gmail message ID was supplied.", nameof(messageId));
        if (!ValidAttachmentId(attachmentId)) throw new ArgumentException("An invalid Gmail attachment ID was supplied.", nameof(attachmentId));
        var token = await VerifiedTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, Root + "/messages/" + messageId + "/attachments/" + Uri.EscapeDataString(attachmentId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendRetryAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != request.RequestUri) throw new GmailReadException("Gmail redirected the request.");
        if (!response.IsSuccessStatusCode) throw new GmailReadException($"Gmail read failed with HTTP {(int)response.StatusCode}.", response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxAttachmentJsonBytes) throw new GmailReadException("The attachment is larger than the supported size.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + n > MaxAttachmentJsonBytes) throw new GmailReadException("The attachment is larger than the supported size.");
            buffer.Write(chunk, 0, n);
        }
        buffer.Position = 0;
        try
        {
            using var json = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
            var data = OptionalString(json.RootElement, "data") ?? throw new GmailReadException("Gmail returned an attachment without data.");
            return GmailMimeBuilder.FromBase64Url(data);
        }
        catch (JsonException) { throw new GmailReadException("Gmail returned invalid JSON."); }
        catch (FormatException) { throw new GmailReadException("Gmail returned invalid attachment data."); }
    }

    /// <summary>Sends a finished message (needs the gmail.send scope). threadId keeps a reply in its conversation. Returns the new message id.</summary>
    public async Task<string> SendAsync(byte[] mime, string? threadId = null, CancellationToken cancellationToken = default)
    {
        var token = await VerifiedTokenAsync(cancellationToken).ConfigureAwait(false);
        using var json = await SendJsonAsync(HttpMethod.Post, "/messages/send", RawBody(mime, threadId), token, cancellationToken).ConfigureAwait(false);
        return OptionalString(json.RootElement, "id") ?? throw new GmailReadException("Gmail did not confirm the message.");
    }

    /// <summary>Creates (draftId null) or replaces a draft; returns the draft id.</summary>
    public async Task<string> SaveDraftAsync(string? draftId, byte[] mime, string? threadId = null, CancellationToken cancellationToken = default)
    {
        if (draftId is not null && !ValidId(draftId)) throw new ArgumentException("An invalid Gmail draft ID was supplied.", nameof(draftId));
        var token = await VerifiedTokenAsync(cancellationToken).ConfigureAwait(false);
        using var json = draftId is null
            ? await SendJsonAsync(HttpMethod.Post, "/drafts", new { message = RawBody(mime, threadId) }, token, cancellationToken).ConfigureAwait(false)
            : await SendJsonAsync(HttpMethod.Put, "/drafts/" + draftId, new { id = draftId, message = RawBody(mime, threadId) }, token, cancellationToken).ConfigureAwait(false);
        return OptionalString(json.RootElement, "id") ?? throw new GmailReadException("Gmail did not confirm the draft.");
    }

    public async Task SendDraftAsync(string draftId, CancellationToken cancellationToken = default)
    {
        if (!ValidId(draftId)) throw new ArgumentException("An invalid Gmail draft ID was supplied.", nameof(draftId));
        var token = await VerifiedTokenAsync(cancellationToken).ConfigureAwait(false);
        using var json = await SendJsonAsync(HttpMethod.Post, "/drafts/send", new { id = draftId }, token, cancellationToken).ConfigureAwait(false);
    }

    private static object RawBody(byte[] mime, string? threadId)
    {
        if (mime is null || mime.Length == 0) throw new ArgumentException("There is no message to send.", nameof(mime));
        if (threadId is not null && !ValidId(threadId)) throw new ArgumentException("An invalid Gmail thread ID was supplied.", nameof(threadId));
        var raw = GmailMimeBuilder.ToBase64Url(mime);
        return threadId is null ? new { raw } : new { raw, threadId };
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string relativePath, object body, string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, Root + relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await SendRetryAsync(request, ct).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != request.RequestUri) throw new GmailReadException("Gmail redirected the request.");
        if (!response.IsSuccessStatusCode)
            throw new GmailReadException(response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
                ? "Gmail did not allow sending. Sign in again from Account setup to allow sending mail."
                : response.StatusCode == System.Net.HttpStatusCode.BadRequest ? "Gmail rejected the message (check the recipients and attachments)."
                : $"Gmail could not complete the request (HTTP {(int)response.StatusCode}).", response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (text.Length > MaxResponseBytes) throw new GmailReadException("Gmail response exceeds the size limit.");
        try { return JsonDocument.Parse(text.Length == 0 ? "{}" : text); }
        catch (JsonException) { throw new GmailReadException("Gmail returned invalid JSON."); }
    }

    private static bool ValidAttachmentId(string? id) => id is { Length: >= 1 and <= 4096 } &&
        id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '=' or '.');

    // ---- internals ----

    /// <summary>
    /// Sends a request; when Gmail answers 429, 500, 502, 503 or a 403 that says the rate limit was exceeded, waits (Retry-After, else 1, 2, 4, 8 seconds) and sends it again,
    /// up to four more times. A burst of requests (the mailbox copy lists many labels) otherwise ends in "HTTP 403" for a minute.
    /// </summary>
    private async Task<HttpResponseMessage> SendRetryAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var contentType = request.Content?.Headers.ContentType;
        for (var attempt = 0; ; attempt++)
        {
            HttpRequestMessage current = request;
            if (attempt > 0)
            {
                current = new HttpRequestMessage(request.Method, request.RequestUri);
                foreach (var h in request.Headers) current.Headers.TryAddWithoutValidation(h.Key, h.Value);
                if (body is not null) { current.Content = new ByteArrayContent(body); if (contentType is not null) current.Content.Headers.ContentType = contentType; }
            }
            var response = await _http.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (attempt >= 4 || !await IsRetryableAsync(response, ct).ConfigureAwait(false)) return response;
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1 << attempt);
            if (wait > TimeSpan.FromSeconds(30)) wait = TimeSpan.FromSeconds(30);
            response.Dispose();
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> IsRetryableAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var code = (int)response.StatusCode;
        if (code is 429 or 500 or 502 or 503) return true;
        if (code != 403) return false;
        try
        {
            await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase) || text.Contains("userRateLimitExceeded", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is HttpRequestException or IOException) { return false; }
    }

    private async Task<string> VerifiedTokenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var token = await _accessToken(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)) throw new GmailReadException("A valid access token is required.");
        if (!string.Equals(_verifiedToken, token, StringComparison.Ordinal))
        {
            using var json = await GetJsonAsync("/profile", token, ct).ConfigureAwait(false);
            if (!string.Equals(OptionalString(json.RootElement, "emailAddress"), _expectedEmail, StringComparison.OrdinalIgnoreCase))
                throw new GmailReadException("Gmail token belongs to a different account.");
            _verifiedToken = token;
        }
        return token;
    }

    private async Task<JsonDocument> GetJsonAsync(string relativePath, string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Get, Root + relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendRetryAsync(request, ct).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != request.RequestUri) throw new GmailReadException("Gmail redirected the request.");
        if (!response.IsSuccessStatusCode) throw new GmailReadException($"Gmail read failed with HTTP {(int)response.StatusCode}.", response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new GmailReadException("Gmail response exceeds the size limit.");
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

    private static GmailSummary ParseSummary(JsonElement root, string id)
    {
        string Header(string name)
        {
            if (root.TryGetProperty("payload", out var payload) && payload.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
                foreach (var h in headers.EnumerateArray())
                    if (string.Equals(OptionalString(h, "name"), name, StringComparison.OrdinalIgnoreCase)) return OptionalString(h, "value") ?? "";
            return "";
        }
        var labels = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("labelIds", out var la) && la.ValueKind == JsonValueKind.Array)
            foreach (var l in la.EnumerateArray()) if (l.ValueKind == JsonValueKind.String) labels.Add(l.GetString()!);
        DateTimeOffset? date = null;
        if (long.TryParse(OptionalString(root, "internalDate"), out var ms) && ms > 0) date = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        else if (DateTimeOffset.TryParse(Header("Date"), out var parsed)) date = parsed;
        var mime = root.TryGetProperty("payload", out var pl) ? OptionalString(pl, "mimeType") ?? "" : "";
        return new GmailSummary(id, OptionalString(root, "threadId"), Header("Subject"), Header("From"), Header("To"), date,
            OptionalInt(root, "sizeEstimate"), labels.Contains("UNREAD"), labels.Contains("STARRED"), labels.Contains("DRAFT"),
            mime.StartsWith("multipart/mixed", StringComparison.OrdinalIgnoreCase), OptionalString(root, "snippet") ?? "");
    }

    private static void ReadContentParts(JsonElement part, ref string? html, ref string? text, List<GmailAttachmentInfo> attachments, int depth, PartCounter counter)
    {
        if (depth > 8 || ++counter.Count > 64) throw new GmailReadException("Gmail message has too many MIME parts.");
        var mime = OptionalString(part, "mimeType") ?? "";
        var filename = OptionalString(part, "filename") ?? "";
        if (part.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
        {
            if (filename.Length > 0) attachments.Add(new GmailAttachmentInfo(filename, mime, OptionalInt(body, "size") ?? 0, OptionalString(body, "attachmentId")));
            else if (body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
            {
                if (mime == "text/html" && html is null) html = DecodeBody(data.GetString()!);
                else if (mime == "text/plain" && text is null) text = DecodeBody(data.GetString()!);
            }
        }
        if (!part.TryGetProperty("parts", out var children)) return;
        if (children.ValueKind != JsonValueKind.Array || children.GetArrayLength() > 64) throw new GmailReadException("Gmail returned invalid MIME parts.");
        foreach (var child in children.EnumerateArray())
            if (child.ValueKind == JsonValueKind.Object) ReadContentParts(child, ref html, ref text, attachments, depth + 1, counter);
    }

    private static string DecodeBody(string encoded)
    {
        if (encoded.Length > MaxBodyBytes * 2) throw new GmailReadException("Gmail message body exceeds the size limit.");
        try
        {
            var bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/').PadRight((encoded.Length + 3) / 4 * 4, '='));
            if (bytes.Length > MaxBodyBytes) throw new GmailReadException("Gmail message body exceeds the size limit.");
            return new UTF8Encoding(false, false).GetString(bytes);              // lenient: a few bad bytes must not hide the whole message
        }
        catch (FormatException) { throw new GmailReadException("Gmail returned an invalid message body."); }
    }

    private static string? OptionalString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? OptionalInt(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool ValidId(string? id) => id is { Length: >= 1 and <= 256 } &&
        id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');

    private static bool ValidLabelId(string? id) => id is { Length: >= 1 and <= 256 } &&
        id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '/' or ' ' or '.');

    private static bool ValidPageToken(string? token) => token is { Length: >= 1 and <= 1024 } &&
        token.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '+' or '/' or '=');

    private sealed class PartCounter { public int Count; }
}

public sealed record GmailLabel(string Id, string Name, bool IsSystem, int? Total, int? Unread);
public sealed record GmailSummary(string Id, string? ThreadId, string Subject, string From, string To, DateTimeOffset? Date, int? SizeBytes,
    bool IsUnread, bool IsStarred, bool IsDraft, bool HasAttachments, string Snippet);
public sealed record GmailAttachmentInfo(string FileName, string MimeType, int SizeBytes, string? AttachmentId = null);
public sealed record GmailContent(string? Html, string? Text, IReadOnlyList<GmailAttachmentInfo> Attachments, IReadOnlyList<GmailHeader> Headers, string? Snippet, string? ThreadId = null);
