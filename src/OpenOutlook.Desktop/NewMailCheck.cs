namespace OpenOutlook.Desktop;

/// <summary>What one light new-mail check found for a folder compared with the previous check.</summary>
public enum NewMailVerdict { First, Same, Changed }

/// <summary>
/// Remembers what the last new-mail check saw per account and folder (a fingerprint: counts plus the newest message, or Gmail's history id), so a
/// check every minute only leads to a mailbox-copy sync when something really changed. Keys are "accountId|folder".
/// </summary>
public sealed class NewMailTracker
{
    private const string Stale = "\0stale";
    private readonly Dictionary<string, string> _seen = new(StringComparer.Ordinal);

    public NewMailVerdict Observe(string accountId, string folder, string fingerprint)
    {
        var key = accountId + "|" + folder;
        var known = _seen.TryGetValue(key, out var old);
        _seen[key] = fingerprint;
        return !known ? NewMailVerdict.First : old == fingerprint ? NewMailVerdict.Same : NewMailVerdict.Changed;
    }

    /// <summary>The sync that should have followed did not complete (offline, sign-in problem): the next check of this account counts as changed.</summary>
    public void Invalidate(string accountId)
    {
        var prefix = accountId + "|";
        foreach (var key in _seen.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _seen[key] = Stale;
    }

    /// <summary>
    /// Whether the check should bring the mailbox copy up to date: always for Send/Receive All; when the Inbox or the open folder changed; and on the
    /// first check of an account whose copy was already synchronised (or was being synchronised) when the check began - mail may have arrived since.
    /// Before the first sync of the copy a first check is only a baseline: the start-up sync, which begins after it, brings everything.
    /// </summary>
    public static bool ShouldSync(NewMailVerdict inbox, NewMailVerdict? open, bool copySyncedBeforeCheck, bool manual) =>
        manual || inbox == NewMailVerdict.Changed || open == NewMailVerdict.Changed || (inbox == NewMailVerdict.First && copySyncedBeforeCheck);

    /// <summary>Options > Advanced > Send and receive: minutes between checks (1 to 60).</summary>
    public static TimeSpan Interval(int minutes) => TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 60));
}
