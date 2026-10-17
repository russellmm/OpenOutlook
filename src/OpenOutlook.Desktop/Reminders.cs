using System.Text.Json;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>One reminder of one event occurrence of one account (docs/reminders-plan.md).</summary>
public sealed record ReminderItem(string AccountId, string EventId, string Subject, string Location,
    DateTimeOffset EventStart, DateTimeOffset EventEnd, DateTimeOffset FireTime)
{
    /// <summary>
    /// What "this reminder" means: account + event + reminder time. A dismissed reminder stays dismissed; if the event is moved, Graph reports a new
    /// fire time, the key changes and it shows again (as in Outlook). A snooze done through Graph also comes back with a new fire time.
    /// </summary>
    public string Key => ReminderSchedule.KeyOf(AccountId, EventId, FireTime);

    public static ReminderItem From(string accountId, GraphReminder r) =>
        new(accountId, r.EventId, r.Subject, r.Location, r.EventStart, r.EventEnd, r.FireTime);
}

/// <summary>What the user did with a reminder on this computer. <see cref="PendingSync"/>: Graph has not confirmed it yet (offline, or the call failed).</summary>
public sealed record ReminderRecord(string Key, string AccountId, string EventId, DateTimeOffset FireTime, string State,
    DateTimeOffset? SnoozedUntil, DateTimeOffset RecordedAt, bool PendingSync)
{
    public const string Dismissed = "dismissed";
    public const string Snoozed = "snoozed";
}

/// <summary>
/// Decides which reminders are due (docs/reminders-plan.md sections 2 and 3). Kept free of controls and of the network, so the rules that matter to the
/// owner are unit tested: a reminder that came due while OpenOutlook was closed (up to the look-back) shows at start-up; a dismissed one never shows
/// again, also before Graph has confirmed the dismissal; a snoozed one shows again at its snooze time.
/// </summary>
public static class ReminderSchedule
{
    public static readonly TimeSpan DefaultLookBack = TimeSpan.FromDays(30);
    public const int MaxRecords = 2000;

    public sealed record Result(IReadOnlyList<ReminderItem> Due, DateTimeOffset? NextFire);

    public static string KeyOf(string accountId, string eventId, DateTimeOffset fireTime) =>
        accountId + "|" + eventId + "|" + fireTime.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The reminders to show now (newest due first) and the next time something becomes due.</summary>
    public static Result Evaluate(IEnumerable<ReminderItem> server, IReadOnlyDictionary<string, ReminderRecord> local, DateTimeOffset now, TimeSpan lookBack)
    {
        var oldest = now - lookBack;
        var due = new List<ReminderItem>();
        DateTimeOffset? next = null;
        void Next(DateTimeOffset t) { if (t > now && (next is null || t < next)) next = t; }
        foreach (var item in server.DistinctBy(i => i.Key))
        {
            if (item.FireTime < oldest) continue;                                   // older than the look-back: ignored, a long absence does not flood the window
            if (local.TryGetValue(item.Key, out var record))
            {
                if (record.State == ReminderRecord.Dismissed) continue;            // dismissed here: never again, confirmed by Graph or not
                if (record.State == ReminderRecord.Snoozed && record.SnoozedUntil is { } until && until > now) { Next(until); continue; }
            }
            if (item.FireTime <= now) due.Add(item);
            else Next(item.FireTime);
        }
        return new Result(due.OrderByDescending(i => i.FireTime).ThenBy(i => i.Subject, StringComparer.CurrentCultureIgnoreCase).ToList(), next);
    }

    /// <summary>The local record of a dismissal (shown immediately as gone; sent to Graph until it accepts).</summary>
    public static ReminderRecord Dismiss(ReminderItem item, DateTimeOffset now) =>
        new(item.Key, item.AccountId, item.EventId, item.FireTime, ReminderRecord.Dismissed, null, now, PendingSync: true);

    /// <summary>The local record of a snooze until now + <paramref name="span"/>.</summary>
    public static ReminderRecord Snooze(ReminderItem item, TimeSpan span, DateTimeOffset now) =>
        new(item.Key, item.AccountId, item.EventId, item.FireTime, ReminderRecord.Snoozed, now + span, now, PendingSync: true);

    /// <summary>Keeps the record file small: drops records older than the look-back plus a week, and keeps at most <see cref="MaxRecords"/>.</summary>
    public static IReadOnlyList<ReminderRecord> Prune(IEnumerable<ReminderRecord> records, DateTimeOffset now, TimeSpan lookBack)
    {
        var limit = now - lookBack - TimeSpan.FromDays(7);
        return records.Where(r => r.FireTime >= limit || r.RecordedAt >= limit || r.PendingSync)
            .OrderByDescending(r => r.RecordedAt).Take(MaxRecords).ToList();
    }

    /// <summary>Outlook's "Due in" column: "6 hours overdue", "in 10 minutes", "Now" -- measured from the event's start.</summary>
    public static string DueText(DateTimeOffset eventStart, DateTimeOffset now)
    {
        var d = now - eventStart;
        var late = d >= TimeSpan.Zero;
        var a = late ? d : -d;
        if (a < TimeSpan.FromMinutes(1)) return "Now";
        string span = a < TimeSpan.FromHours(1) ? Unit((int)a.TotalMinutes, "minute")
            : a < TimeSpan.FromDays(2) ? Unit((int)a.TotalHours, "hour")
            : a < TimeSpan.FromDays(14) ? Unit((int)a.TotalDays, "day")
            : Unit((int)(a.TotalDays / 7), "week");
        return late ? span + " overdue" : "in " + span;
        static string Unit(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
    }
}

/// <summary>reminders-state.json in the OpenOutlook settings folder: what was dismissed or snoozed on this computer. A damaged file reads as empty.</summary>
public sealed class ReminderStateStore
{
    public const int MaximumFileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    public string Path { get; }

    public ReminderStateStore(string? configRoot = null)
    {
        var root = configRoot ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(root)) root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        Path = System.IO.Path.Combine(root, "OpenOutlook", "reminders-state.json");
    }

    public IReadOnlyDictionary<string, ReminderRecord> Load()
    {
        try
        {
            if (!File.Exists(Path) || new FileInfo(Path).Length > MaximumFileBytes) return new Dictionary<string, ReminderRecord>();
            var list = JsonSerializer.Deserialize<List<ReminderRecord>>(File.ReadAllText(Path), Json) ?? [];
            return list.Where(r => r is not null && !string.IsNullOrEmpty(r.Key) && r.State is ReminderRecord.Dismissed or ReminderRecord.Snoozed)
                .GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.RecordedAt).First());
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return new Dictionary<string, ReminderRecord>(); }
    }

    /// <summary>Adds or replaces records (by key), prunes, and writes atomically.</summary>
    public IReadOnlyDictionary<string, ReminderRecord> Upsert(IEnumerable<ReminderRecord> changes, DateTimeOffset now, TimeSpan lookBack)
    {
        var all = new Dictionary<string, ReminderRecord>(Load());
        foreach (var r in changes) all[r.Key] = r;
        var kept = ReminderSchedule.Prune(all.Values, now, lookBack);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".reminders-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(kept, Json));
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return kept.ToDictionary(r => r.Key);
    }
}
