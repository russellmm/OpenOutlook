using OpenOutlook.JunkCleaner;

namespace OpenOutlook.Providers.Microsoft;

/// <summary>One message the cleaner matched (or moved): what the manual-run preview and the run log show.</summary>
public sealed record JunkCleanerHit(string MessageId, string Subject, string Sender, IReadOnlyList<string> Reasons);

public sealed record JunkCleanerRunResult(int Scanned, IReadOnlyList<JunkCleanerHit> Matched, IReadOnlyList<JunkCleanerHit> Moved,
    IReadOnlyList<string> Failures, bool CapReached);

/// <summary>
/// Cleans one Microsoft account's Junk Email folder: lists it, matches the account's keywords and rules, and moves matches to Deleted Items
/// (never a permanent delete). The same Graph account id is used for the scan, the settings and the moves.
/// </summary>
public sealed class MicrosoftJunkCleanerRunner(GraphJunkMailReader reader, GraphMailWriter writer, Func<CancellationToken, Task<string>> accessToken, string accountId, int maxMoves = MicrosoftJunkCleanerRunner.MaxMovesPerRun)
{
    /// <summary>Messages moved in one run at most, so a bad keyword cannot empty a folder.</summary>
    public const int MaxMovesPerRun = 500;

    /// <summary>Scans and matches without changing anything (the manual run's preview).</summary>
    public async Task<JunkCleanerRunResult> ScanAsync(JunkCleanerAccountSettings settings, CancellationToken ct = default)
    {
        var messages = await reader.ListJunkMessagesAsync(50, 1000, ct).ConfigureAwait(false);
        var preview = new MicrosoftJunkPreviewMatcher(accountId).Preview(accountId, settings, messages);
        var byId = messages.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var matched = preview.Matches.Select(m =>
        {
            var message = byId[m.MessageId];
            return new JunkCleanerHit(m.MessageId, message.Subject ?? "(no subject)", message.From ?? message.Sender ?? "", m.Reasons);
        }).ToList();
        return new JunkCleanerRunResult(messages.Count, matched, [], [], matched.Count > maxMoves);
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
    public async Task<JunkCleanerRunResult> CleanAsync(JunkCleanerAccountSettings settings, CancellationToken ct = default) =>
        await MoveAsync(await ScanAsync(settings, ct).ConfigureAwait(false), null, ct).ConfigureAwait(false);
}
