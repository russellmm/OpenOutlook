using PstCore;

namespace OpenOutlook.Mirror;

public sealed record RemoteFolder(string Id, string? ParentId, string Name, string? WellKnown);
public sealed record RemoteMessage(string Id, string? ChangeKey, DateTimeOffset? Received, bool IsRead, bool Flagged);

/// <summary>What a mail provider has to offer the mirror. Everything is read-only: the mirror never changes the server in this direction.</summary>
public interface IMailSyncSource
{
    /// <summary>Every folder of the mailbox, parents before children is not required. WellKnown is "deleteditems" for the folder that maps to the PST's Deleted Items.</summary>
    Task<IReadOnlyList<RemoteFolder>> GetFoldersAsync(CancellationToken ct);
    /// <summary>All messages of a folder received on or after <paramref name="since"/> (null = all).</summary>
    Task<IReadOnlyList<RemoteMessage>> ListMessagesAsync(string folderId, DateTimeOffset? since, CancellationToken ct);
    /// <summary>The message as MIME (an .eml).</summary>
    Task<byte[]> GetMimeAsync(string messageId, CancellationToken ct);
}

public sealed record MirrorSyncOptions(int KeepMonths = 12, long MaxAttachmentBytes = 25L * 1024 * 1024);
public sealed record MirrorProgress(string Phase, string? Folder, int Done, int Total);
public sealed record MirrorSyncResult(int FoldersCreated, int FoldersRemoved, int MessagesAdded, int MessagesRemoved, int MessagesUpdated, int Skipped, int Failed, string? FirstError, string? FirstSkipReason = null)
{
    public bool Changed => FoldersCreated + FoldersRemoved + MessagesAdded + MessagesRemoved + MessagesUpdated > 0;
}

/// <summary>
/// One-way sync (server to local copy) of a mailbox into a PST: the folder tree, then for every folder the messages of the keep-window. Messages are
/// compared by server id and read / flag state, so a run only downloads what is new, removes what is gone from the server (or fell out of the window)
/// and updates changed flags. The state of what was mirrored is kept in <see cref="SyncStateStore"/>; a crash leaves a state that the next run continues.
/// </summary>
public static class MirrorSyncEngine
{
    private const int BatchMessages = 100;
    private const long BatchBytes = 32L * 1024 * 1024;

