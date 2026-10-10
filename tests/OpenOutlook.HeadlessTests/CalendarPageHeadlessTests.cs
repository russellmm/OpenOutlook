using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using OpenOutlook.Auth;
using OpenOutlook.Desktop;
using Xunit;

namespace OpenOutlook.HeadlessTests;

public sealed class CalendarPageHeadlessTests
{
    [AvaloniaFact]
    public async Task ShowsGraphEventForMicrosoftAccountAndExcludesGmail()
    {
        var when = DateTime.Today.AddHours(9).ToUniversalTime();
        var after = when.AddHours(1);
        var eventJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            value = new[] { new
            {
                id = "synthetic-event", subject = "Synthetic appointment",
                start = new { dateTime = when.ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
                end = new { dateTime = after.ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
                isAllDay = false, isCancelled = false, location = new { displayName = "" }, attendees = Array.Empty<object>()
            } }
        });
        using var http = new HttpClient(new Handler(request =>
        {
            var response = request.RequestUri!.AbsolutePath switch
            {
                "/v1.0/me" => Json("""{"id":"owner"}"""),
                "/v1.0/me/calendars" => Json(
                    """{"value":[{"id":"calendar","name":"Calendar","isDefaultCalendar":true,"canEdit":true}]}"""),
                "/v1.0/me/calendars/calendar/calendarView" => Json(eventJson),
                _ => throw new Exception("Unexpected Graph endpoint")
            };
            response.RequestMessage = request;
            return response;
        }));
        var owner = new Window { Width = 1200, Height = 800 };
        var page = new CalendarPage(owner, http, _ => Task.FromResult("access"), _ => Task.CompletedTask);
        owner.Content = page;
        page.SetAccounts([
            new ConnectedAccount(OAuthProvider.Google, "gmail", "person@gmail.test", "client",
                DateTimeOffset.UtcNow),
            new ConnectedAccount(OAuthProvider.MicrosoftConsumers, "owner", "person@hotmail.test", "client",
                DateTimeOffset.UtcNow, MicrosoftAccountPermissions.PlannedPersonalScopes)
        ]);
        owner.Show();
        try
        {
            await page.ActivateAsync();
            owner.UpdateLayout();
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text?.Contains("Synthetic appointment") == true);
            Assert.Single(page.GetVisualDescendants().OfType<ComboBox>().First().Items);
        }
        finally { page.Deactivate(); owner.Close(); }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
