using System.Net;
using System.Text;
using System.Text.Json;
using OpenOutlook.Desktop;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

/// <summary>Opening, changing and deleting calendar events; Outlook's date and time boxes (owner 2026-10-17).</summary>
public sealed class CalendarEditingTests
{
    private const string Details = """
        {"id":"ev1","subject":"Dentist","isAllDay":false,"type":"occurrence","isOrganizer":true,"isReminderOn":true,"reminderMinutesBeforeStart":1080,
         "showAs":"tentative","sensitivity":"private","importance":"high",
         "start":{"dateTime":"2026-10-20T16:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-10-20T17:00:00.0000000","timeZone":"UTC"},
         "location":{"displayName":"Main St"},"body":{"contentType":"text","content":"Bring the card.\r\nCall first."},
         "attendees":[{"emailAddress":{"address":"jessica.marrash@gmail.com"}}],"organizer":{"emailAddress":{"name":"Russell","address":"rmarrash@hotmail.com"}}}
        """;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { var r = respond(request); r.RequestMessage = request; return Task.FromResult(r); }
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task An_event_opens_with_its_fields_body_lines_attendees_and_kind()
    {
        string prefer = "";
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1.0/me") return Json("""{"id":"acct"}""");
            prefer = string.Join(",", request.Headers.GetValues("Prefer"));
            Assert.Equal("/v1.0/me/events/ev1", request.RequestUri.AbsolutePath);
            return Json(Details);
        }));
        var e = await new GraphCalendarClient(http, "acct").GetEventAsync("t", "ev1");
        Assert.Contains("outlook.body-content-type=\"text\"", prefer);
        Assert.Equal(("Dentist", "Main St", "Bring the card.\r\nCall first."), (e.Subject, e.Location, e.Body));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero), e.Start);
        Assert.Equal(["jessica.marrash@gmail.com"], e.Attendees);
        Assert.Equal((true, 1080, "tentative", "private", "high"), (e.IsReminderOn, e.ReminderMinutesBeforeStart, e.ShowAs, e.Sensitivity, e.Importance));
        Assert.True(e.IsOccurrence);
        Assert.Equal("rmarrash@hotmail.com", e.Organizer);
    }

    [Fact]
    public async Task All_day_events_keep_their_calendar_dates()
    {
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/v1.0/me" ? Json("""{"id":"acct"}""") : Json("""
            {"id":"ev2","subject":"Holiday","isAllDay":true,"start":{"dateTime":"2026-10-20T00:00:00.0000000","timeZone":"UTC"},
             "end":{"dateTime":"2026-10-22T00:00:00.0000000","timeZone":"UTC"}}
            """)));
        var e = await new GraphCalendarClient(http, "acct").GetEventAsync("t", "ev2");
        Assert.Equal((new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 22)), (e.StartDate, e.EndDate));
        Assert.False(e.IsOccurrence);
        Assert.Equal("busy", e.ShowAs);
    }

    [Fact]
    public async Task Saving_patches_only_what_should_change_and_delete_removes_the_event()
    {
        var calls = new List<(string Method, string Path, string? Body)>();
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1.0/me") return Json("""{"id":"acct"}""");
            calls.Add((request.Method.Method, request.RequestUri.AbsolutePath, request.Content?.ReadAsStringAsync().Result));
            return request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(Details);
        }));
        var client = new GraphCalendarClient(http, "acct");
        var start = new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero);
        var draft = new NewCalendarEvent("Dentist (moved)", start, start.AddHours(1), false, "Main St", "Bring the card.", ["jessica.marrash@gmail.com"], Guid.NewGuid(),
            "UTC", 30, "oof", "private", "low");
        await client.UpdateEventAsync("t", "ev1", draft, bodyChanged: false, attendeesChanged: false);
        await client.UpdateEventAsync("t", "ev1", draft, bodyChanged: true, attendeesChanged: true);
        await client.DeleteEventAsync("t", "ev1");
        Assert.Equal([("PATCH", "/v1.0/me/events/ev1"), ("PATCH", "/v1.0/me/events/ev1"), ("DELETE", "/v1.0/me/events/ev1")], calls.Select(c => (c.Method, c.Path)));
        using (var first = JsonDocument.Parse(calls[0].Body!))
        {
            var root = first.RootElement;
            Assert.Equal("Dentist (moved)", root.GetProperty("subject").GetString());
            Assert.Equal(("oof", "private", "low"), (root.GetProperty("showAs").GetString(), root.GetProperty("sensitivity").GetString(), root.GetProperty("importance").GetString()));
            Assert.Equal(30, root.GetProperty("reminderMinutesBeforeStart").GetInt32());
            Assert.False(root.TryGetProperty("body", out _));                                          // a formatted body is not overwritten by plain text
            Assert.False(root.TryGetProperty("attendees", out _));
            Assert.False(root.TryGetProperty("transactionId", out _));
        }
        using (var second = JsonDocument.Parse(calls[1].Body!))
        {
            Assert.Equal("Bring the card.", second.RootElement.GetProperty("body").GetProperty("content").GetString());
            Assert.Equal(1, second.RootElement.GetProperty("attendees").GetArrayLength());
        }
        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateEventAsync("t", "ev1", draft with { ShowAs = "away" }, false, false));
    }

    [Theory]
    [InlineData("8:15 AM", 8, 15)]
    [InlineData("8:15pm", 20, 15)]
    [InlineData("20:30", 20, 30)]
    [InlineData("8", 8, 0)]
    [InlineData("8p", 20, 0)]
    [InlineData("830", 8, 30)]
    [InlineData("12 am", 0, 0)]
    [InlineData("9:30 AM (30 minutes)", 9, 30)]
    public void Typed_times_are_read_like_outlook(string typed, int hour, int minute) =>
        Assert.Equal(new TimeOnly(hour, minute), OutlookTimeBox.Parse(typed));

    [Fact]
    public void The_time_list_has_half_hours_and_the_end_list_durations()
    {
        Assert.Equal(48, OutlookTimeBox.Entries(null).Count);
        var end = OutlookTimeBox.Entries(new TimeOnly(8, 0));
        Assert.Equal(new TimeOnly(8, 0), end[0].Time);
        Assert.EndsWith("(0 minutes)", end[0].Label);
        Assert.EndsWith("(30 minutes)", end[1].Label);
        Assert.EndsWith("(1 hour)", end[2].Label);
        Assert.EndsWith("(1.5 hours)", end[3].Label);
        Assert.Null(OutlookTimeBox.Parse("not a time"));
    }

    [Fact]
    public void Dates_show_like_outlook_and_typed_dates_are_read()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            Assert.Equal("Sat 10/17/2026", OutlookDateBox.Format(new DateOnly(2026, 10, 17)));
            var today = new DateOnly(2026, 10, 17);
            Assert.Equal(new DateOnly(2026, 10, 20), OutlookDateBox.Parse("Tue 10/20/2026", today));
            Assert.Equal(new DateOnly(2026, 10, 20), OutlookDateBox.Parse("10/20", today));
            Assert.Equal(new DateOnly(2026, 10, 20), OutlookDateBox.Parse("2026-10-20", today));
            Assert.Null(OutlookDateBox.Parse("someday", today));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }
}
