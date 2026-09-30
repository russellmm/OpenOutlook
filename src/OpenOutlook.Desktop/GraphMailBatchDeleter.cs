using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

public sealed record GraphDeleteBatchResult(IReadOnlyList<GraphInboxMessage> Completed,
    int Requested, bool Canceled, Exception? Failure);

/// <summary>Processes a snapshot of selected messages; never repeats a completed mutation.</summary>
public static class GraphMailBatchDeleter
{
    public static async Task<GraphDeleteBatchResult> DeleteAsync(GraphMailWriter writer, string token,
        IReadOnlyList<GraphInboxMessage> messages, Func<int, Task<bool>> confirmPermanent,
        Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(confirmPermanent);
        var chosen = messages.DistinctBy(message => message.Id).ToArray();
        if (chosen.Length == 0) return new GraphDeleteBatchResult([], 0, false, null);
        var permanent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in chosen)
            if (await writer.IsInDeletedItemsAsync(token, message.Id)) permanent.Add(message.Id);
        if (permanent.Count > 0 && !await confirmPermanent(permanent.Count))
            return new GraphDeleteBatchResult([], chosen.Length, true, null);

        var completed = new List<GraphInboxMessage>();
        foreach (var message in chosen)
        {
            try
            {
                if (permanent.Contains(message.Id)) await writer.DeletePermanentlyAsync(token, message.Id);
                else await writer.MoveAsync(token, message.Id, "deleteditems");
                completed.Add(message);
                progress?.Invoke(completed.Count, chosen.Length);
            }
            catch (Exception failure)
            { return new GraphDeleteBatchResult(completed, chosen.Length, false, failure); }
        }
        return new GraphDeleteBatchResult(completed, chosen.Length, false, null);
    }
}
