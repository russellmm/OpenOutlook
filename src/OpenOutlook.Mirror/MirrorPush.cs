using PstCore;

namespace OpenOutlook.Mirror;

/// <summary>What the server side of the mirror must be able to do for changes made in the local copy (Phase 2). A source that also implements this gets the changes pushed.</summary>
public interface IMailSyncSink
{
    /// <summary>False when the sign-in may only read: nothing is pushed then.</summary>
    bool CanPush { get; }
    Task SetReadAsync(string messageId, bool read, CancellationToken ct);
    Task SetFlaggedAsync(string messageId, bool flagged, CancellationToken ct);
    /// <summary>Moves a message to another folder; the server may give it a new id, which is returned.</summary>
    Task<string> MoveAsync(string messageId, string destinationFolderId, CancellationToken ct);
    /// <summary>Removes a message for good (it is in Deleted Items already).</summary>
    Task PurgeAsync(string messageId, CancellationToken ct);
    /// <summary>Creates a folder (parentId null = at the top of the mailbox); returns its id.</summary>
    Task<string> CreateFolderAsync(string? parentId, string name, CancellationToken ct);
}

public enum LocalChangeKind { Read, Flag, Move, Purge }

/// <summary>One difference between the local copy and what was last synchronised.</summary>
public sealed record LocalChange(LocalChangeKind Kind, MirrorMessageState Message, string FolderRemoteId, string? TargetFolderRemoteId, bool Value, uint PstNid);

/// <summary>A folder created in the local copy that the server does not have yet.</summary>
public sealed record LocalFolder(uint PstNid, string Name, uint ParentPstNid);

public sealed record LocalScan(IReadOnlyList<LocalChange> Changes, IReadOnlyList<LocalFolder> NewFolders, int LocalOnlyMessages)
{
    public int Pending => Changes.Count + NewFolders.Count;
}

/// <summary>
/// Finds what changed in the local copy since the last sync by comparing the PST with the state (read and flag state, folder of every message, messages that
/// are gone, folders the state does not know), and sends it to the server. Server changes win when both sides changed the same property; a failed or
/// offline push leaves the difference in place, so the next sync tries again.
/// </summary>
public static class MirrorPush
{
    /// <summary>Stands for a folder that exists only in the local copy so far: "local:" + its PST node id.</summary>
    public const string LocalPrefix = "local:";

    /// <summary>Compares the PST with the state. Needs no network.</summary>
    public static LocalScan Scan(IPstEngine pst, SyncStateStore state, MailFolder top, MailFolder deleted)
    {
        var folders = state.Folders();
        var byPstNid = folders.ToDictionary(f => f.PstNid, f => f.RemoteId);

        // folders created in the local copy that the server does not have yet (parents before children)
        var knownFolders = new HashSet<uint>(folders.Select(f => f.PstNid)) { deleted.Nid, top.Nid };
        var newFolders = new List<LocalFolder>();
        foreach (var f in pst.AllFolders())
        {
            if (knownFolders.Contains(f.Nid) || f.ParentNid == 0) continue;
            var parentOk = f.ParentNid == top.Nid || byPstNid.ContainsKey(f.ParentNid) || newFolders.Any(n => n.PstNid == f.ParentNid);
            if (parentOk) newFolders.Add(new LocalFolder(f.Nid, f.Name, f.ParentNid));
        }
        newFolders = newFolders.OrderBy(n => Depth(pst, n.PstNid, top.Nid)).ToList();

        var location = new Dictionary<uint, (string FolderRemoteId, MailSummary Summary)>();
        foreach (var f in folders)
        {
            var folder = pst.FindFolder(f.PstNid);
            if (folder is null) continue;
            foreach (var s in pst.GetMessages(folder)) location[s.Nid] = (f.RemoteId, s);
        }
        foreach (var nf in newFolders)                                                    // a message moved into such a folder is not a deleted message
        {
            var folder = pst.FindFolder(nf.PstNid);
            if (folder is null) continue;
            foreach (var s in pst.GetMessages(folder)) location[s.Nid] = (LocalPrefix + nf.PstNid, s);
        }

        var changes = new List<LocalChange>();
        var known = new HashSet<uint>();
        foreach (var f in folders)
        {
            foreach (var m in state.MessagesIn(f.RemoteId))
            {
                if (m.PstNid == 0) continue;
                known.Add(m.PstNid);
                if (!location.TryGetValue(m.PstNid, out var at))
                {
                    changes.Add(new LocalChange(LocalChangeKind.Purge, m, f.RemoteId, null, false, m.PstNid));
                    continue;
                }
                if (at.FolderRemoteId != f.RemoteId)
                {
                    changes.Add(new LocalChange(LocalChangeKind.Move, m, f.RemoteId, at.FolderRemoteId, false, m.PstNid));
                    continue;
                }
                if (at.Summary.IsRead != m.IsRead) changes.Add(new LocalChange(LocalChangeKind.Read, m, f.RemoteId, null, at.Summary.IsRead, m.PstNid));
                var flagged = at.Summary.Flagged || IsFlagged(pst, at.Summary);
                if (flagged != m.Flagged) changes.Add(new LocalChange(LocalChangeKind.Flag, m, f.RemoteId, null, flagged, m.PstNid));
            }
        }
        var localOnly = location.Keys.Count(nid => !known.Contains(nid));

        return new LocalScan(changes, newFolders, localOnly);
    }

    private static int Depth(IPstEngine pst, uint nid, uint topNid)
    {
        var d = 0;
        for (var f = pst.FindFolder(nid); f is not null && f.Nid != topNid && d < 50; f = pst.FindFolder(f.ParentNid)) d++;
        return d;
    }

