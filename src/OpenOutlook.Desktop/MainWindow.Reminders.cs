using Avalonia.Threading;
using OpenOutlook.Auth;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// Calendar reminders (docs/reminders-plan.md section 4): every 5 minutes, and once at start-up, Graph's reminderView is read for each Microsoft
/// account over the look-back (30 days) plus the day ahead; a second timer fires at the next reminder time between polls. Dismissals and snoozes
/// that Graph has not confirmed yet are sent again on each poll.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan ReminderPollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReminderLookAhead = TimeSpan.FromDays(1);
    private readonly ReminderStateStore _reminderStore = new();                                  // ~/.config/OpenOutlook/reminders-state.json, beside the other settings
    private readonly Dictionary<string, IReadOnlyList<ReminderItem>> _reminderServer = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, ReminderRecord> _reminderLocal = new Dictionary<string, ReminderRecord>();
    private readonly HashSet<string> _remindersAnnounced = new(StringComparer.Ordinal);
    private DispatcherTimer? _reminderPollTimer, _reminderFireTimer;
    private bool _reminderBusy;

    /// <summary>The reminders due now, newest first (what the Reminders window lists).</summary>
    internal IReadOnlyList<ReminderItem> DueReminders { get; private set; } = [];

    private TimeSpan ReminderLookBack => ReminderSchedule.DefaultLookBack;

    private void StartReminderScheduler()
    {
        if (Environment.GetEnvironmentVariable("OPENOUTLOOK_NO_MIRROR") == "1") return;     // tests must not touch real calendars
        _reminderLocal = _reminderStore.Load();
        _reminderPollTimer = new DispatcherTimer { Interval = ReminderPollInterval };
        _reminderPollTimer.Tick += async (_, _) => await RefreshRemindersAsync();
        _reminderPollTimer.Start();
        _reminderFireTimer = new DispatcherTimer();
        _reminderFireTimer.Tick += (_, _) => { _reminderFireTimer.Stop(); EvaluateReminders(); };
        _ = RefreshRemindersAsync();
    }

    private IReadOnlyList<ConnectedAccount> ReminderAccounts()
    {
        try { return _accountRegistry.Load().Where(a => a.CanWriteMicrosoftCalendar).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return []; }
    }

    /// <summary>Reads every account's reminders from Graph, retries unconfirmed dismissals and snoozes, then re-evaluates. Never throws.</summary>
    internal async Task RefreshRemindersAsync()
    {
        if (_reminderBusy) return;
        _reminderBusy = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var accounts = ReminderAccounts();
            foreach (var gone in _reminderServer.Keys.Except(accounts.Select(a => a.AccountId)).ToList()) _reminderServer.Remove(gone);
            foreach (var account in accounts)
            {
                try
                {
                    var token = await GetMicrosoftSession(account).GetAccessTokenAsync();
                    var client = new GraphReminderClient(_graphHttp, account.AccountId);
                    await SyncPendingRemindersAsync(account, client, token);
                    var list = await client.ReminderViewAsync(token, now - ReminderLookBack, now + ReminderLookAhead);
                    _reminderServer[account.AccountId] = list.Select(r => ReminderItem.From(account.AccountId, r)).ToList();
                }
                catch (Exception e)                                            // background work: a failed sign-in refresh or network error is logged, never thrown
                { AppLog.Error("reminders", e, $"reading reminders of {account.DisplayAddress} failed; the last list is kept"); }
            }
            EvaluateReminders();
        }
        finally { _reminderBusy = false; }
    }

    /// <summary>Sends dismissals and snoozes made while Graph could not be reached. A deleted event (404) counts as done.</summary>
    private async Task SyncPendingRemindersAsync(ConnectedAccount account, GraphReminderClient client, string token)
    {
        var done = new List<ReminderRecord>();
        foreach (var record in _reminderLocal.Values.Where(r => r.PendingSync && r.AccountId == account.AccountId).ToList())
        {
            try
            {
                if (record.State == ReminderRecord.Dismissed) await client.DismissAsync(token, record.EventId);
                else if (record.SnoozedUntil is { } until && until > DateTimeOffset.UtcNow) await client.SnoozeAsync(token, record.EventId, until);
                done.Add(record with { PendingSync = false });
            }
            catch (GraphMailException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { done.Add(record with { PendingSync = false }); }
        }
        if (done.Count > 0) SaveReminderRecords(done);
    }

    private void SaveReminderRecords(IEnumerable<ReminderRecord> records)
    {
        try { _reminderLocal = _reminderStore.Upsert(records, DateTimeOffset.UtcNow, ReminderLookBack); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var merged = new Dictionary<string, ReminderRecord>(_reminderLocal);                    // still honoured for this run
            foreach (var r in records) merged[r.Key] = r;
            _reminderLocal = merged;
            AppLog.Error("reminders", ex, "could not save reminders-state.json");
        }
    }

    /// <summary>Works out what is due from the last server lists and the local records, and arms the timer for the next reminder.</summary>
    private void EvaluateReminders()
    {
        var now = DateTimeOffset.UtcNow;
        var result = ReminderSchedule.Evaluate(_reminderServer.Values.SelectMany(l => l), _reminderLocal, now, ReminderLookBack);
        DueReminders = result.Due;
        var fresh = result.Due.Where(i => _remindersAnnounced.Add(i.Key)).ToList();
        if (fresh.Count > 0)
            AppLog.Note("reminders", $"{fresh.Count} reminder(s) due: " + string.Join("; ", fresh.Take(20).Select(i =>
                $"\"{i.Subject}\" starts {i.EventStart.ToLocalTime():yyyy-MM-dd HH:mm} ({ReminderSchedule.DueText(i.EventStart, now)})")));
        if (_reminderFireTimer is not null)
        {
            _reminderFireTimer.Stop();
            if (result.NextFire is { } next && next - now < ReminderPollInterval)
            {
                _reminderFireTimer.Interval = (next - now) + TimeSpan.FromSeconds(1);
                _reminderFireTimer.Start();
            }
        }
        OnRemindersChanged(fresh.Count > 0);
    }

    /// <summary>Dismisses reminders: recorded here first (gone at once, also offline), then sent to Graph.</summary>
    internal Task DismissRemindersAsync(IEnumerable<ReminderItem> items) =>
        ChangeRemindersAsync(items.Select(i => ReminderSchedule.Dismiss(i, DateTimeOffset.UtcNow)).ToList());

    /// <summary>Snoozes reminders for <paramref name="span"/>: recorded here first, then sent to Graph.</summary>
    internal Task SnoozeRemindersAsync(IEnumerable<ReminderItem> items, TimeSpan span) =>
        ChangeRemindersAsync(items.Select(i => ReminderSchedule.Snooze(i, span, DateTimeOffset.UtcNow)).ToList());

    private async Task ChangeRemindersAsync(IReadOnlyList<ReminderRecord> records)
    {
        if (records.Count == 0) return;
        SaveReminderRecords(records);
        foreach (var r in records) _remindersAnnounced.Remove(r.Key);                                 // a snoozed one is announced again when it returns
        EvaluateReminders();
        foreach (var account in ReminderAccounts().Where(a => records.Any(r => r.AccountId == a.AccountId)))
        {
            try { await SyncPendingRemindersAsync(account, new GraphReminderClient(_graphHttp, account.AccountId), await GetMicrosoftSession(account).GetAccessTokenAsync()); }
            catch (Exception e)                                            // background work: a failed sign-in refresh or network error is logged, never thrown
            { AppLog.Error("reminders", e, $"reminder change for {account.DisplayAddress} not confirmed yet; it is sent again on the next check"); }
        }
    }

    private RemindersWindow? _remindersWindow;

    /// <summary>Keeps the Reminders window in step with the due list: shown (again, also after X) when a reminder newly comes due, hidden when none is left.</summary>
    private void OnRemindersChanged(bool newlyDue)
    {
        if (DueReminders.Count == 0)
        {
            if (_remindersWindow is { IsVisible: true }) _remindersWindow.Hide();
            _remindersWindow?.SetItems([], DateTimeOffset.UtcNow);
            return;
        }
        _remindersWindow ??= new RemindersWindow(items => DismissRemindersAsync(items), (items, span) => SnoozeRemindersAsync(items, span), OpenReminderItem);
        _remindersWindow.SetItems(DueReminders, DateTimeOffset.UtcNow);
        if (newlyDue && !_remindersWindow.IsVisible)
        {
            _remindersWindow.Show();
            AppLog.Note("reminders", $"Reminders window shown with {DueReminders.Count} reminder(s)");
        }
    }

    /// <summary>Open Item: the calendar of the reminder's account on the event's day.</summary>
    private void OpenReminderItem(ReminderItem item)
    {
        ShowCalendarClicked(this, new Avalonia.Interactivity.RoutedEventArgs());
        _calendarPage?.SelectAccount(item.AccountId);
        _calendarPage?.GoToDay(DateOnly.FromDateTime(item.EventStart.ToLocalTime().DateTime));
        Activate();
    }

    private void CloseRemindersWindow()
    {
        _reminderPollTimer?.Stop();
        _reminderFireTimer?.Stop();
        _remindersWindow?.CloseForGood();
    }
}
