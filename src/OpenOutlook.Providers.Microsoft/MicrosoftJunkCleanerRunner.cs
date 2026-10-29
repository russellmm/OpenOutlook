using OpenOutlook.JunkCleaner;

namespace OpenOutlook.Providers.Microsoft;

/// <summary>One message the cleaner matched (or moved): what the manual-run preview and the run log show.</summary>
public sealed record JunkCleanerHit(string MessageId, string Subject, string Sender, IReadOnlyList<string> Reasons);

/// <param name="ScannedIds">Every message looked at, safe ones included (the Inbox spam check uses it to see which list rows were checked).</param>
public sealed record JunkCleanerRunResult(int Scanned, IReadOnlyList<JunkCleanerHit> Matched, IReadOnlyList<JunkCleanerHit> Moved,
    IReadOnlyList<string> Failures, bool CapReached, IReadOnlyList<string>? ScannedIds = null);

/// <summary>What applying the Safe / Blocked Senders lists did: safe mail moved from Junk Email to the Inbox, blocked mail from the Inbox to Junk Email.</summary>
public sealed record JunkListRunResult(IReadOnlyList<JunkCleanerHit> Rescued, IReadOnlyList<JunkCleanerHit> Blocked, IReadOnlyList<string> Failures);

/// <summary>
/// Cleans one Microsoft account's Junk Email folder: lists it, matches the account's keywords and rules, and moves matches to Deleted Items
/// (never a permanent delete). The same Graph account id is used for the scan, the settings and the moves.
/// </summary>
public sealed class MicrosoftJunkCleanerRunner(GraphJunkMailReader reader, GraphMailWriter writer, Func<CancellationToken, Task<string>> accessToken, string accountId, int maxMoves = MicrosoftJunkCleanerRunner.MaxMovesPerRun, string? ownAddress = null)
{
    /// <summary>Messages moved in one run at most, so a bad keyword cannot empty a folder.</summary>
    public const int MaxMovesPerRun = 500;

    /// <summary>Moves made by one <see cref="ApplyListsAsync"/> run at most.</summary>
    public const int MaxListMovesPerRun = 200;

    /// <summary>Scans and matches without changing anything (the manual run's preview). Mail the lists call safe is never matched.</summary>
    public async Task<JunkCleanerRunResult> ScanAsync(JunkCleanerAccountSettings settings, CancellationToken ct = default, JunkLists? lists = null, ISet<string>? contacts = null)
    {
        var all = await reader.ListJunkMessagesAsync(50, 1000, ct).ConfigureAwait(false);
        return Match(all, settings, lists, contacts);
    }

    /// <summary>
    /// The Inbox spam check (owner 2026-10-29): matches the newest <paramref name="max"/> Inbox messages against the given rules without
    /// changing anything. Mail the lists call safe is never matched, mail from a blocked sender always is ("Blocked sender"); the settings' own
    /// opt-in is ignored (the caller has its own).
    /// </summary>
    public async Task<JunkCleanerRunResult> ScanInboxAsync(JunkCleanerAccountSettings settings, int max = 200, CancellationToken ct = default, JunkLists? lists = null, ISet<string>? contacts = null)
    {
        var all = await reader.ListInboxMessagesAsync(50, max, ct).ConfigureAwait(false);
        return Match(all, settings with { Enabled = true }, lists, contacts, markBlocked: true);
    }

    private JunkCleanerRunResult Match(IReadOnlyList<GraphJunkMessage> all, JunkCleanerAccountSettings settings, JunkLists? lists, ISet<string>? contacts, bool markBlocked = false)
    {
        var messages = lists is null ? all : all.Where(m => Verdict(lists, m, contacts) != JunkListVerdict.Safe).ToList();
        var preview = new MicrosoftJunkPreviewMatcher(accountId, ownAddress).Preview(accountId, settings, messages);
        var byId = messages.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var matched = preview.Matches.Select(m =>
        {
            var message = byId[m.MessageId];
            return new JunkCleanerHit(m.MessageId, message.Subject ?? "(no subject)", message.From ?? message.Sender ?? "", m.Reasons);
        }).ToList();
        if (markBlocked && lists is not null)
        {
            var blocked = messages.Where(m => Verdict(lists, m, contacts) == JunkListVerdict.Blocked).ToDictionary(m => m.Id, StringComparer.Ordinal);
            for (var i = 0; i < matched.Count; i++)
                if (blocked.Remove(matched[i].MessageId)) matched[i] = matched[i] with { Reasons = ["Blocked sender", .. matched[i].Reasons] };
            matched.AddRange(blocked.Values.Select(m => new JunkCleanerHit(m.Id, m.Subject ?? "(no subject)", m.From ?? m.Sender ?? "", ["Blocked sender"])));
        }
        return new JunkCleanerRunResult(messages.Count, matched, [], [], matched.Count > maxMoves, all.Select(m => m.Id).ToArray());
    }

