namespace OpenOutlook.Desktop;

/// <summary>One message being copied or moved between stores. <see cref="Id"/> is whatever the source needs to find it again (a PST NID, a Graph or Gmail id).</summary>
public sealed record TransferItem(string Id, string Subject, bool IsRead);

/// <summary>Where messages come from when the store cannot copy natively: gives the whole message as MIME and, for a move, removes the originals.</summary>
public interface ITransferSource
{
    Task<byte[]> ReadMimeAsync(TransferItem item, CancellationToken ct);
    /// <summary>Takes the originals out of their folder (a delete to the store's own Deleted Items / Trash, never a permanent delete).</summary>
    Task RemoveAsync(IReadOnlyList<TransferItem> items, CancellationToken ct);
}

/// <summary>Where messages go: a PST folder, a Microsoft folder or a Gmail label. Writes may be buffered until <see cref="CommitAsync"/>.</summary>
public interface ITransferSink
{
    Task AddAsync(byte[] mime, TransferItem item, CancellationToken ct);
    /// <summary>Makes everything added since the last commit durable; throwing means none of it can be relied on.</summary>
    Task CommitAsync(CancellationToken ct);
}

public sealed record TransferFailure(TransferItem Item, string Error);

public sealed record TransferResult(int Transferred, int Removed, IReadOnlyList<TransferFailure> Failures, string? RemoveError)
{
    public bool AllDone => Failures.Count == 0 && RemoveError is null;
}

/// <summary>
/// Copy or move messages between stores of different kinds through MIME. The safety rule for a move: an original is removed only after its copy has been
/// committed in the destination, so a failure anywhere leaves the message in (at least) its original place; a failed removal leaves a duplicate, never a loss.
/// </summary>
public static class MailTransfer
{
    public const int ChunkSize = 20;

    public static async Task<TransferResult> RunAsync(ITransferSource source, IReadOnlyList<TransferItem> items, ITransferSink sink, bool move,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var failures = new List<TransferFailure>();
        var transferred = 0;
        var removed = 0;
        string? removeError = null;
        var done = 0;
        for (var start = 0; start < items.Count; start += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = items.Skip(start).Take(ChunkSize).ToList();
            var added = new List<TransferItem>();
            foreach (var item in chunk)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"{(move ? "Moving" : "Copying")} message {++done} of {items.Count}…");
                try
                {
                    var mime = await source.ReadMimeAsync(item, ct).ConfigureAwait(false);
                    await sink.AddAsync(mime, item, ct).ConfigureAwait(false);
                    added.Add(item);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failures.Add(new TransferFailure(item, e.Message));
                }
            }
            if (added.Count == 0) continue;
            try { await sink.CommitAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures.AddRange(added.Select(i => new TransferFailure(i, e.Message)));          // not committed: nothing is removed from the source
                continue;
            }
            transferred += added.Count;
            if (!move) continue;
            try
            {
                await source.RemoveAsync(added, ct).ConfigureAwait(false);
                removed += added.Count;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                removeError = e.Message;                                                         // copies exist, originals stay: stop here rather than pile up duplicates
                break;
            }
        }
        return new TransferResult(transferred, removed, failures, removeError);
    }
}
