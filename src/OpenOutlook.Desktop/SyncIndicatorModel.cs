namespace OpenOutlook.Desktop;

public enum SyncPhase { Idle, Syncing, Offline, Problem, Off }

/// <summary>What the status bar knows about one mailbox copy.</summary>
public sealed record MirrorInfo(string Address, SyncPhase Phase, string Detail, DateTime? LastSyncUtc, int Waiting = 0);

/// <summary>The one-line state of all mailbox copies shown at the right of the status bar: a glyph, a short text and a tooltip with one line per account.</summary>
public sealed record SyncIndicatorState(SyncPhase Phase, string Glyph, string Text, string Tooltip, bool Visible)
{
    /// <summary>Worst state wins: a problem over offline over syncing over up to date. Copies that are turned off do not count; with nothing left the indicator is hidden.</summary>
    public static SyncIndicatorState Compute(IReadOnlyCollection<MirrorInfo> copies, DateTime nowUtc)
    {
        var active = copies.Where(c => c.Phase != SyncPhase.Off).ToList();
        if (active.Count == 0) return new SyncIndicatorState(SyncPhase.Off, "", "", "", false);
        var lines = string.Join("\n", active.Select(c => $"{c.Address}: {Describe(c, nowUtc)}"));
        var waiting = active.Sum(c => c.Waiting);
        if (active.Any(c => c.Phase == SyncPhase.Problem))
            return new SyncIndicatorState(SyncPhase.Problem, "✖", "Sync problem", lines, true);
        if (active.Any(c => c.Phase == SyncPhase.Offline))
            return new SyncIndicatorState(SyncPhase.Offline, "⚠", waiting > 0 ? $"Offline · {waiting} change{(waiting == 1 ? "" : "s")} waiting" : "Offline", lines, true);
        if (active.Any(c => c.Phase == SyncPhase.Syncing))
            return new SyncIndicatorState(SyncPhase.Syncing, "↻", "Syncing…", lines, true);
        var newest = active.Where(c => c.LastSyncUtc is not null).Select(c => c.LastSyncUtc!.Value).DefaultIfEmpty().Max();
        var text = newest == default ? "Waiting for the first sync" : "Up to date · " + Ago(nowUtc - newest);
        return new SyncIndicatorState(SyncPhase.Idle, newest == default ? "…" : "✓", text, lines, true);
    }

    private static string Describe(MirrorInfo c, DateTime now) => c.Phase switch
    {
        SyncPhase.Syncing => "syncing" + (c.Detail.Length > 0 ? " (" + c.Detail + ")" : ""),
        SyncPhase.Offline => "offline" + (c.Waiting > 0 ? $", {c.Waiting} change{(c.Waiting == 1 ? "" : "s")} waiting" : ""),
        SyncPhase.Problem => c.Detail,
        _ => (c.LastSyncUtc is { } l ? "up to date, synchronised " + Ago(now - l) : "waiting for the first sync") + (c.Detail.Length > 0 ? "; " + c.Detail : "")
    };

    internal static string Ago(TimeSpan span) =>
        span < TimeSpan.FromMinutes(1) ? "just now" : span < TimeSpan.FromHours(1) ? $"{(int)span.TotalMinutes} min ago" : span < TimeSpan.FromDays(1) ? $"{(int)span.TotalHours} h ago" : $"{(int)span.TotalDays} d ago";
}