    private static bool IsFlagged(IPstEngine pst, MailSummary summary)
    {
        try { return pst.OpenMessage(summary).Summary.Flagged; }
        catch (PstException) { return false; }
    }

    public sealed record PushResult(int Pushed, int Failed, bool Offline, string? FirstError, ISet<string> TouchedFolders);

    /// <summary>
    /// Sends the scanned changes. <paramref name="listAsync"/> gives the server's current list of a folder (for the conflict rule); <paramref name="mapped"/> maps remote
    /// folder ids to PST folders and receives the folders created here.
    /// </summary>
    public static async Task<PushResult> PushAsync(IMailSyncSink sink, LocalScan scan, IPstEngine pst, SyncStateStore state, MailFolder top, MailFolder deleted,
        Dictionary<string, MailFolder> mapped, Func<string, Task<IReadOnlyList<RemoteMessage>>> listAsync, CancellationToken ct)
    {
        int pushed = 0, failed = 0;
        string? firstError = null;
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var offline = false;
        var deletedRemote = mapped.FirstOrDefault(p => ReferenceEquals(p.Value, deleted) || p.Value.Nid == deleted.Nid).Key;
        void Fail(Exception e) { failed++; firstError ??= e.Message; }

        // new folders first, so that moves into them have somewhere to go
        var created = new Dictionary<uint, string>();
        foreach (var nf in scan.NewFolders)
        {
            if (offline) break;
            ct.ThrowIfCancellationRequested();
            string? parentRemote = null;
            if (nf.ParentPstNid != top.Nid)
            {
                parentRemote = created.TryGetValue(nf.ParentPstNid, out var c) ? c : mapped.FirstOrDefault(p => p.Value.Nid == nf.ParentPstNid).Key;
                if (parentRemote is null) continue;
            }
            try
            {
                var id = await sink.CreateFolderAsync(parentRemote, nf.Name, ct).ConfigureAwait(false);
                created[nf.PstNid] = id;
                var folder = pst.FindFolder(nf.PstNid);
                if (folder is not null) mapped[id] = folder;
                state.UpsertFolder(new MirrorFolderState(id, nf.PstNid, null, null));
                pushed++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (IsOffline(e)) { offline = true; Fail(e); }
            catch (Exception e) { Fail(e); }
        }

        foreach (var change in scan.Changes)
        {
            if (offline) break;
            ct.ThrowIfCancellationRequested();
            var m = change.Message;
            try
            {
                switch (change.Kind)
                {
                    case LocalChangeKind.Read:
                    case LocalChangeKind.Flag:
                    {
                        var remote = (await listAsync(change.FolderRemoteId).ConfigureAwait(false)).FirstOrDefault(r => r.Id == m.RemoteId);
                        if (remote is null) continue;                                           // gone from the server: the pull removes the local copy
                        if (change.Kind == LocalChangeKind.Read)
                        {
                            if (remote.IsRead != m.IsRead) continue;                            // changed on the server too: the server wins
                            await sink.SetReadAsync(m.RemoteId, change.Value, ct).ConfigureAwait(false);
                            state.UpsertMessage((state.GetMessage(m.RemoteId, change.FolderRemoteId) ?? m) with { IsRead = change.Value });
                        }
                        else
                        {
                            if (remote.Flagged != m.Flagged) continue;
                            await sink.SetFlaggedAsync(m.RemoteId, change.Value, ct).ConfigureAwait(false);
                            state.UpsertMessage((state.GetMessage(m.RemoteId, change.FolderRemoteId) ?? m) with { Flagged = change.Value });          // the row as the read change just left it
                        }
                        touched.Add(change.FolderRemoteId);                                       // the server's list changed: the pull must read it again
                        pushed++;
                        break;
                    }
                    case LocalChangeKind.Move:
                    {
                        var destination = change.TargetFolderRemoteId!;
                        if (destination.StartsWith(LocalPrefix, StringComparison.Ordinal))
                        {
                            if (!uint.TryParse(destination[LocalPrefix.Length..], out var targetNid) || !created.TryGetValue(targetNid, out var createdId)) continue;       // its folder could not be created yet: wait
                            destination = createdId;
                        }
                        var newId = await sink.MoveAsync(m.RemoteId, destination, ct).ConfigureAwait(false);
                        state.InTransaction(() =>
                        {
                            state.DeleteMessage(m.RemoteId, change.FolderRemoteId);
                            state.UpsertMessage(m with { RemoteId = newId, FolderRemoteId = destination });
                        });
                        touched.Add(change.FolderRemoteId); touched.Add(destination);
                        pushed++;
                        break;
                    }
                    case LocalChangeKind.Purge:
                    {
                        if (change.FolderRemoteId == deletedRemote) await sink.PurgeAsync(m.RemoteId, ct).ConfigureAwait(false);
                        else if (deletedRemote is not null) await sink.MoveAsync(m.RemoteId, deletedRemote, ct).ConfigureAwait(false);       // removed outside Deleted Items: the server keeps it in Deleted Items
                        state.DeleteMessage(m.RemoteId, change.FolderRemoteId);
                        touched.Add(change.FolderRemoteId);
                        if (deletedRemote is not null) touched.Add(deletedRemote);
                        pushed++;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (IsOffline(e)) { offline = true; Fail(e); }
            catch (Exception e) { Fail(e); }
        }
        return new PushResult(pushed, failed, offline, firstError, touched);
    }

    /// <summary>A network failure (as opposed to the server refusing one change).</summary>
    public static bool IsOffline(Exception e) =>
        e is HttpRequestException { StatusCode: null } || e is System.Net.Sockets.SocketException || e is IOException { InnerException: System.Net.Sockets.SocketException } ||
        e is TaskCanceledException { InnerException: TimeoutException };
}
