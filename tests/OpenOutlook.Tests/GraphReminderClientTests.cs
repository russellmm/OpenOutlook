using System.Net;
using System.Text;
using System.Text.Json;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

/// <summary>Calendar reminders through Graph (docs/reminders-plan.md, step 1).</summary>
public sealed class GraphReminderClientTests
{
    private const string Page1 = """
        {"value":[
          {"eventId":"e2","changeKey":"ck2","eventSubject":"Dentist","eventLocation":{"displayName":"Main St"},
           "eventStartTime":{"dateTime":"2026-10-16T15:00:00.0000000","timeZone":"UTC"},"eventEndTime":{"dateTime":"2026-10-16T16:00:00.0000000","timeZone":"UTC"},
           "reminderFireTime":{"dateTime":"2026-10-16T14:45:00.0000000","timeZone":"UTC"},"eventWebLink":"https://outlook.live.com/owa/?itemid=e2"},
          {"changeKey":"no-event","eventSubject":"orphan","eventStartTime":{"dateTime":"2026-10-16T09:00:00","timeZone":"UTC"},"eventEndTime":{"dateTime":"2026-10-16T10:00:00","timeZone":"UTC"},"reminderFireTime":{"dateTime":"2026-10-16T08:45:00","timeZone":"UTC"}}
        ],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/reminderView(startDateTime='2026-09-16T00:00:00.0000000',endDateTime='2026-10-17T00:00:00.0000000')?$skip=2"}
        """;
    private const string Page2 = """
        {"value":[
          {"eventId":"e1","changeKey":"ck1","eventSubject":"test 2:25",
           "eventStartTime":{"dateTime":"2026-10-09T15:00:00.0000000","timeZone":"UTC"},"eventEndTime":{"dateTime":"2026-10-09T15:30:00.0000000","timeZone":"UTC"},
           "reminderFireTime":{"dateTime":"2026-10-09T14:45:00.0000000","timeZone":"UTC"}}
        ]}
        """;

    [Fact]
    public async Task Lists_undismissed_reminders_of_the_range_across_pages_oldest_first()
    {
        var gets = new List<Uri>();
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("access", request.Headers.Authorization?.Parameter);
            Assert.Contains("outlook.timezone=\"UTC\"", string.Join(",", request.Headers.GetValues("Prefer")));
            if (request.RequestUri!.AbsolutePath == "/v1.0/me") return Json("""{"id":"owner"}""");
            gets.Add(request.RequestUri);
            return Json(request.RequestUri.Query.Contains("skip") ? Page2 : Page1);
        }));
        var client = new GraphReminderClient(http, "owner");
        var start = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        var list = await client.ReminderViewAsync("access", start, start.AddDays(31));
        Assert.Equal(2, gets.Count);
        var first = Uri.UnescapeDataString(gets[0].AbsoluteUri);
        Assert.Contains("/v1.0/me/reminderView(startDateTime='2026-09-16T00:00:00.0000000',endDateTime='2026-10-17T00:00:00.0000000')", first);
        Assert.Equal(["e1", "e2"], list.Select(r => r.EventId).ToArray());                 // the orphan without an event id is skipped; oldest first
        var dentist = list[1];
        Assert.Equal("Dentist", dentist.Subject);
        Assert.Equal("Main St", dentist.Location);
        Assert.Equal("ck2", dentist.ChangeKey);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 14, 45, 0, TimeSpan.Zero), dentist.FireTime);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero), dentist.EventStart);
        Assert.Equal(TimeSpan.Zero, dentist.FireTime.Offset);
    }

    [Fact]
    public async Task Dismiss_and_snooze_post_to_the_event_and_a_deleted_event_reports_404()
    {
        var posts = new List<(string Path, string Body)>();
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1.0/me") return Json("""{"id":"owner"}""");
            Assert.Equal(HttpMethod.Post, request.Method);
            posts.Add((Uri.UnescapeDataString(request.RequestUri.AbsolutePath), request.Content!.ReadAsStringAsync().Result));
            return request.RequestUri.AbsolutePath.Contains("gone") ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var client = new GraphReminderClient(http, "owner");
        await client.DismissAsync("access", "AAMk/e1=");
        await client.SnoozeAsync("access", "e2", new DateTimeOffset(2026, 10, 16, 15, 5, 0, TimeSpan.FromHours(-7)));
        Assert.Equal("/v1.0/me/events/AAMk/e1=/dismissReminder", posts[0].Path);
        Assert.Equal("/v1.0/me/events/e2/snoozeReminder", posts[1].Path);
        using (var body = JsonDocument.Parse(posts[1].Body))
        {
            var time = body.RootElement.GetProperty("newReminderTime");
            Assert.Equal("2026-10-16T22:05:00", time.GetProperty("dateTime").GetString());       // 15:05 at -7 is 22:05 UTC
            Assert.Equal("UTC", time.GetProperty("timeZone").GetString());
        }
        var gone = await Assert.ThrowsAsync<GraphMailException>(() => client.DismissAsync("access", "gone"));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Refuses_another_account_unsafe_page_links_and_oversized_ranges_without_leaking_the_token()
    {
        {
            using var other = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/v1.0/me"
                ? Json("""{"id":"someone-else"}""") : throw new Xunit.Sdk.XunitException("must not get past the account check")));
            var error = await Assert.ThrowsAsync<GraphMailException>(() => new GraphReminderClient(other, "owner").DismissAsync("secret-token", "e1"));
            Assert.DoesNotContain("secret-token", error.Message);

            using var unsafeLink = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/v1.0/me"
                ? Json("""{"id":"owner"}""") : Json("""{"value":[],"@odata.nextLink":"https://evil.example/v1.0/me/reminderView"}""")));
            var start = DateTimeOffset.UtcNow;
            await Assert.ThrowsAsync<GraphMailException>(() => new GraphReminderClient(unsafeLink, "owner").ReminderViewAsync("t", start, start.AddDays(1)));
            await Assert.ThrowsAsync<ArgumentException>(() => new GraphReminderClient(unsafeLink, "owner").ReminderViewAsync("t", start, start.AddDays(101)));
            await Assert.ThrowsAsync<ArgumentException>(() => new GraphReminderClient(unsafeLink, "owner").ReminderViewAsync("t", start, start));
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
