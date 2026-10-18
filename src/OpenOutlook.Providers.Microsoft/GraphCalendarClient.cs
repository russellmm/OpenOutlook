using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenOutlook.Providers.Microsoft;

public sealed record GraphCalendar(string Id, string Name, bool IsDefault, bool CanEdit = true);

public sealed record GraphCalendarEvent(string Id, string Subject, DateTimeOffset Start,
    DateTimeOffset End, bool IsAllDay, string Location, bool HasAttendees);

public sealed record NewCalendarEvent(string Subject, DateTimeOffset Start, DateTimeOffset End,
    bool IsAllDay, string Location, string Body, IReadOnlyList<string> Attendees, Guid TransactionId,
    string TimeZoneId = "UTC", int? ReminderMinutesBeforeStart = 15,      // null: no reminder (Outlook's "None")
    string ShowAs = "busy", string Sensitivity = "normal", string Importance = "normal");

/// <summary>Everything the appointment window shows of an existing event (one occurrence for a recurring series).</summary>
public sealed record GraphEventDetails(string Id, string Subject, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay,
    DateOnly StartDate, DateOnly EndDate, string Location, string Body, IReadOnlyList<string> Attendees, bool IsReminderOn,
    int ReminderMinutesBeforeStart, string ShowAs, string Sensitivity, string Importance, bool IsOrganizer, string Type, string Organizer)
{
    /// <summary>An occurrence (or changed occurrence) of a recurring series: changes apply to it alone.</summary>
    public bool IsOccurrence => Type is "occurrence" or "exception";
}

/// <summary>Personal Microsoft calendar access through a no-redirect Graph transport.</summary>
public sealed class GraphCalendarClient(HttpClient http, string expectedAccountId)
{
    private const string Origin = "https://graph.microsoft.com/v1.0";
    private const int MaxJsonBytes = 1024 * 1024;
    private const int MaxPages = 10;

