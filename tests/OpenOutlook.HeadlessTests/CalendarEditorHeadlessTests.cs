using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpenOutlook.Auth;
using OpenOutlook.Desktop;
using OpenOutlook.Providers.Microsoft;
using Xunit;

namespace OpenOutlook.HeadlessTests;

/// <summary>The appointment window like Outlook's, and opening / changing / deleting events (owner 2026-10-17).</summary>
public sealed class CalendarEditorHeadlessTests
{
    private static GraphEventDetails Details(bool organizer = true, string type = "singleInstance") => new(
        "ev1", "Dentist", new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 20, 17, 30, 0, TimeSpan.Zero), false,
        new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20), "Main St", "Bring the card.", [], true, 1080, "tentative", "private", "high", organizer, type,
        organizer ? "rmarrash@hotmail.com" : "boss@example.org");

    [AvaloniaFact]
    public void A_new_appointment_has_outlooks_boxes_the_end_follows_the_start_and_the_options_are_saved()
    {
        var editor = new CalendarEditorWindow(new DateOnly(2026, 10, 17), meeting: false);
        try
        {
            editor.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(OutlookDateBox.Format(new DateOnly(2026, 10, 17)), editor.StartDateBox.Shown);
            Assert.Equal((new TimeOnly(9, 0), new TimeOnly(9, 30)), (editor.StartTimeBox.Time, editor.EndTimeBox.Time));    // half an hour, as in Outlook
            editor.StartTimeBox.Type("2:00 PM");                                                        // the end moves with the start
            Assert.Equal(new TimeOnly(14, 30), editor.EndTimeBox.Time);
            editor.EndTimeBox.Type("4 pm");                                                             // a longer meeting ...
            editor.StartTimeBox.Type("10:00");                                                          // ... keeps its length
            Assert.Equal(new TimeOnly(12, 0), editor.EndTimeBox.Time);
            editor.StartTimeBox.OpenList();                                                             // the list stays open (it closed at once before)
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.StartTimeBox.IsListOpen);
            Assert.Equal(new TimeOnly(10, 0), editor.StartTimeBox.Time);                               // highlighting the current time takes nothing
            editor.StartTimeBox.Highlight(22);                                                          // 11:00 AM, as with the arrow keys ...
            Assert.True(editor.StartTimeBox.IsListOpen);
            editor.StartTimeBox.AcceptHighlighted();                                                    // ... then Enter / a click
            Assert.False(editor.StartTimeBox.IsListOpen);
            Assert.Equal((new TimeOnly(11, 0), new TimeOnly(13, 0)), (editor.StartTimeBox.Time, editor.EndTimeBox.Time));
            editor.StartTimeBox.Type("10:00");
            editor.EndTimeBox.OpenList();
            Assert.True(editor.EndTimeBox.IsListOpen);
            Assert.Contains(OutlookTimeBox.Format(new TimeOnly(12, 0)) + " (2 hours)", editor.EndTimeBox.ListLabels);      // in the computer's time format
            Assert.Equal(OutlookTimeBox.Format(new TimeOnly(10, 0)) + " (0 minutes)", editor.EndTimeBox.ListLabels[0]);
            editor.StartDateBox.Type("10/24/2026");
            Assert.Equal(new DateOnly(2026, 10, 24), editor.EndDateBox.Date);
            editor.StartDateBox.Type("not a date");                                                     // ignored: back to the last date
            Assert.Equal(new DateOnly(2026, 10, 24), editor.StartDateBox.Date);
            Shot(editor, "appointment-window");

            editor.SetSubject("Dentist");
            editor.ChooseShowAs("oof");
            editor.SetPrivate(true);
            editor.SetImportance("high");
            var item = editor.Build()!;
            Assert.Equal(("oof", "private", "high"), (item.ShowAs, item.Sensitivity, item.Importance));
            Assert.Equal(TimeSpan.FromHours(2), item.End - item.Start);
        }
        finally { editor.Close(); }
    }

    [AvaloniaFact]
    public void An_existing_event_opens_with_its_values_and_a_meeting_from_someone_else_is_read_only()
    {
        var editor = new CalendarEditorWindow(Details(type: "occurrence"));
        try
        {
            editor.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Dentist - Appointment", editor.Title);
            Assert.Equal(TimeOnly.FromDateTime(Details().Start.ToLocalTime().DateTime), editor.StartTimeBox.Time);
            var item = editor.Build()!;
            Assert.Equal(1080, item.ReminderMinutesBeforeStart);                                      // 18 hours: added to the list, as Outlook shows it
            Assert.Equal(("tentative", "private", "high"), (item.ShowAs, item.Sensitivity, item.Importance));
            Assert.Equal(TimeSpan.FromMinutes(90), item.End - item.Start);
            Assert.Contains("this occurrence only", editor.StatusText);
            Assert.False(editor.BodyChanged);
            editor.SetBody("Bring the card and the form.");
            Assert.True(editor.BodyChanged);
        }
        finally { editor.Close(); }

        var other = new CalendarEditorWindow(Details(organizer: false) with { Attendees = ["rmarrash@hotmail.com"] });
        try
        {
            other.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(other.IsReadOnly);
            Assert.EndsWith("(read only)", other.Title);
            Assert.Contains("boss@example.org", other.StatusText);
            other.RequestDelete();
            Assert.True(other.DeleteRequested);
        }
        finally { other.Close(); }
    }

    [AvaloniaFact]
    public async Task Double_clicking_an_event_opens_it_and_saving_or_deleting_reaches_graph()
    {
        var when = DateTime.Today.AddHours(9).ToUniversalTime();
        var list = System.Text.Json.JsonSerializer.Serialize(new
        {
            value = new[] { new
            {
                id = "ev1", subject = "Dentist", start = new { dateTime = when.ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
                end = new { dateTime = when.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
                isAllDay = false, isCancelled = false, location = new { displayName = "" }, attendees = Array.Empty<object>()
            } }
        });
        var details = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "ev1", subject = "Dentist", isAllDay = false, type = "singleInstance", isOrganizer = true, isReminderOn = true, reminderMinutesBeforeStart = 15,
            showAs = "busy", sensitivity = "normal", importance = "normal",
            start = new { dateTime = when.ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" }, end = new { dateTime = when.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
            location = new { displayName = "" }, body = new { contentType = "text", content = "" }, attendees = Array.Empty<object>(),
        });
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get) calls.Add(request.Method.Method + " " + path);
            var response = (request.Method.Method, path) switch
            {
                (_, "/v1.0/me") => Json("""{"id":"owner"}"""),
                (_, "/v1.0/me/calendars") => Json("""{"value":[{"id":"calendar","name":"Calendar","isDefaultCalendar":true,"canEdit":true}]}"""),
                (_, "/v1.0/me/calendars/calendar/calendarView") => Json(list),
                ("GET", "/v1.0/me/events/ev1") => Json(details),
                ("PATCH", "/v1.0/me/events/ev1") => Json(details.Replace("\"Dentist\"", "\"Dentist (moved)\"")),
                ("DELETE", "/v1.0/me/events/ev1") => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new Exception("Unexpected Graph endpoint " + path)
            };
            response.RequestMessage = request;
            return response;
        }));
        var owner = new Window { Width = 1200, Height = 800 };
        var page = new CalendarPage(owner, http, _ => Task.FromResult("access"), _ => Task.CompletedTask);
        owner.Content = page;
        page.SetAccounts([new ConnectedAccount(OAuthProvider.MicrosoftConsumers, "owner", "person@hotmail.test", "client", DateTimeOffset.UtcNow, MicrosoftAccountPermissions.PlannedPersonalScopes)]);
        owner.Show();
        try
        {
            await page.ActivateAsync();
            var saved = 0;
            page.EventSaved += () => saved++;
            var item = new GraphCalendarEvent("ev1", "Dentist", when, when.AddHours(1), false, "", false);
            page.EditorOverride = editor => { editor.SetSubject("Dentist (moved)"); return Task.FromResult(editor.Build()); };
            await page.OpenEventAsync(item);
            Assert.Equal(["PATCH /v1.0/me/events/ev1"], calls);
            page.EditorOverride = editor => { editor.RequestDelete(); return Task.FromResult<NewCalendarEvent?>(null); };
            await page.OpenEventAsync(item);
            Assert.Equal(["PATCH /v1.0/me/events/ev1", "DELETE /v1.0/me/events/ev1"], calls);
            Assert.Equal(2, saved);                                                                    // the reminder check reads Graph again
            page.EditorOverride = _ => Task.FromResult<NewCalendarEvent?>(null);                       // Cancel: nothing sent
            await page.OpenEventAsync(item);
            Assert.Equal(2, calls.Count);
        }
        finally { page.Deactivate(); owner.Close(); }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private static void Shot(Window w, string name)
    {
        var dir = Environment.GetEnvironmentVariable("OO_HEADLESS_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        w.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        w.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"));
    }
}
