using OpenOutlook.Providers.Google;

namespace OpenOutlook.Mirror;

/// <summary>
/// The Gmail side of the mirror. Labels are folders and a message with several labels appears in each of its label folders, like an IMAP client shows it. Lists are cheap:
/// the ids of a label (inside the keep-window) plus the ids that are unread and the ids that are starred, so read and flag state need no per-message request.
/// Changes from the copy become label changes (a move adds the destination label and removes the source label; Delete moves to Trash; permanent deletion is not
/// possible with the gmail.modify scope).
/// </summary>
public sealed class GmailMirrorSource(GmailMailbox box, bool canPush) : IMailSyncSource, IMailSyncSink
{
    private static readonly Dictionary<string, string> SystemFolders = new(StringComparer.Ordinal)
    {
        ["INBOX"] = "Inbox", ["SENT"] = "Sent", ["DRAFT"] = "Drafts", ["SPAM"] = "Spam", ["TRASH"] = "Trash", ["STARRED"] = "Starred", ["IMPORTANT"] = "Important"
    };

    private readonly Dictionary<string, string> _labelNames = new(StringComparer.Ordinal);       // label id -> full label name, for creating nested labels
    private readonly Dictionary<string, byte[]> _mimeCache = new(StringComparer.Ordinal);       // a message in several label folders is downloaded once
    private readonly Queue<string> _mimeOrder = new();
    private long _mimeBytes;
    private const long MimeCacheLimit = 64L * 1024 * 1024;

    public bool CanPush => canPush;

    public async Task<IReadOnlyList<RemoteFolder>> GetFoldersAsync(CancellationToken ct)
    {
        var labels = await box.ListLabelNamesAsync(ct).ConfigureAwait(false);
        _labelNames.Clear();
        var wanted = labels.Where(l => l.IsSystem ? SystemFolders.ContainsKey(l.Id) : true).ToList();
        var userByName = wanted.Where(l => !l.IsSystem).ToDictionary(l => l.Name, l => l.Id, StringComparer.Ordinal);
        var list = new List<RemoteFolder>();
        foreach (var l in wanted)
        {
            _labelNames[l.Id] = l.IsSystem ? SystemFolders[l.Id] : l.Name;
            if (l.IsSystem) { list.Add(new RemoteFolder(l.Id, null, SystemFolders[l.Id], l.Id == GmailMailbox.TrashLabel ? "deleteditems" : null)); continue; }
            // "Work/Reports" is nested under "Work" when that label exists
            var slash = l.Name.LastIndexOf('/');
            if (slash > 0 && userByName.TryGetValue(l.Name[..slash], out var parentId)) list.Add(new RemoteFolder(l.Id, parentId, l.Name[(slash + 1)..], null));
            else list.Add(new RemoteFolder(l.Id, null, l.Name, null));
        }
        return list;
    }

    public async Task<IReadOnlyList<RemoteMessage>> ListMessagesAsync(string folderId, DateTimeOffset? since, CancellationToken ct)
    {
        var window = since is { } s ? $"after:{s.UtcDateTime:yyyy/MM/dd}" : null;
        var ids = await box.ListMessageIdsAsync(folderId, window, 20_000, ct).ConfigureAwait(false);
        if (ids.Count == 0) return [];
        var unread = new HashSet<string>(await box.ListMessageIdsAsync(folderId, (window + " is:unread").Trim(), 20_000, ct).ConfigureAwait(false), StringComparer.Ordinal);
        var starred = new HashSet<string>(await box.ListMessageIdsAsync(folderId, (window + " is:starred").Trim(), 20_000, ct).ConfigureAwait(false), StringComparer.Ordinal);
        return ids.Select(id => new RemoteMessage(id, null, null, !unread.Contains(id), starred.Contains(id))).ToList();
    }

    public async Task<byte[]> GetMimeAsync(string messageId, CancellationToken ct)
    {
        if (_mimeCache.TryGetValue(messageId, out var cached)) return cached;
        var raw = await box.GetRawAsync(messageId, ct).ConfigureAwait(false);
        if (raw.Length < MimeCacheLimit / 4)
        {
            _mimeCache[messageId] = raw; _mimeOrder.Enqueue(messageId); _mimeBytes += raw.Length;
            while (_mimeBytes > MimeCacheLimit && _mimeOrder.Count > 0)
            {
                var old = _mimeOrder.Dequeue();
                if (_mimeCache.Remove(old, out var gone)) _mimeBytes -= gone.Length;
            }
        }
        return raw;
    }

    // ---- changes from the copy ------------------------------------------------------------------------------------------------------------------------

    public Task SetReadAsync(string messageId, bool read, CancellationToken ct) => box.SetReadAsync([messageId], read, ct);

    public Task SetFlaggedAsync(string messageId, bool flagged, CancellationToken ct) => box.SetStarredAsync([messageId], flagged, ct);

    public async Task<string> MoveAsync(string messageId, string fromFolderId, string destinationFolderId, CancellationToken ct)
    {
        if (destinationFolderId == GmailMailbox.TrashLabel) await box.TrashAsync([messageId], ct).ConfigureAwait(false);
        else
        {
            var remove = new List<string>();
            if (fromFolderId != destinationFolderId) remove.Add(fromFolderId);
            await box.ModifyLabelsAsync([messageId], [destinationFolderId], remove, ct).ConfigureAwait(false);
        }
        return messageId;                                                                         // Gmail keeps the id when labels change
    }

    public Task PurgeAsync(string messageId, CancellationToken ct) =>
        throw new PushNotSupportedException("Gmail does not allow permanent deletion with this sign-in; the message stays in Trash until Gmail empties it.");

    public async Task<string> CreateFolderAsync(string? parentId, string name, CancellationToken ct)
    {
        var full = parentId is not null && _labelNames.TryGetValue(parentId, out var parent) ? parent + "/" + name : name;
        var label = await box.CreateLabelAsync(full, ct).ConfigureAwait(false);
        _labelNames[label.Id] = label.Name;
        return label.Id;
    }
}