    public async Task<IReadOnlyList<GraphCalendar>> ListCalendarsAsync(string token, CancellationToken ct = default)
    {
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var calendars = new List<GraphCalendar>();
        var url = Origin + "/me/calendars?$select=id,name,isDefaultCalendar,canEdit&$top=100";
        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            using var json = await RequestAsync(HttpMethod.Get, url, token, null, ct).ConfigureAwait(false);
            foreach (var item in Value(json.RootElement))
                calendars.Add(new GraphCalendar(Required(item, "id", 2048), Required(item, "name", 512),
                    Boolean(item, "isDefaultCalendar"), !item.TryGetProperty("canEdit", out var canEdit) ||
                    canEdit.ValueKind != JsonValueKind.False));
            url = NextLink(json.RootElement);
        }
        if (url is not null) throw new GraphMailException("Graph returned too many calendar pages.");
        return calendars;
    }

    public async Task<IReadOnlyList<GraphCalendarEvent>> GetEventsAsync(string token, string calendarId,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        ValidateId(calendarId);
        if (end <= start || end - start > TimeSpan.FromDays(62))
            throw new ArgumentException("Calendar range must be positive and no longer than 62 days.");
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var events = new List<GraphCalendarEvent>();
        var url = Origin + "/me/calendars/" + Uri.EscapeDataString(calendarId) + "/calendarView" +
            "?startDateTime=" + Uri.EscapeDataString(start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)) +
            "&endDateTime=" + Uri.EscapeDataString(end.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)) +
            "&$select=id,subject,start,end,isAllDay,isCancelled,location,attendees&$top=100";
        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            using var json = await RequestAsync(HttpMethod.Get, url, token, null, ct).ConfigureAwait(false);
            foreach (var item in Value(json.RootElement))
            {
                if (Boolean(item, "isCancelled")) continue;
                var startTime = ReadTime(item, "start");
                var endTime = ReadTime(item, "end");
                if (endTime <= startTime) continue;
                var location = item.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object
                    ? Optional(loc, "displayName", 1024) ?? "" : "";
                var attendees = item.TryGetProperty("attendees", out var list) && list.ValueKind == JsonValueKind.Array &&
                    list.GetArrayLength() > 0;
                events.Add(new GraphCalendarEvent(Required(item, "id", 2048),
                    Optional(item, "subject", 4096) ?? "(no subject)", startTime, endTime,
                    Boolean(item, "isAllDay"), location, attendees));
            }
            url = NextLink(json.RootElement);
        }
        if (url is not null) throw new GraphMailException("Graph returned too many event pages.");
        return events.OrderBy(item => item.Start).ToArray();
    }

    public async Task<GraphCalendarEvent> CreateEventAsync(string token, string calendarId,
        NewCalendarEvent item, CancellationToken ct = default)
    {
        ValidateId(calendarId);
        Validate(item);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var payload = Payload(item, includeBody: true, includeAttendees: true);
        payload["transactionId"] = item.TransactionId.ToString("D");
        var url = Origin + "/me/calendars/" + Uri.EscapeDataString(calendarId) + "/events";
        using var json = await RequestAsync(HttpMethod.Post, url, token, payload, ct, HttpStatusCode.Created).ConfigureAwait(false);
        return Summary(json.RootElement, item);
    }

    /// <summary>One event with everything the appointment window edits; the body as plain text.</summary>
    public async Task<GraphEventDetails> GetEventAsync(string token, string eventId, CancellationToken ct = default)
    {
        ValidateId(eventId);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var url = Origin + "/me/events/" + Uri.EscapeDataString(eventId) +
            "?$select=id,subject,start,end,isAllDay,location,body,attendees,isReminderOn,reminderMinutesBeforeStart,showAs,sensitivity,importance,isOrganizer,type,organizer";
        using var json = await RequestAsync(HttpMethod.Get, url, token, null, ct, prefer: "outlook.body-content-type=\"text\"").ConfigureAwait(false);
        var root = json.RootElement;
        var location = root.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object ? Optional(loc, "displayName", 1024) ?? "" : "";
        var body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object ? Text(b, "content", 65_536) : "";
        var attendees = root.TryGetProperty("attendees", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Take(500).Select(a => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("emailAddress", out var e) && e.ValueKind == JsonValueKind.Object
                ? Optional(e, "address", 320) : null).OfType<string>().ToList()
            : [];
        var organizer = root.TryGetProperty("organizer", out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty("emailAddress", out var oe) &&
            oe.ValueKind == JsonValueKind.Object ? Optional(oe, "address", 320) ?? Optional(oe, "name", 320) ?? "" : "";
        var reminder = root.TryGetProperty("reminderMinutesBeforeStart", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var minutes) ? minutes : 15;
        return new GraphEventDetails(Required(root, "id", 2048), Optional(root, "subject", 4096) ?? "", ReadTime(root, "start"), ReadTime(root, "end"),
            Boolean(root, "isAllDay"), ReadDate(root, "start"), ReadDate(root, "end"), location, body, attendees,
            !root.TryGetProperty("isReminderOn", out var on) || on.ValueKind != JsonValueKind.False, reminder,
            Optional(root, "showAs", 32) ?? "busy", Optional(root, "sensitivity", 32) ?? "normal", Optional(root, "importance", 32) ?? "normal",
            !root.TryGetProperty("isOrganizer", out var org) || org.ValueKind != JsonValueKind.False, Optional(root, "type", 32) ?? "singleInstance", organizer);
    }

    /// <summary>
    /// Saves the appointment window's fields to an existing event (PATCH; for an occurrence of a series only that occurrence changes). The body is sent
    /// only when it was changed (Graph would store the plain text over a formatted body), the attendees only for a meeting the account organizes.
    /// </summary>
    public async Task<GraphCalendarEvent> UpdateEventAsync(string token, string eventId, NewCalendarEvent item, bool bodyChanged, bool attendeesChanged,
        CancellationToken ct = default)
    {
        ValidateId(eventId);
        Validate(item);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var payload = Payload(item, includeBody: bodyChanged, includeAttendees: attendeesChanged);
        using var json = await RequestAsync(HttpMethod.Patch, Origin + "/me/events/" + Uri.EscapeDataString(eventId), token, payload, ct, HttpStatusCode.OK).ConfigureAwait(false);
        return Summary(json.RootElement, item);
    }

    /// <summary>Removes the event from the calendar (Graph answers 204). A meeting the account organizes is cancelled for its attendees by Outlook.com.</summary>
    public async Task DeleteEventAsync(string token, string eventId, CancellationToken ct = default)
    {
        ValidateId(eventId);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        using var _ = await RequestAsync(HttpMethod.Delete, Origin + "/me/events/" + Uri.EscapeDataString(eventId), token, null, ct, HttpStatusCode.NoContent).ConfigureAwait(false);
    }

    private static readonly string[] ShowAsValues = ["free", "tentative", "busy", "oof", "workingElsewhere"];

    private static void Validate(NewCalendarEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Subject) || item.Subject.Length > 4096 || item.Subject.Any(char.IsControl) ||
            item.End <= item.Start || item.End - item.Start > TimeSpan.FromDays(366) ||
            item.Location.Length > 1024 || item.Body.Length > 32_768 || item.TransactionId == Guid.Empty)
            throw new ArgumentException("Invalid calendar event.", nameof(item));
        if (item.Attendees.Count > 100 || item.Attendees.Any(address =>
            string.IsNullOrWhiteSpace(address) || address.Length > 320 || address.Any(char.IsWhiteSpace) ||
            !address.Contains('@')))
            throw new ArgumentException("Invalid meeting attendees.", nameof(item));
        if (item.IsAllDay && (item.Start.TimeOfDay != TimeSpan.Zero || item.End.TimeOfDay != TimeSpan.Zero ||
                              string.IsNullOrWhiteSpace(item.TimeZoneId) || item.TimeZoneId.Length > 128))
            throw new ArgumentException("All-day event boundaries must be midnight in one time zone.", nameof(item));
        if (item.ReminderMinutesBeforeStart is < 0 or > 4 * 7 * 24 * 60)
            throw new ArgumentException("A reminder can be up to four weeks before the start.", nameof(item));
        if (!ShowAsValues.Contains(item.ShowAs) || item.Sensitivity is not ("normal" or "personal" or "private" or "confidential") ||
            item.Importance is not ("low" or "normal" or "high"))
            throw new ArgumentException("Invalid Show As, sensitivity or importance.", nameof(item));
    }

    private static Dictionary<string, object> Payload(NewCalendarEvent item, bool includeBody, bool includeAttendees)
    {
        object Time(DateTimeOffset value) => new
        {
            dateTime = item.IsAllDay
                ? value.DateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
                : value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            timeZone = item.IsAllDay ? item.TimeZoneId : "UTC"
        };
        var payload = new Dictionary<string, object>
        {
            ["subject"] = item.Subject.Trim(),
            ["start"] = Time(item.Start),
            ["end"] = Time(item.End),
            ["isAllDay"] = item.IsAllDay,
            ["location"] = new { displayName = item.Location.Trim() },
            ["isReminderOn"] = item.ReminderMinutesBeforeStart is not null,
            ["reminderMinutesBeforeStart"] = item.ReminderMinutesBeforeStart ?? 0,
            ["showAs"] = item.ShowAs,
            ["sensitivity"] = item.Sensitivity,
            ["importance"] = item.Importance,
        };
        if (includeBody) payload["body"] = new { contentType = "text", content = item.Body };
        if (includeAttendees)
            payload["attendees"] = item.Attendees.Select(address => new { emailAddress = new { address }, type = "required" }).ToArray();
        return payload;
    }

    private static GraphCalendarEvent Summary(JsonElement root, NewCalendarEvent item) =>
        new(Required(root, "id", 2048), Optional(root, "subject", 4096) ?? item.Subject, ReadTime(root, "start"), ReadTime(root, "end"),
            Boolean(root, "isAllDay"), item.Location, item.Attendees.Count > 0);

    /// <summary>The calendar date of a dateTimeTimeZone as Graph wrote it (all-day events: the date itself, not a converted instant).</summary>
    private static DateOnly ReadDate(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object && Optional(value, "dateTime", 64) is { Length: >= 10 } text &&
            DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        throw new GraphMailException("Graph omitted an event date.");
    }

    /// <summary>Text that may hold line breaks and tabs (an event body); other control characters are dropped.</summary>
    private static string Text(JsonElement root, string name, int limit)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return "";
        if (value.ValueKind != JsonValueKind.String) throw new GraphMailException("Graph returned invalid calendar data.");
        var text = value.GetString() ?? "";
        if (text.Length > limit) text = text[..limit];
        return new string(text.Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').ToArray());
    }

    private async Task VerifyAsync(string token, CancellationToken ct)
    {
        if (GraphAccountVerification.IsVerified(expectedAccountId, token)) return;
        using var json = await RequestAsync(HttpMethod.Get, Origin + "/me?$select=id", token, null, ct)
            .ConfigureAwait(false);
        if (Required(json.RootElement, "id", 256) != expectedAccountId)
            throw new GraphMailException("Graph token belongs to a different account.");
        GraphAccountVerification.Mark(expectedAccountId, token);
    }

    private async Task<JsonDocument> RequestAsync(HttpMethod method, string url, string token,
        object? payload, CancellationToken ct, HttpStatusCode? expected = null, string? prefer = null)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new GraphMailException("A valid access token is required.");
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "graph.microsoft.com" || uri.Port != 443 ||
            !uri.AbsolutePath.StartsWith("/v1.0/me/", StringComparison.Ordinal) && uri.AbsolutePath != "/v1.0/me")
            throw new GraphMailException("Graph returned an unsafe calendar URL.");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\"");
        if (prefer is not null) request.Headers.TryAddWithoutValidation("Prefer", prefer);
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != uri || (int)response.StatusCode is >= 300 and < 400)
            throw new GraphMailException("Graph redirected a calendar request.");
        if (!response.IsSuccessStatusCode)
            throw new GraphMailException($"Graph calendar request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode);
        if (expected is { } want && response.StatusCode != want)
            throw new GraphMailException(method == HttpMethod.Post ? "Graph did not confirm event creation." : "Graph did not confirm the calendar change.");
        if (response.StatusCode == HttpStatusCode.NoContent) return JsonDocument.Parse("{}");
        if (response.Content.Headers.ContentLength > MaxJsonBytes)
            throw new GraphMailException("Graph calendar response exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxJsonBytes)
                throw new GraphMailException("Graph calendar response exceeds the size limit.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: ct).ConfigureAwait(false); }
        catch (JsonException) { throw new GraphMailException("Graph returned invalid calendar JSON."); }
    }

    private static IReadOnlyList<JsonElement> Value(JsonElement root)
    {
        if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() > 100)
            throw new GraphMailException("Graph returned an invalid calendar page.");
        var items = value.EnumerateArray().ToArray();
        if (items.Any(item => item.ValueKind != JsonValueKind.Object))
            throw new GraphMailException("Graph returned invalid calendar items.");
        return items;
    }

    private static string? NextLink(JsonElement root)
    {
        var next = Optional(root, "@odata.nextLink", 4096);
        if (next is null) return null;
        if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "graph.microsoft.com" || uri.AbsolutePath != "/v1.0/me/calendars" &&
            !uri.AbsolutePath.StartsWith("/v1.0/me/calendars/", StringComparison.Ordinal))
            throw new GraphMailException("Graph returned an unsafe calendar page link.");
        return next;
    }

    private static DateTimeOffset ReadTime(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new GraphMailException("Graph omitted an event time.");
        var date = Required(value, "dateTime", 64);
        var zone = Required(value, "timeZone", 128);
        // Graph normally supplies a wall-clock string plus a separate time zone, but some
        // responses include an ISO offset. Parse that directly to avoid converting it twice.
        if (date.EndsWith('Z') || date.Length >= 6 && (date[^6] == '+' || date[^6] == '-') && date[^3] == ':')
        {
            if (DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out var instant)) return instant.ToUniversalTime();
            throw new GraphMailException("Graph returned an invalid event time.");
        }
        if (!DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            throw new GraphMailException("Graph returned an invalid event time.");
        try
        {
            var timezone = TimeZoneInfo.FindSystemTimeZoneById(zone);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), timezone));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { throw new GraphMailException("Graph returned an unsupported event time zone."); }
    }

    private static bool Boolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Required(JsonElement root, string name, int limit) =>
        Optional(root, name, limit) ?? throw new GraphMailException("Graph omitted required calendar data.");

    private static string? Optional(JsonElement root, string name, int limit)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new GraphMailException("Graph returned invalid calendar data.");
        var text = value.GetString();
        if (text is null || text.Length > limit || text.Any(char.IsControl))
            throw new GraphMailException("Graph returned invalid calendar data.");
        return text;
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 2048 || id.Any(char.IsControl))
            throw new ArgumentException("Invalid calendar ID.", nameof(id));
    }
}