    public static async Task<MirrorSyncResult> SyncAsync(IMailSyncSource source, IPstEngine pst, SyncStateStore state, MirrorSyncOptions options,
        IProgress<MirrorProgress>? progress, CancellationToken ct)
    {
        if (!pst.CanWrite) throw new InvalidOperationException("The mirror file is not open for writing.");
        int created = 0, removedFolders = 0, added = 0, removed = 0, updated = 0, skipped = 0, failed = 0;
        string? firstError = null, firstSkipReason = null;
        void Fail(Exception e) { failed++; firstError ??= e.Message; }

        var deleted = pst.DeletedItemsFolder() ?? throw new InvalidOperationException("The mirror file has no Deleted Items folder.");
        var top = pst.FindFolder(deleted.ParentNid) ?? throw new InvalidOperationException("The mirror file has no top folder.");

        // ---- folders
        progress?.Report(new MirrorProgress("folders", null, 0, 0));
        var remote = await source.GetFoldersAsync(ct).ConfigureAwait(false);
        var byId = remote.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var mapped = new Dictionary<string, MailFolder>(StringComparer.Ordinal);       // remote id -> PST folder
        foreach (var f in OrderParentsFirst(remote))
        {
            ct.ThrowIfCancellationRequested();
            var parent = f.ParentId is not null && mapped.TryGetValue(f.ParentId, out var p) ? p : top;
            MailFolder? have = null;
            var known = state.GetFolder(f.Id);
            if (known is not null) have = pst.FindFolder(known.PstNid);
            if (f.WellKnown == "deleteditems") have = deleted;
            if (have is null)
            {
                var name = UniqueName(pst, parent, f.Name);
                have = pst.CreateFolder(parent.Nid, name);
                created++;
            }
            else if (!ReferenceEquals(have, deleted))
            {
                if (have.ParentNid != parent.Nid) { try { pst.MoveFolder(have.Nid, parent.Nid); } catch (PstException e) { Fail(e); } }
                have = pst.FindFolder(have.Nid) ?? have;
                if (!string.Equals(have.Name, f.Name, StringComparison.Ordinal) && !StartsWithName(have.Name, f.Name))
                {
                    try { pst.RenameFolder(have.Nid, UniqueName(pst, pst.FindFolder(have.ParentNid) ?? parent, f.Name)); } catch (PstException e) { Fail(e); }
                    have = pst.FindFolder(have.Nid) ?? have;
                }
            }
            mapped[f.Id] = have;
            state.UpsertFolder(new MirrorFolderState(f.Id, have.Nid, known?.SyncToken, known?.LastSync));
        }
        // folders that no longer exist on the server (never the special Deleted Items)
        foreach (var gone in state.Folders().Where(s => !byId.ContainsKey(s.RemoteId)).ToList())
        {
            var folder = pst.FindFolder(gone.PstNid);
            if (folder is not null && !ReferenceEquals(folder, deleted))
            {
                try { pst.PurgeFolder(folder.Nid); removedFolders++; } catch (PstException e) { Fail(e); continue; }
            }
            state.DeleteFolder(gone.RemoteId);
        }

        // ---- messages
        var since = options.KeepMonths > 0 ? DateTimeOffset.UtcNow.AddMonths(-options.KeepMonths) : (DateTimeOffset?)null;
        var maxMime = options.MaxAttachmentBytes + 8L * 1024 * 1024;
        var folderIndex = 0;
        foreach (var (remoteId, folder) in mapped.ToList())
        {
            ct.ThrowIfCancellationRequested();
            folderIndex++;
            IReadOnlyList<RemoteMessage> list;
            try { list = await source.ListMessagesAsync(remoteId, since, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Fail(e); continue; }
            var have = state.MessagesIn(remoteId).ToDictionary(m => m.RemoteId, StringComparer.Ordinal);
            var listed = new HashSet<string>(list.Select(m => m.Id), StringComparer.Ordinal);

            // removed from the server or aged out of the window
            var gone = have.Values.Where(m => !listed.Contains(m.RemoteId)).ToList();
            if (gone.Count > 0)
            {
                var summaries = pst.GetMessages(folder).ToDictionary(s => s.Nid);
                foreach (var m in gone)
                {
                    if (m.PstNid != 0 && summaries.TryGetValue(m.PstNid, out var s))
                    {
                        try { pst.DeleteMessage(s); removed++; } catch (PstException e) { Fail(e); continue; }
                    }
                    state.DeleteMessage(m.RemoteId, remoteId);
                }
            }

            // changed read / flag state
            List<MailSummary>? current = null;
            foreach (var r in list)
            {
                if (!have.TryGetValue(r.Id, out var m) || m.PstNid == 0) continue;
                if (m.IsRead == r.IsRead && m.Flagged == r.Flagged) { if (m.ChangeKey != r.ChangeKey) state.UpsertMessage(m with { ChangeKey = r.ChangeKey }); continue; }
                current ??= pst.GetMessages(folder).ToList();
                var s = current.FirstOrDefault(x => x.Nid == m.PstNid);
                if (s is null) { state.DeleteMessage(r.Id, remoteId); have.Remove(r.Id); continue; }          // vanished from the file: downloaded again below
                try
                {
                    if (m.IsRead != r.IsRead) pst.SetReadState(s, r.IsRead);
                    if (m.Flagged != r.Flagged) pst.SetFlagged(s, r.Flagged);
                    state.UpsertMessage(m with { ChangeKey = r.ChangeKey, IsRead = r.IsRead, Flagged = r.Flagged });
                    updated++;
                }
                catch (PstException e) { Fail(e); }
            }

            // new messages, newest first, in batches
            var known = state.MessagesIn(remoteId).Select(m => m.RemoteId).ToHashSet(StringComparer.Ordinal);
            var fresh = list.Where(r => !known.Contains(r.Id)).ToList();
            var batch = new List<(RemoteMessage Remote, MailImport Mail)>();
            long batchBytes = 0;
            var done = 0;
            void Flush()
            {
                if (batch.Count == 0) return;
                try { Commit(batch); added += batch.Count; }
                catch (PstException) when (batch.Count > 1)
                {
                    foreach (var one in batch)                                    // one bad message must not cost the whole batch: find it
                    {
                        try { Commit([one]); added++; }
                        catch (PstException e) { Fail(new IOException($"message \"{one.Mail.Subject}\": {e.Message}", e)); }
                    }
                }
                catch (PstException e) { Fail(new IOException($"message \"{batch[0].Mail.Subject}\": {e.Message}", e)); }
                batch.Clear();
                batchBytes = 0;
            }
            void Commit(List<(RemoteMessage Remote, MailImport Mail)> items)
            {
                var imported = pst.ImportMessages(folder, items.Select(b => b.Mail).ToList());
                state.InTransaction(() =>
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        var r = items[i].Remote;
                        if (r.Flagged) { try { pst.SetFlagged(imported[i], true); } catch (PstException e) { Fail(e); } }
                        state.UpsertMessage(new MirrorMessageState(r.Id, remoteId, imported[i].Nid, r.ChangeKey, r.IsRead, r.Flagged));
                    }
                });
            }
            foreach (var r in fresh)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new MirrorProgress("messages", folder.Name, done++, fresh.Count));
                byte[] raw;
                try { raw = await source.GetMimeAsync(r.Id, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { Fail(e); continue; }
                if (raw.Length > maxMime)
                {
                    state.UpsertMessage(new MirrorMessageState(r.Id, remoteId, 0, r.ChangeKey, r.IsRead, r.Flagged));      // too large: remembered so it is not fetched on every run
                    skipped++;
                    continue;
                }
                MailImport mail;
                try { mail = EmlParser.Parse(raw, markRead: r.IsRead); }
                catch (Exception e) when (e is InvalidDataException or FormatException or ArgumentException)
                {
                    // not an RFC 822 message (a meeting or system item the server returns in another form): remembered so it is not fetched on every run
                    state.UpsertMessage(new MirrorMessageState(r.Id, remoteId, 0, r.ChangeKey, r.IsRead, r.Flagged));
                    skipped++;
                    firstSkipReason ??= e.Message + " (" + System.Text.Encoding.ASCII.GetString(raw, 0, Math.Min(raw.Length, 120)).Replace('\r', ' ').Replace('\n', ' ') + ")";
                    continue;
                }
                if (r.Received is { } rec) { mail.Received ??= rec.UtcDateTime; }
                batch.Add((r, mail));
                batchBytes += raw.Length;
                if (batch.Count >= BatchMessages || batchBytes >= BatchBytes) Flush();
            }
            Flush();
            state.UpsertFolder(new MirrorFolderState(remoteId, folder.Nid, state.GetFolder(remoteId)?.SyncToken, DateTime.UtcNow));
            progress?.Report(new MirrorProgress("folder done", folder.Name, folderIndex, mapped.Count));
        }
        state.SetMeta("last_sync", DateTime.UtcNow.ToString("O"));
        return new MirrorSyncResult(created, removedFolders, added, removed, updated, skipped, failed, firstError, firstSkipReason);
    }

    private static IEnumerable<RemoteFolder> OrderParentsFirst(IReadOnlyList<RemoteFolder> folders)
    {
        var ids = folders.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var pending = folders.ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(f => f.ParentId is null || !ids.Contains(f.ParentId) || emitted.Contains(f.ParentId)).ToList();
            if (ready.Count == 0) ready = [pending[0]];                // a cycle (cannot happen on a real server): break it
            foreach (var f in ready) { emitted.Add(f.Id); pending.Remove(f); yield return f; }
        }
    }

    private static bool StartsWithName(string have, string wanted) => have.StartsWith(wanted + " (", StringComparison.Ordinal);       // "Inbox (2)" is how a clash was resolved

    private static string UniqueName(IPstEngine pst, MailFolder parent, string name)
    {
        var taken = (pst.FindFolder(parent.Nid)?.Children ?? parent.Children).Select(c => c.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        name = name.Trim().Replace('/', '_').Replace('\\', '_');
        if (name.Length == 0) name = "Folder";
        if (name.Length > 200) name = name[..200];
        if (!taken.Contains(name)) return name;
        for (var n = 2; ; n++) { var candidate = $"{name} ({n})"; if (!taken.Contains(candidate)) return candidate; }
    }
}
