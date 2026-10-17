using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenOutlook.Providers.Microsoft;

/// <summary>One reminder of a calendar event occurrence, as Graph's reminderView lists it.</summary>
public sealed record GraphReminder(string EventId, string ChangeKey, string Subject, string Location,
    DateTimeOffset EventStart, DateTimeOffset EventEnd, DateTimeOffset FireTime, string? WebLink);

/// <summary>
/// Calendar reminders of a personal Microsoft account (docs/reminders-plan.md). Graph's <c>reminderView</c> is the source of truth: it lists every
/// event occurrence whose reminder time falls in a range and that has not been dismissed (or snoozed past now), which is the list Outlook works
/// from. So a reminder missed while OpenOutlook was closed is still listed, and one dismissed in OpenOutlook, Outlook or on a phone is not.
/// Same transport rules as <see cref="GraphCalendarClient"/>: verified account, no redirects, page links only back to Graph, bounded responses,
/// tokens never in errors.
/// </summary>
public sealed class GraphReminderClient(HttpClient http, string expectedAccountId)
{
    private const string Origin = "https://graph.microsoft.com/v1.0";
    private const int MaxJsonBytes = 1024 * 1024;
    private const int MaxPages = 10;
    /// <summary>The longest range asked for: the 90-day maximum look-back plus the day ahead, with room to spare.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(100);

