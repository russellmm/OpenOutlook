using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Mirror;

/// <summary>The Microsoft Graph side of the mirror: folders, message lists and message MIME of one verified mailbox, and (when a writer is given) the changes made in the local copy.</summary>
public sealed class GraphMirrorSource(GraphMailFolderReader folders, GraphMailboxSyncReader sync, GraphInboxReader inbox, Func<CancellationToken, Task<string>> token, GraphMailWriter? writer = null)
    : IMailSyncSource, IMailSyncSink
{
    private GraphMailWriter Writer => writer ?? throw new InvalidOperationException("This mailbox cannot be changed (the sign-in has no write permission).");
    public bool CanPush => writer is not null;

    public async Task SetReadAsync(string messageId, bool read, CancellationToken ct) => await Writer.SetReadAsync(await token(ct).ConfigureAwait(false), messageId, read, ct).ConfigureAwait(false);
    public async Task SetFlaggedAsync(string messageId, bool flagged, CancellationToken ct) => await Writer.SetFlagAsync(await token(ct).ConfigureAwait(false), messageId, flagged, ct).ConfigureAwait(false);
    public async Task<string> MoveAsync(string messageId, string destinationFolderId, CancellationToken ct) => await Writer.MoveAsync(await token(ct).ConfigureAwait(false), messageId, destinationFolderId, ct).ConfigureAwait(false);
    public async Task PurgeAsync(string messageId, CancellationToken ct) => await Writer.DeletePermanentlyAsync(await token(ct).ConfigureAwait(false), messageId, ct).ConfigureAwait(false);
    public async Task<string> CreateFolderAsync(string? parentId, string name, CancellationToken ct) => (await Writer.CreateFolderAsync(await token(ct).ConfigureAwait(false), parentId, name, ct).ConfigureAwait(false)).Id;

    public async Task<IReadOnlyList<RemoteFolder>> GetFoldersAsync(CancellationToken ct)
    {
        var t = await token(ct).ConfigureAwait(false);
        var tree = await folders.GetFoldersAsync(t, ct).ConfigureAwait(false);
        var wellKnown = await sync.GetWellKnownFolderIdsAsync(t, ct).ConfigureAwait(false);
        var kind = wellKnown.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        var list = new List<RemoteFolder>();
        void Walk(IEnumerable<GraphMailboxFolder> level, string? parent)
        {
            foreach (var f in level)
            {
                list.Add(new RemoteFolder(f.Id, parent, f.DisplayName, kind.TryGetValue(f.Id, out var k) ? k : null));
                Walk(f.Children, f.Id);
            }
        }
        Walk(tree, null);
        return list;
    }

    public async Task<IReadOnlyList<RemoteMessage>> ListMessagesAsync(string folderId, DateTimeOffset? since, CancellationToken ct)
    {
        var messages = await sync.ListMessagesAsync(await token(ct).ConfigureAwait(false), folderId, since, ct).ConfigureAwait(false);
        return messages.Select(m => new RemoteMessage(m.Id, m.ChangeKey, m.Received, m.IsRead, m.Flagged)).ToList();
    }

    public async Task<byte[]> GetMimeAsync(string messageId, CancellationToken ct) =>
        await inbox.GetMessageMimeAsync(await token(ct).ConfigureAwait(false), messageId, ct).ConfigureAwait(false);
}
