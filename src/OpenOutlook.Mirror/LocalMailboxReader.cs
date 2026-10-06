using OpenOutlook.Providers.Microsoft;
using PstCore;

namespace OpenOutlook.Mirror;

/// <summary>
/// Reads a folder of a mailbox from its local copy (the PST plus the sync state) in the shape the live Graph list uses, so the folder list can show
/// instantly without a network request. Message ids are the server ids, so reading, flagging and deleting still go to the server.
/// </summary>
public static class LocalMailboxReader
{
    /// <summary>Messages shown for one folder (newest first); the copy holds more, this keeps the list responsive.</summary>
    public const int MaxMessages = 500;

    /// <summary>
    /// Returns null when the local copy cannot answer yet (the folder was never synchronised), so the caller asks the server instead.
    /// <paramref name="selectionId"/> is a server folder id or the well-known name "inbox". <paramref name="hidden"/> holds server ids removed locally
    /// that the next sync has not caught up with; <paramref name="flags"/> holds flag changes in the same position.
    /// </summary>
    public static GraphInboxPage? Read(IPstEngine store, SyncStateStore state, string selectionId, ISet<string>? hidden = null, IReadOnlyDictionary<string, bool>? flags = null)
    {
        MirrorFolderState? folder = null;
        if (selectionId.Equals("inbox", StringComparison.OrdinalIgnoreCase))
            folder = state.Folders().FirstOrDefault(f => store.FindFolder(f.PstNid) is { Name: "Inbox" } inbox && inbox.ParentNid != 0 && f.LastSync is not null);
        else if (state.GetFolder(selectionId) is { LastSync: not null } f) folder = f;
        if (folder is null || store.FindFolder(folder.PstNid) is not { } pstFolder) return null;

        var byNid = new Dictionary<uint, (string Id, bool Flagged)>();            // the list table of the PST does not carry the flag, the sync state does
        foreach (var m in state.MessagesIn(folder.RemoteId)) if (m.PstNid != 0) byNid[m.PstNid] = (m.RemoteId, m.Flagged);
        var rows = new List<GraphInboxMessage>();
        foreach (var s in store.GetMessages(pstFolder))
        {
            if (!byNid.TryGetValue(s.Nid, out var known) || hidden?.Contains(known.Id) == true) continue;
            var id = known.Id;
            var flagged = flags is not null && flags.TryGetValue(id, out var changed) ? changed : known.Flagged;
            rows.Add(new GraphInboxMessage(id, s.Subject, s.From, s.To,
                s.Received == default ? null : new DateTimeOffset(DateTime.SpecifyKind(s.Received, DateTimeKind.Utc)),
                s.Size, s.HasAttachment, s.IsRead, "", flagged, false));
        }
        rows.Sort((a, b) => Nullable.Compare(b.Received, a.Received));
        var unread = rows.Count(r => !r.IsRead);
        return new GraphInboxPage(pstFolder.Name, rows.Count, unread, rows.Take(MaxMessages).ToList(), rows.Count > MaxMessages);
    }
}