    /// <summary>Undismissed reminders whose fire time lies in [<paramref name="start"/>, <paramref name="end"/>], oldest first.</summary>
    public async Task<IReadOnlyList<GraphReminder>> ReminderViewAsync(string token, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        if (end <= start || end - start > MaxRange)
            throw new ArgumentException("Reminder range must be positive and no longer than 100 days.");
        await VerifyAsync(token, ct).ConfigureAwait(false);
        static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        var url = Origin + "/me/reminderView(startDateTime='" + Stamp(start) + "',endDateTime='" + Stamp(end) + "')";
        var reminders = new List<GraphReminder>();
        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            using var json = await RequestJsonAsync(url, token, ct).ConfigureAwait(false);
            foreach (var item in Value(json.RootElement))
            {
                var eventId = Optional(item, "eventId", 2048);
                if (eventId is null) continue;                                        // nothing to dismiss or open: skip, never fail the list
                var location = item.TryGetProperty("eventLocation", out var loc) && loc.ValueKind == JsonValueKind.Object
                    ? Optional(loc, "displayName", 1024) ?? "" : "";
                reminders.Add(new GraphReminder(eventId, Optional(item, "changeKey", 512) ?? "",
                    Optional(item, "eventSubject", 4096) ?? "(no subject)", location,
                    ReadTime(item, "eventStartTime"), ReadTime(item, "eventEndTime"), ReadTime(item, "reminderFireTime"),
                    Optional(item, "eventWebLink", 4096)));
            }
            url = NextLink(json.RootElement);
        }
        if (url is not null) throw new GraphMailException("Graph returned too many reminder pages.");
        return reminders.OrderBy(r => r.FireTime).ToArray();
    }

    /// <summary>Dismisses the event's reminder for the account (everywhere: Outlook, phone, web). A deleted event answers 404 (<see cref="GraphMailException.StatusCode"/>).</summary>
    public async Task DismissAsync(string token, string eventId, CancellationToken ct = default)
    {
        ValidateId(eventId);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        await PostAsync(Origin + "/me/events/" + Uri.EscapeDataString(eventId) + "/dismissReminder", token, null, ct).ConfigureAwait(false);
    }

    /// <summary>Snoozes the event's reminder until <paramref name="until"/> (for the account, everywhere).</summary>
    public async Task SnoozeAsync(string token, string eventId, DateTimeOffset until, CancellationToken ct = default)
    {
        ValidateId(eventId);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var body = new { newReminderTime = new { dateTime = until.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), timeZone = "UTC" } };
        await PostAsync(Origin + "/me/events/" + Uri.EscapeDataString(eventId) + "/snoozeReminder", token, body, ct).ConfigureAwait(false);
    }

    private async Task VerifyAsync(string token, CancellationToken ct)
    {
        if (GraphAccountVerification.IsVerified(expectedAccountId, token)) return;
        using var json = await RequestJsonAsync(Origin + "/me?$select=id", token, ct).ConfigureAwait(false);
        if (Optional(json.RootElement, "id", 256) != expectedAccountId)
            throw new GraphMailException("Graph token belongs to a different account.");
        GraphAccountVerification.Mark(expectedAccountId, token);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new GraphMailException("A valid access token is required.");
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "graph.microsoft.com" || uri.Port != 443 ||
            !uri.AbsolutePath.StartsWith("/v1.0/me/", StringComparison.Ordinal) && uri.AbsolutePath != "/v1.0/me")
            throw new GraphMailException("Graph returned an unsafe reminder URL.");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\"");
        request.Content = payload is null
            ? (method == HttpMethod.Post ? new StringContent("", Encoding.UTF8, "application/json") : null)
            : new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != uri || (int)response.StatusCode is >= 300 and < 400)
        {
            response.Dispose();
            throw new GraphMailException("Graph redirected a reminder request.");
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new GraphMailException($"Graph reminder request failed with HTTP {(int)status}.", status);
        }
        return response;
    }

    private async Task PostAsync(string url, string token, object? payload, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, url, token, payload, ct).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.Accepted))
            throw new GraphMailException("Graph did not confirm the reminder change.");
    }

    private async Task<JsonDocument> RequestJsonAsync(string url, string token, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, url, token, null, ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaxJsonBytes)
            throw new GraphMailException("Graph reminder response exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxJsonBytes) throw new GraphMailException("Graph reminder response exceeds the size limit.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: ct).ConfigureAwait(false); }
        catch (JsonException) { throw new GraphMailException("Graph returned invalid reminder JSON."); }
    }

    private static IReadOnlyList<JsonElement> Value(JsonElement root)
    {
        if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 1000)
            throw new GraphMailException("Graph returned an invalid reminder list.");
        var items = value.EnumerateArray().ToArray();
        if (items.Any(item => item.ValueKind != JsonValueKind.Object)) throw new GraphMailException("Graph returned invalid reminders.");
        return items;
    }

    private static string? NextLink(JsonElement root)
    {
        var next = Optional(root, "@odata.nextLink", 4096);
        if (next is null) return null;
        if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "graph.microsoft.com" ||
            !uri.AbsolutePath.StartsWith("/v1.0/me/reminderView", StringComparison.Ordinal))
            throw new GraphMailException("Graph returned an unsafe reminder page link.");
        return next;
    }

    /// <summary>A Graph dateTimeTimeZone as an instant (UTC is asked for with the Prefer header; other zones are converted).</summary>
    internal static DateTimeOffset ReadTime(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new GraphMailException("Graph omitted a reminder time.");
        var date = Optional(value, "dateTime", 64) ?? throw new GraphMailException("Graph omitted a reminder time.");
        var zone = Optional(value, "timeZone", 128) ?? "UTC";
        if (date.EndsWith('Z') || date.Length >= 6 && (date[^6] == '+' || date[^6] == '-') && date[^3] == ':')
        {
            if (DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)) return instant.ToUniversalTime();
            throw new GraphMailException("Graph returned an invalid reminder time.");
        }
        if (!DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            throw new GraphMailException("Graph returned an invalid reminder time.");
        if (zone is "UTC" or "Etc/UTC" or "tzone://Microsoft/Utc")
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
        try
        {
            var timezone = TimeZoneInfo.FindSystemTimeZoneById(zone);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), timezone));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { throw new GraphMailException("Graph returned an unsupported reminder time zone."); }
    }

    private static string? Optional(JsonElement root, string name, int limit)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new GraphMailException("Graph returned invalid reminder data.");
        var text = value.GetString();
        if (text is null || text.Length > limit || text.Any(char.IsControl)) throw new GraphMailException("Graph returned invalid reminder data.");
        return text;
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 2048 || id.Any(char.IsControl))
            throw new ArgumentException("Invalid event ID.", nameof(id));
    }
}
