namespace OpenOutlook.Desktop;

/// <summary>
/// Schedules the pictures of one message so the quick ones are shown before the slow ones arrive. Up to
/// <c>concurrency</c> fetches run at a time; each result is handed to <c>accept</c> the moment it arrives; once
/// <c>firstLayoutDelay</c> has passed and something new was accepted, <c>layout</c> is called so the message is laid out with the
/// pictures so far. The caller lays it out once more at the end when <see cref="Result.ArrivedSinceLastLayout"/> is not zero.
///
/// Found 2026-10-15: an American Express mailing waited 8 s for its pictures because a tracking server sends its 47-byte pixel's
/// headers at once and the body seconds later, and the reader used to lay the message out only after every picture (in batches
/// of four, each waiting for its slowest). Kept free of controls so that timing can be tested with fake fetches.
///
/// Continuations run on the caller's synchronization context (no ConfigureAwait(false)), so <c>accept</c> and <c>layout</c> may
/// touch the UI.
/// </summary>
public static class ProgressivePictureLoader
{
    /// <summary>How the load ended.</summary>
    /// <param name="Completed">False when it was cancelled or the message changed (<c>isCurrent</c> returned false).</param>
    /// <param name="LaidOutEarly">True when <c>layout</c> was called before the last picture arrived.</param>
    /// <param name="ArrivedSinceLastLayout">Pictures accepted after the last early layout (all of them when there was none).</param>
    /// <param name="NeedsFinalLayout">The caller should lay the message out now: something arrived since the last layout, or the
    /// load finished before the first-layout time (then nothing has been laid out yet).</param>
    public sealed record Result(bool Completed, bool LaidOutEarly, int ArrivedSinceLastLayout, bool NeedsFinalLayout);

    public static async Task<Result> RunAsync<T>(
        IReadOnlyList<T> sources,
        Func<T, CancellationToken, Task<byte[]?>> fetch,
        Func<T, byte[], bool> accept,
        Action layout,
        Func<bool> isCurrent,
        int concurrency,
        TimeSpan firstLayoutDelay,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        // Not disposed: fetches still running after a cancel release it later (a SemaphoreSlim without its wait handle needs no disposal).
        var gate = new SemaphoreSlim(concurrency);
        var remaining = sources.Select(async source =>
        {
            await gate.WaitAsync(cancellationToken);
            try { return (Source: source, Bytes: await fetch(source, cancellationToken)); }
            catch (Exception) { return (Source: source, Bytes: (byte[]?)null); }      // a failed picture keeps its placeholder
            finally { gate.Release(); }
        }).ToList();
        var firstLayout = Task.Delay(firstLayoutDelay, cancellationToken);
        var waited = false;               // the first-layout time has passed
        var laidOut = false;
        var arrived = 0;
        try
        {
            while (remaining.Count > 0)
            {
                var next = await Task.WhenAny(waited ? (IEnumerable<Task>)remaining : remaining.Append<Task>(firstLayout));
                if (cancellationToken.IsCancellationRequested || !isCurrent()) return new Result(false, laidOut, arrived, false);
                if (next == firstLayout)
                {
                    waited = true;
                    if (arrived > 0)
                    {
                        arrived = 0;
                        laidOut = true;
                        layout();
                    }
                    continue;
                }
                var finished = (Task<(T Source, byte[]? Bytes)>)next;
                remaining.Remove(finished);
                var (source, bytes) = await finished;
                if (bytes is not null && accept(source, bytes)) arrived++;
            }
        }
        catch (OperationCanceledException) { return new Result(false, laidOut, arrived, false); }
        var completed = !cancellationToken.IsCancellationRequested && isCurrent();
        return new Result(completed, laidOut, arrived, completed && (arrived > 0 || !waited));
    }
}
