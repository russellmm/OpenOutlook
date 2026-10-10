using System.Net;
using System.Text.Json;
using OpenOutlook.Auth;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

public sealed class GraphCalendarClientTests
{
    [Fact]
    public async Task ListsCalendarViewAndCreatesMeetingForVerifiedAccount()
    {
        var calls = new List<string>();
        JsonDocument? posted = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            Assert.Equal("access", request.Headers.Authorization?.Parameter);
            calls.Add(request.Method + " " + request.RequestUri.AbsolutePath);
            var path = request.RequestUri.AbsolutePath;
            if (path == "/v1.0/me") return Json(HttpStatusCode.OK, """{"id":"owner"}""");
            if (path == "/v1.0/me/calendars")
                return Json(HttpStatusCode.OK,
                    """{"value":[{"id":"calendar","name":"Calendar","isDefaultCalendar":true}]}""");
            if (path == "/v1.0/me/calendars/calendar/calendarView")
            {
                Assert.Contains("startDateTime=", request.RequestUri.Query);
                return Json(HttpStatusCode.OK,
                    """{"value":[{"id":"existing","subject":"Existing","start":{"dateTime":"2026-10-09T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-09T10:00:00","timeZone":"UTC"},"isAllDay":false,"isCancelled":false,"location":{"displayName":"Office"},"attendees":[]}]}""");
            }
            if (path == "/v1.0/me/calendars/calendar/events")
            {
                posted = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                return Json(HttpStatusCode.Created,
                    """{"id":"created","subject":"Meeting","start":{"dateTime":"2026-10-09T11:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-09T12:00:00","timeZone":"UTC"},"isAllDay":false}""");
            }
            throw new Exception("Unexpected Graph endpoint");
        }));
        var client = new GraphCalendarClient(http, "owner");
        Assert.Equal("Calendar", Assert.Single(await client.ListCalendarsAsync("access")).Name);
        var events = await client.GetEventsAsync("access", "calendar",
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("Existing", Assert.Single(events).Subject);
        var created = await client.CreateEventAsync("access", "calendar",
            new NewCalendarEvent("Meeting", new DateTimeOffset(2026, 10, 9, 11, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), false, "Office", "Agenda",
                ["person@example.test"], Guid.Parse("e71f4655-8d56-42d8-b53e-ef14c8a80aad")));
        Assert.Equal("created", created.Id);
        Assert.Equal("person@example.test", posted!.RootElement.GetProperty("attendees")[0]
            .GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal("e71f4655-8d56-42d8-b53e-ef14c8a80aad",
            posted.RootElement.GetProperty("transactionId").GetString());
        Assert.Equal("UTC", posted.RootElement.GetProperty("start").GetProperty("timeZone").GetString());
        Assert.Equal(3, calls.Count(path => path == "GET /v1.0/me"));
        Assert.Equal("POST /v1.0/me/calendars/calendar/events", calls[^1]);
        posted.Dispose();
    }

    [Fact]
    public async Task RefusesWrongAccountBeforeCreatingEvent()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(HttpStatusCode.OK, """{"id":"other"}"""));
        }));
        var item = new NewCalendarEvent("Appointment", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1), false, "", "", [], Guid.NewGuid());
        await Assert.ThrowsAsync<GraphMailException>(() =>
            new GraphCalendarClient(http, "owner").CreateEventAsync("access", "calendar", item));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AllDayEventUsesMidnightInItsCalendarTimeZone()
    {
        JsonDocument? posted = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1.0/me")
                return Json(HttpStatusCode.OK, """{"id":"owner"}""");
            posted = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            return Json(HttpStatusCode.Created,
                """{"id":"all-day","subject":"Vacation","start":{"dateTime":"2026-10-09T07:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-10T07:00:00","timeZone":"UTC"},"isAllDay":true}""");
        }));
        var client = new GraphCalendarClient(http, "owner");
        await client.CreateEventAsync("access", "calendar", new NewCalendarEvent("Vacation",
            new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.FromHours(-7)),
            new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.FromHours(-7)),
            true, "", "", [], Guid.NewGuid(), "Pacific Standard Time"));
        Assert.Equal("2026-10-09T00:00:00", posted!.RootElement.GetProperty("start")
            .GetProperty("dateTime").GetString());
        Assert.Equal("Pacific Standard Time", posted.RootElement.GetProperty("start")
            .GetProperty("timeZone").GetString());
        Assert.True(posted.RootElement.GetProperty("isAllDay").GetBoolean());
        posted.Dispose();
    }

    [Fact]
    public async Task RejectsCalendarPaginationToAnotherHost()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            return Task.FromResult(request.RequestUri!.AbsolutePath == "/v1.0/me"
                ? Json(HttpStatusCode.OK, """{"id":"owner"}""")
                : Json(HttpStatusCode.OK,
                    """{"value":[],"@odata.nextLink":"https://evil.example/v1.0/me/calendars"}"""));
        }));
        await Assert.ThrowsAsync<GraphMailException>(() =>
            new GraphCalendarClient(http, "owner").ListCalendarsAsync("access"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void CalendarWritePermissionIsMicrosoftOnlyAndRequiresReconnect()
    {
        var old = new ConnectedAccount(OAuthProvider.MicrosoftConsumers, "owner", "owner@hotmail.com",
            "client", DateTimeOffset.UtcNow, ["User.Read", "Mail.ReadWrite"]);
        Assert.False(old.CanWriteMicrosoftCalendar);
        Assert.True((old with { RequestedScopes = MicrosoftAccountPermissions.PlannedPersonalScopes })
            .CanWriteMicrosoftCalendar);
        Assert.False((old with { Provider = OAuthProvider.Google }).CanWriteMicrosoftCalendar);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this(request => Task.FromResult(respond(request))) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await respond(request);
            response.RequestMessage = request;
            return response;
        }
    }
}
