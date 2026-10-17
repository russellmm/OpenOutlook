using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpenOutlook.Desktop;
using Xunit;

namespace OpenOutlook.HeadlessTests;

/// <summary>The Reminders window (docs/reminders-plan.md section 4).</summary>
public sealed class RemindersHeadlessTests
{
    private static ReminderItem Item(string id, string subject, DateTimeOffset start, string location = "") =>
        new("hotmail", id, subject, location, start, start.AddHours(1), start.AddMinutes(-15));

    [AvaloniaFact]
    public async Task Lists_due_reminders_with_overdue_text_and_dismiss_snooze_and_dismiss_all_act_on_the_right_ones()
    {
        var now = DateTimeOffset.UtcNow;
        var dentist = Item("e1", "Dentist", now.AddHours(-6), "Main St");
        var standup = Item("e2", "Stand-up", now.AddMinutes(10));
        var dismissed = new List<string>();
        var snoozed = new List<(string Id, TimeSpan Span)>();
        var asked = 0;
        var answer = false;
        var window = new RemindersWindow(
            items => { dismissed.AddRange(items.Select(i => i.EventId)); return Task.CompletedTask; },
            (items, span) => { snoozed.AddRange(items.Select(i => (i.EventId, span))); return Task.CompletedTask; },
            _ => { },
            count => { asked = count; return Task.FromResult(answer); });
        try
        {
            window.Show();
            window.SetItems([standup, dentist], now);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("2 Reminders", window.Title);
            Assert.Equal("Stand-up", window.HeaderSubject);                                   // the first is selected
            Assert.Equal("in 10 minutes", window.DueText(standup));
            Assert.Equal("6 hours overdue", window.DueText(dentist));
            Shot(window, "reminders-window");

            window.Select(dentist);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Dentist", window.HeaderSubject);
            Assert.StartsWith("Start time: ", window.HeaderWhen, StringComparison.Ordinal);
            await window.DismissSelectedAsync();
            Assert.Equal(["e1"], dismissed);

            window.Select(standup);
            window.ChooseSnooze(Array.FindIndex(RemindersWindow.SnoozeChoices, c => c.Label == "1 hour"));
            await window.SnoozeSelectedAsync();
            Assert.Equal([("e2", TimeSpan.FromHours(1))], snoozed);

            await window.DismissAllAsync();                                                   // asks first; "No" dismisses nothing
            Assert.Equal(2, asked);
            Assert.Equal(["e1"], dismissed);
            answer = true;
            await window.DismissAllAsync();
            Assert.Equal(["e1", "e2", "e1"], dismissed);

            window.Close();                                                                   // X hides; nothing is dismissed
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.HiddenByUser);
            Assert.False(window.IsVisible);
            Assert.Equal(3, dismissed.Count);
        }
        finally { window.CloseForGood(); }
    }

    [AvaloniaFact]
    public void One_reminder_reads_1_reminder_and_none_disables_the_buttons()
    {
        var window = new RemindersWindow(_ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => { });
        try
        {
            window.Show();
            window.SetItems([Item("e1", "Call", DateTimeOffset.UtcNow.AddMinutes(-1))], DateTimeOffset.UtcNow);
            Assert.Equal("1 Reminder", window.Title);
            window.SetItems([], DateTimeOffset.UtcNow);
            Assert.Equal("0 Reminders", window.Title);
            Assert.Equal("No reminders", window.HeaderSubject);
        }
        finally { window.CloseForGood(); }
    }

    [AvaloniaFact]
    public void The_appointment_reminder_box_offers_outlooks_choices_defaults_to_15_minutes_and_is_saved()
    {
        var editor = new CalendarEditorWindow(new DateOnly(2026, 10, 20), meeting: false);
        try
        {
            editor.Show();
            editor.SetSubject("Dentist");
            Assert.Equal(15, editor.Build()!.ReminderMinutesBeforeStart);
            editor.ChooseReminder("1 hour");
            Assert.Equal(60, editor.Build()!.ReminderMinutesBeforeStart);
            editor.ChooseReminder("None");
            Assert.Null(editor.Build()!.ReminderMinutesBeforeStart);
            Assert.Equal(["None", "0 minutes", "5 minutes", "10 minutes", "15 minutes", "30 minutes", "1 hour", "2 hours", "3 hours", "4 hours",
                "8 hours", "0.5 days", "1 day", "2 days", "1 week"], CalendarEditorWindow.ReminderChoices.Select(c => c.Label));
        }
        finally { editor.Close(); }
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