    /// <summary>Moves the given matches (at most <see cref="MaxMovesPerRun"/>) to Deleted Items; one failure never stops the rest.</summary>
    public async Task<JunkCleanerRunResult> MoveAsync(JunkCleanerRunResult scan, IEnumerable<JunkCleanerHit>? only = null, CancellationToken ct = default)
    {
        var chosen = (only ?? scan.Matched).Take(maxMoves).ToList();
        var moved = new List<JunkCleanerHit>();
        var failures = new List<string>();
        foreach (var hit in chosen)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await writer.MoveAsync(await accessToken(ct).ConfigureAwait(false), hit.MessageId, "deleteditems", ct).ConfigureAwait(false);
                moved.Add(hit);
            }
            catch (GraphMailException e) when (e.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                failures.Add("Microsoft declined mail access; sign in again from Account Settings.");
                break;                                                              // every further move would fail the same way
            }
            catch (Exception e) when (e is GraphMailException or HttpRequestException or ArgumentException)
            { failures.Add($"\"{hit.Subject}\": {e.Message}"); }
        }
        return scan with { Moved = moved, Failures = failures, CapReached = scan.Matched.Count > maxMoves };
    }

    /// <summary>The scheduled run: scan, then move everything that matched.</summary>
    public async Task<JunkCleanerRunResult> CleanAsync(JunkCleanerAccountSettings settings, CancellationToken ct = default, JunkLists? lists = null, ISet<string>? contacts = null) =>
        await MoveAsync(await ScanAsync(settings, ct, lists, contacts).ConfigureAwait(false), null, ct).ConfigureAwait(false);

    public static JunkListVerdict Verdict(JunkLists lists, GraphJunkMessage m, ISet<string>? contacts) =>
        lists.Classify(m.From ?? m.Sender, m.ToRecipients.Concat(m.CcRecipients), contacts);

    /// <summary>
    /// Applies the lists: mail in Junk Email that they call safe goes to the Inbox; mail among the newest 200 of the Inbox from a blocked sender
    /// goes to Junk Email (never deleted). At most <see cref="MaxListMovesPerRun"/> moves; one failure never stops the rest.
    /// </summary>
    public async Task<JunkListRunResult> ApplyListsAsync(JunkLists lists, ISet<string>? contacts = null, CancellationToken ct = default)
    {
        var rescued = new List<JunkCleanerHit>(); var blocked = new List<JunkCleanerHit>(); var failures = new List<string>();
        if (lists.Count == 0 && contacts is not { Count: > 0 }) return new JunkListRunResult(rescued, blocked, failures);
        static JunkCleanerHit Hit(GraphJunkMessage m, string reason) => new(m.Id, m.Subject ?? "(no subject)", m.From ?? m.Sender ?? "", [reason]);
        var junk = await reader.ListJunkMessagesAsync(50, 500, ct).ConfigureAwait(false);
        var toInbox = junk.Where(m => Verdict(lists, m, contacts) == JunkListVerdict.Safe).Select(m => Hit(m, "Safe sender")).ToList();
        var inbox = await reader.ListInboxMessagesAsync(50, 200, ct).ConfigureAwait(false);
        var toJunk = inbox.Where(m => Verdict(lists, m, contacts) == JunkListVerdict.Blocked).Select(m => Hit(m, "Blocked sender")).ToList();
        var budget = MaxListMovesPerRun;
        foreach (var (hits, destination, done) in new[] { (toInbox, "inbox", rescued), (toJunk, "junkemail", blocked) })
            foreach (var hit in hits)
            {
                if (budget-- <= 0) break;
                ct.ThrowIfCancellationRequested();
                try
                {
                    await writer.MoveAsync(await accessToken(ct).ConfigureAwait(false), hit.MessageId, destination, ct).ConfigureAwait(false);
                    done.Add(hit);
                }
                catch (GraphMailException e) when (e.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    failures.Add("Microsoft declined mail access; sign in again from Account Settings.");
                    return new JunkListRunResult(rescued, blocked, failures);
                }
                catch (Exception e) when (e is GraphMailException or HttpRequestException or ArgumentException)
                { failures.Add($"\"{hit.Subject}\": {e.Message}"); }
            }
        return new JunkListRunResult(rescued, blocked, failures);
    }
}
