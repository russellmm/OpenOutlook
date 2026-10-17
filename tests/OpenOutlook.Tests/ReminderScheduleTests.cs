using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

/// <summary>Which reminders show and when (docs/reminders-plan.md sections 2 and 3; owner: missed ones must show, dismissed never again).</summary>
public sealed class ReminderScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 21, 30, 0, TimeSpan.Zero);          // 2:30 PM Pacific daylight time
    private static readonly IReadOnlyDictionary<string, ReminderRecord> None = new Dictionary<string, ReminderRecord>();

    private static ReminderItem Item(string id, DateTimeOffset start, int minutesBefore = 15, string account = "hotmail") =>
        new(account, id, "Subject " + id, "", start, start.AddHours(1), start.AddMinutes(-minutesBefore));

    [Fact]
    public void A_reminder_that_came_due_while_openoutlook_was_closed_for_a_week_shows_at_start()
    {
        var weekOld = Item("week", Now.AddDays(-6));
        var result = ReminderSchedule.Evaluate([weekOld], None, Now, ReminderSchedule.DefaultLookBack);
        Assert.Equal(["week"], result.Due.Select(i => i.EventId));
        Assert.Equal("6 days overdue", ReminderSchedule.DueText(weekOld.EventStart, Now));
    }

    [Fact]
    public void A_reminder_older_than_the_look_back_is_ignored_and_one_still_ahead_sets_the_next_fire_time()
    {
        var ancient = Item("ancient", Now.AddDays(-45));
        var soon = Item("soon", Now.AddMinutes(25));                                                   // fires in 10 minutes
        var result = ReminderSchedule.Evaluate([ancient, soon], None, Now, ReminderSchedule.DefaultLookBack);
        Assert.Empty(result.Due);
        Assert.Equal(soon.FireTime, result.NextFire);
        Assert.Equal("in 25 minutes", ReminderSchedule.DueText(soon.EventStart, Now));
    }

    [Fact]
    public void A_dismissed_reminder_never_shows_again_even_while_the_server_still_lists_it()
    {
        var item = Item("e1", Now.AddHours(-6));
        var dismissed = ReminderSchedule.Dismiss(item, Now);
        Assert.True(dismissed.PendingSync);                                                               // not confirmed by Graph yet (offline)
        var local = new Dictionary<string, ReminderRecord> { [dismissed.Key] = dismissed };
        Assert.Empty(ReminderSchedule.Evaluate([item], local, Now, ReminderSchedule.DefaultLookBack).Due);
        Assert.Empty(ReminderSchedule.Evaluate([item], local, Now.AddDays(3), ReminderSchedule.DefaultLookBack).Due);   // restarts later: still gone
    }

    [Fact]
    public void A_moved_event_shows_again_and_the_next_occurrence_of_a_series_is_not_hidden_by_an_earlier_dismissal()
    {
        var first = Item("series", Now.AddDays(-1));
        var local = new Dictionary<string, ReminderRecord> { [first.Key] = ReminderSchedule.Dismiss(first, Now) };
        var moved = first with { EventStart = Now.AddMinutes(-5), FireTime = Now.AddMinutes(-20) };        // same event id, new reminder time
        var due = ReminderSchedule.Evaluate([first, moved], local, Now, ReminderSchedule.DefaultLookBack).Due;
        Assert.Equal([moved.Key], due.Select(i => i.Key));
    }

    [Fact]
    public void A_snoozed_reminder_hides_until_its_time_then_shows_again()
    {
        var item = Item("e1", Now.AddMinutes(-2));
        var snooze = ReminderSchedule.Snooze(item, TimeSpan.FromMinutes(5), Now);
        var local = new Dictionary<string, ReminderRecord> { [snooze.Key] = snooze };
        var now = ReminderSchedule.Evaluate([item], local, Now, ReminderSchedule.DefaultLookBack);
        Assert.Empty(now.Due);
        Assert.Equal(Now.AddMinutes(5), now.NextFire);
        Assert.Single(ReminderSchedule.Evaluate([item], local, Now.AddMinutes(6), ReminderSchedule.DefaultLookBack).Due);
    }

    [Fact]
    public void Due_reminders_come_newest_first_and_each_account_counts_separately()
    {
        var older = Item("a", Now.AddHours(-3));
        var newer = Item("b", Now.AddMinutes(-5));
        var sameIdOtherAccount = Item("a", Now.AddHours(-3), account: "work");
        var due = ReminderSchedule.Evaluate([older, newer, sameIdOtherAccount, older], None, Now, ReminderSchedule.DefaultLookBack).Due;
        Assert.Equal(3, due.Count);                                                                         // the duplicate "older" counts once
        Assert.Equal("b", due[0].EventId);
    }

    [Theory]
    [InlineData(0, "Now")]
    [InlineData(30, "Now")]                                                                                 // under a minute is "Now"
    [InlineData(-61, "in 1 minute")]
    [InlineData(6 * 3600, "6 hours overdue")]
    [InlineData(47 * 3600, "47 hours overdue")]
    [InlineData(3 * 86400, "3 days overdue")]
    [InlineData(15 * 86400, "2 weeks overdue")]
    public void Due_text_reads_like_outlooks(int secondsSinceStart, string expected)
    {
        Assert.Equal(expected, ReminderSchedule.DueText(Now.AddSeconds(-secondsSinceStart), Now));
    }

    [Fact]
    public void The_record_file_survives_restarts_reads_damage_as_empty_and_stays_bounded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oo-rem-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ReminderStateStore(dir);
            Assert.Empty(store.Load());
            var item = Item("e1", Now.AddHours(-1));
            store.Upsert([ReminderSchedule.Dismiss(item, Now)], Now, ReminderSchedule.DefaultLookBack);
            var again = new ReminderStateStore(dir).Load();
            Assert.Equal(ReminderRecord.Dismissed, again[item.Key].State);
            var many = Enumerable.Range(0, 2500).Select(i => ReminderSchedule.Dismiss(Item("m" + i, Now.AddMinutes(-i)), Now.AddSeconds(-i)) with { PendingSync = false });
            Assert.Equal(ReminderSchedule.MaxRecords, store.Upsert(many, Now, ReminderSchedule.DefaultLookBack).Count);
            var stale = ReminderSchedule.Dismiss(Item("stale", Now.AddDays(-60)), Now.AddDays(-60)) with { PendingSync = false };
            Assert.DoesNotContain(stale.Key, ReminderSchedule.Prune([stale], Now, ReminderSchedule.DefaultLookBack).Select(r => r.Key));
            File.WriteAllText(store.Path, "{ not json");
            Assert.Empty(store.Load());                                                                     // damage never stops start-up
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
