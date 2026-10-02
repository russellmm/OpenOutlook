using System.Text;

namespace PstCore;

public sealed class PstStore : IDisposable
{
    private readonly Ndb _ndb;
    private readonly Encoding _ansi;
    private readonly Dictionary<uint, MailFolder> _folders = [];
    private MailFolder? _root;

    public PstHeader Header { get; }
    public string DisplayName { get; }
    public bool CanWrite { get; }
    public MailFolder Root => _root ?? throw new PstException("PST root folder is missing.");

    private PstStore(Ndb ndb, bool canWrite)
    {
        _ndb = ndb;
        Header = ndb.Header;
        CanWrite = canWrite;
        _ansi = EncodingUtil.Ansi1252();
        DisplayName = ReadStoreDisplayName();
        LoadFolders();
    }

    public static PstStore Open(string path, bool writable = false)
    {
        var ndb = Ndb.Open(path, writable ? FileAccess.ReadWrite : FileAccess.Read);
        return new PstStore(ndb, writable);
    }

    public static PstHeader Inspect(string path) => Ndb.Inspect(path);

    public IEnumerable<MailFolder> AllFolders()
    {
        foreach (var f in _folders.Values.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            yield return f;
    }

    public MailFolder? FindFolder(uint nid) => _folders.GetValueOrDefault(nid);

    private string ReadStoreDisplayName()
    {
        try
        {
            var node = _ndb.GetNode(SpecialNids.MessageStore);
            var heap = HeapOnNode.Load(_ndb, node);
            var name = PropertyContext.Read(heap).GetString(Pid.DisplayName, _ansi).Trim();
            if (!string.IsNullOrWhiteSpace(name) && name.Length <= 256 && !name.Any(char.IsControl))
                return name;
        }
        catch (Exception) { /* A missing store name must not prevent read-only browsing. */ }
        return Path.GetFileNameWithoutExtension(Header.Path);
    }

    public IReadOnlyList<MailSummary> GetMessages(MailFolder folder)
    {
        Dictionary<uint, MailSummary> byNid = [];
        TableContext? table = null;
        try
        {
            table = TableContext.TryLoad(_ndb, folder.Nid | (uint)NidType.ContentsTable);
            if (table != null)
            {
                foreach (var row in table.Rows)
                {
                    var nid = row.RowId;
                    if ((nid & 0x1F) != (uint)NidType.NormalMessage)
                        nid = (nid & 0xFFFFFFE0) | (uint)NidType.NormalMessage;
                    byNid[nid] = SummaryFromRow(folder.Nid, nid, table, row);
                }
            }
        }
        catch
        {
            byNid = [];
            table = null;
        }

        // A folder with a Contents table shows exactly its rows - Outlook's own rule. The NBT parent
        // scan is only the fallback for stores whose folders have no readable table, because a Phase B
        // delete unlinks the row while the message node stays under the folder until some future GC;
        // unioning both views would resurrect every deleted message.
        List<MailSummary> list;
        if (table != null)
        {
            list = new List<MailSummary>(byNid.Values);
            foreach (var node in _ndb.Nodes)
            {
                if (node.Nid.Type != NidType.NormalMessage || node.Parent.Value != folder.Nid) continue;
                if (byNid.TryGetValue(node.Nid.Value, out var cached) && cached.Size <= 0)
                    cached.Size = MessageSizeFromNode(node);
            }
        }
        else
        {
            list = new List<MailSummary>();
            foreach (var node in _ndb.Nodes)
            {
                if (node.Nid.Type != NidType.NormalMessage) continue;
                if (node.Parent.Value != folder.Nid) continue;
                if (byNid.TryGetValue(node.Nid.Value, out var cached))
                {
                    if (cached.Size <= 0)
                        cached.Size = MessageSizeFromNode(node);
                    list.Add(cached);
                }
                else
                    list.Add(SummaryFromNode(folder.Nid, node));
            }
        }

        return list
            .OrderByDescending(m => m.Received == DateTime.MinValue ? m.Sent : m.Received)
            .ToList();
    }

    private int MessageSizeFromNode(NbtEntry node)
    {
        try
        {
            var heap = HeapOnNode.Load(_ndb, node);
            return Math.Max(0, PropertyContext.Read(heap).GetInt(Pid.MessageSize));
        }
        catch (Exception) { return 0; }
    }

    private MailSummary SummaryFromRow(uint folderNid, uint nid, TableContext table, TableRow row)
    {
        var flags = table.CellInt(row, Pid.MessageFlags);
        var subject = CleanSubject(table.CellString(row, Pid.Subject, _ansi));
        if (string.IsNullOrWhiteSpace(subject))
            subject = CleanSubject(table.CellString(row, Pid.NormalizedSubject, _ansi));

        var from = table.CellString(row, Pid.SenderName, _ansi);
        if (string.IsNullOrWhiteSpace(from))
            from = table.CellString(row, Pid.SentRepresentingName, _ansi);

        var received = table.CellTime(row, Pid.MessageDeliveryTime);
        if (received == DateTime.MinValue)
            received = table.CellTime(row, Pid.ClientSubmitTime);

        return new MailSummary
        {
            Nid = nid,
            FolderNid = folderNid,
            Subject = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject,
            From = from,
            To = table.CellString(row, Pid.DisplayTo, _ansi),
            Received = received,
            Sent = table.CellTime(row, Pid.ClientSubmitTime),
            IsRead = (flags & MailFlags.Read) != 0,
            HasAttachment = (flags & MailFlags.HasAttach) != 0,
            Flagged = table.CellInt(row, Pid.FlagStatus) != 0,
            Size = table.CellInt(row, Pid.MessageSize),
            MessageClass = table.CellString(row, Pid.MessageClass, _ansi),
            Importance = table.CellInt(row, Pid.Importance, 1)
        };
    }

    private MailSummary SummaryFromNode(uint folderNid, NbtEntry node)
    {
        try
        {
            var heap = HeapOnNode.Load(_ndb, node);
            var bag = PropertyContext.Read(heap);
            var subject = CleanSubject(StringProps.Best(bag, _ansi, Pid.Subject, Pid.NormalizedSubject));
            var flags = bag.GetInt(Pid.MessageFlags);
            var received = bag.GetTime(Pid.MessageDeliveryTime);
            if (received == DateTime.MinValue) received = bag.GetTime(Pid.ClientSubmitTime);
            return new MailSummary
            {
                Nid = node.Nid.Value,
                FolderNid = folderNid,
                Subject = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject,
                From = StringProps.Best(bag, _ansi, Pid.SenderName, Pid.SentRepresentingName),
                To = bag.GetString(Pid.DisplayTo, _ansi),
                Received = received,
                Sent = bag.GetTime(Pid.ClientSubmitTime),
                IsRead = (flags & MailFlags.Read) != 0,
                HasAttachment = (flags & MailFlags.HasAttach) != 0,
                Flagged = bag.GetInt(Pid.FlagStatus) != 0,
                Size = bag.GetInt(Pid.MessageSize),
                MessageClass = bag.GetString(Pid.MessageClass, _ansi),
                Importance = bag.GetInt(Pid.Importance, 1)
            };
        }
        catch
        {
            return new MailSummary
            {
                Nid = node.Nid.Value,
                FolderNid = folderNid,
                Subject = $"(unreadable 0x{node.Nid.Value:X})"
            };
        }
    }

    public MailMessage OpenMessage(MailSummary summary)
    {
        var node = _ndb.GetNode(summary.Nid);
        var heap = HeapOnNode.Load(_ndb, node);
        var bag = PropertyContext.Read(heap);
        var ansi = EncodingFor(bag);

        var subject = CleanSubject(StringProps.Best(bag, ansi, Pid.Subject, Pid.NormalizedSubject));

        var fromName = StringProps.Best(bag, ansi, Pid.SenderName, Pid.SentRepresentingName);
        var fromEmail = StringProps.Best(bag, ansi, Pid.SenderEmail, Pid.SentRepresentingEmail, Pid.SmtpAddress);
        var from = string.IsNullOrWhiteSpace(fromEmail) ? fromName : $"{fromName} <{fromEmail}>".Trim();

        var flags = bag.GetInt(Pid.MessageFlags);
        var bodyText = bag.GetString(Pid.Body, ansi);
        var bodyHtml = DecodeHtml(bag, ansi);
        if (string.IsNullOrWhiteSpace(bodyText) && bag.TryGet(Pid.RtfCompressed, out var rtfProp))
        {
            try
            {
                var rtf = RtfDecompressor.Decompress(rtfProp.Raw);
                bodyText = RtfDecompressor.ToPlainText(rtf);
            }
            catch
            {
                // keep empty
            }
        }

        var recipients = ReadRecipients(heap, ansi);
        var attachments = ReadAttachments(heap, ansi);

        var received = bag.GetTime(Pid.MessageDeliveryTime);
        if (received == DateTime.MinValue) received = bag.GetTime(Pid.ClientSubmitTime);

        summary.IsRead = (flags & MailFlags.Read) != 0;
        summary.Flagged = bag.GetInt(Pid.FlagStatus) != 0;

        return new MailMessage
        {
            Summary = new MailSummary
            {
                Nid = summary.Nid,
                FolderNid = summary.FolderNid,
                Subject = string.IsNullOrWhiteSpace(subject) ? summary.Subject : subject,
                From = string.IsNullOrWhiteSpace(from) ? summary.From : from,
                To = bag.GetString(Pid.DisplayTo, ansi),
                Received = received,
                Sent = bag.GetTime(Pid.ClientSubmitTime),
                IsRead = summary.IsRead,
                HasAttachment = attachments.Count > 0 || (flags & MailFlags.HasAttach) != 0,
                Flagged = summary.Flagged,
                Size = bag.GetInt(Pid.MessageSize, summary.Size),
                MessageClass = bag.GetString(Pid.MessageClass, ansi),
                Importance = bag.GetInt(Pid.Importance, 1)
            },
            BodyText = bodyText,
            BodyHtml = bodyHtml,
            Headers = bag.GetString(Pid.TransportHeaders, ansi),
            Recipients = recipients,
            Attachments = attachments
        };
    }

    public byte[] ReadAttachmentData(MailSummary message, MailAttachment attachment)
    {
        var node = _ndb.GetNode(message.Nid);
        var heap = HeapOnNode.Load(_ndb, node);
        if (!heap.SubNodes.TryGetValue(attachment.Nid, out var sub))
            throw new PstException("Attachment subnode was not found.");
        var attachHeap = HeapOnNode.Load(_ndb, sub.Data, _ndb.ReadSubNodes(sub.Sub));
        var bag = PropertyContext.Read(attachHeap);
        if (bag.TryGet(Pid.AttachData, out var data) && data.Raw.Length > 0)
            return data.Raw;
        if (attachHeap.SubNodes.Count > 0)
        {
            foreach (var s in attachHeap.SubNodes.Values)
            {
                try { return _ndb.ReadDataTree(s.Data); }
                catch { /* try next */ }
            }
        }
        return [];
    }

    /// <summary>
    /// Reads a by-value attachment without allocating an external PST data block whose
    /// declared size would exceed the remaining budget. The attachment's message and
    /// property-context heaps are independently limited to 1 MiB each; their BTH
    /// records and subnode indexes can still consume memory proportional to their
    /// (bounded) heap or PST metadata, rather than the attachment byte limit.
    /// </summary>
    public byte[] ReadAttachmentData(MailSummary message, MailAttachment attachment, int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (attachment.Method != 1)
                throw new PstException("Only by-value attachments are supported.");
            const int maxHeapBytes = 1024 * 1024;
            var node = _ndb.GetNode(message.Nid);
            var heap = HeapOnNode.LoadBounded(_ndb, node, maxHeapBytes, cancellationToken);
            if (!heap.SubNodes.TryGetValue(attachment.Nid, out var sub))
                throw new PstException("Attachment subnode was not found.");
            var attachHeap = HeapOnNode.LoadBounded(_ndb, sub.Data,
                _ndb.ReadSubNodes(sub.Sub, cancellationToken), maxHeapBytes, cancellationToken);

            // Do not use PropertyContext.Read: that eagerly resolves *every* property,
            // including a potentially unbounded attachment data subnode.
            foreach (var (key, record, _, _) in attachHeap.WalkBth(attachHeap.UserRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((ushort)key != Pid.AttachData) continue;
                if (record.Length < 8)
                    throw new PstException("Attachment data property is malformed.");
                var type = BinaryUtil.ReadU16(record, 2);
                if (type != PropType.Binary)
                    throw new PstException("Attachment data property type is unsupported.");
                var hnid = BinaryUtil.ReadU32(record, 4);
                var value = attachHeap.GetItem(hnid, maxBytes, cancellationToken);
                if (value.Length > 0) return value;
                break;
            }
            // A nested subnode is not necessarily attachment payload. The legacy
            // reader chooses the first arbitrarily; a safe reader must fail closed.
            if (attachHeap.SubNodes.Count != 0)
                throw new PstException("Attachment data identity is ambiguous.");
            return [];
        }
        catch (OperationCanceledException) { throw; }
        catch (PstException exception) when (exception.Message == "Attachment exceeds the permitted size." ||
                                             exception.Message == "Only by-value attachments are supported.")
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Do not expose raw PST offsets, property values or lower-level exception text.
            throw new PstException("Attachment data could not be read safely.");
        }
    }

    public IReadOnlyList<MailSummary> Search(string query, MailFolder? folder = null)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        var results = new List<MailSummary>();
        IEnumerable<MailFolder> folders = folder == null ? AllFolders() : Walk(folder);
        foreach (var f in folders)
        {
            foreach (var m in GetMessages(f))
            {
                if (Contains(m.Subject, query) || Contains(m.From, query) || Contains(m.To, query))
                {
                    results.Add(m);
                    continue;
                }
                try
                {
                    var full = OpenMessage(m);
                    if (Contains(full.BodyText, query) || Contains(full.Summary.Subject, query) || Contains(full.Headers, query))
                        results.Add(m);
                }
                catch
                {
                    // skip unreadable messages
                }
            }
        }
        return results;
    }

    public void SetReadState(MailSummary message, bool read)
    {
        EnsureWritable();
        var node = _ndb.GetNode(message.Nid);
        var heap = HeapOnNode.Load(_ndb, node);
        var bag = PropertyContext.Read(heap);
        var flags = bag.GetInt(Pid.MessageFlags);
        var wasRead = (flags & MailFlags.Read) != 0;
        flags = read ? flags | MailFlags.Read : flags & ~MailFlags.Read;

        // PR_MESSAGE_FLAGS lives twice: in the item's own property heap and in the folder Contents
        // Table row that drives the message list. Both copies are validated before either is written,
        // so a failure leaves the archive untouched instead of desynchronising the two views.
        var table = TableContext.TryLoad(_ndb, message.FolderNid | (uint)NidType.ContentsTable);
        var cellBytes = BitConverter.GetBytes((uint)flags);
        if (!(table?.TryPatchCell(message.Nid, Pid.MessageFlags, cellBytes, dryRun: true) ?? false))
            throw new PstException("Could not locate this message's row in the folder contents table; nothing was changed.");
        if (!PropertyContext.TryPatchFixedUInt32(heap, Pid.MessageFlags, (uint)flags, dryRun: true))
            throw new PstException("Could not update the message flags in this PST (the property is not a 4-byte in-heap value); nothing was changed.");

        table!.TryPatchCell(message.Nid, Pid.MessageFlags, cellBytes);
        PropertyContext.TryPatchFixedUInt32(heap, Pid.MessageFlags, (uint)flags);
        message.IsRead = read;
        if (wasRead != read) AdjustFolderUnread(message.FolderNid, read ? -1 : +1);
    }

    /// <summary>Removes a message from its folder for real (Phase B delete): unlinks the TCROWID record
    /// from the folder's Contents Row-ID BTree through the proven same-size block rewrite, then orphans
    /// everything the row pointed at - matrix slot, item property context, recipients, attachments.
    /// Nothing is freed and the archive never shrinks; that is spec-legal garbage that Outlook itself
    /// produces for deleted mail until a manual cleanup. The folder's cached ContentCount/UnreadCount
    /// are adjusted best-effort (they are calculated properties; Outlook recomputes them anyway).
    /// A failed attempt writes nothing: the unlink is dry-run validated first.</summary>
    public void DeleteMessage(MailSummary message)
    {
        EnsureWritable();
        var table = TableContext.TryLoad(_ndb, message.FolderNid | (uint)NidType.ContentsTable)
            ?? throw new PstException("This folder has no readable contents table; nothing was changed.");
        if (!table.TryUnlinkRow(message.Nid, dryRun: true))
            throw new PstException(
                "Could not locate this message's row in the folder contents table (or its storage is not rewritable); nothing was changed.");
        var wasUnread = !message.IsRead;
        if (!table.TryUnlinkRow(message.Nid))
            throw new PstException("The message row disappeared while deleting; nothing further was changed.");
        AdjustFolderCounts(message.FolderNid, -1, wasUnread ? -1 : 0);
    }

    /// <summary>Keeps the folder's badges honest after edits. A failure here never fails the caller's
    /// edit: the message itself persisted, and Outlook recomputes these counts on its own.</summary>
    private void AdjustFolderCounts(uint folderNid, int contentDelta, int unreadDelta)
    {
        if (contentDelta == 0 && unreadDelta == 0) return;
        if (!_folders.TryGetValue(folderNid, out var folder)) return;
        try
        {
            var node = _ndb.GetNode(folder.Nid);
            var heap = HeapOnNode.Load(_ndb, node);
            if (contentDelta != 0)
            {
                var current = PropertyContext.Read(heap).GetInt(Pid.ContentCount);
                if (current >= 0)
                {
                    var updated = (uint)Math.Max(0, current + contentDelta);
                    if (PropertyContext.TryPatchFixedUInt32(heap, Pid.ContentCount, updated))
                        folder.ContentCount = (int)updated;
                }
            }
            if (unreadDelta != 0)
            {
                var current = PropertyContext.Read(heap).GetInt(Pid.ContentUnread);
                if (current >= 0)
                {
                    var updated = (uint)Math.Max(0, current + unreadDelta);
                    if (PropertyContext.TryPatchFixedUInt32(heap, Pid.ContentUnread, updated))
                        folder.UnreadCount = (int)updated;
                }
            }
        }
        catch (PstException) { /* badge drift is tolerable */ }
    }

    private void AdjustFolderUnread(uint folderNid, int delta) => AdjustFolderCounts(folderNid, 0, delta);

    /// <summary>Full-file consistency check: every B-tree page CRC and every block trailer.</summary>
    public IReadOnlyList<string> VerifyIntegrity() => _ndb.VerifyIntegrity();

    public void SetFlagged(MailSummary message, bool flagged)
    {
        EnsureWritable();
        var node = _ndb.GetNode(message.Nid);
        var heap = HeapOnNode.Load(_ndb, node);
        if (!PropertyContext.TryPatchFixedUInt32(heap, Pid.FlagStatus, flagged ? 2u : 0u, dryRun: true))
            throw new PstException("Could not update the flag on this message. The flag property may be missing from the original PST; nothing was changed.");
        var table = TableContext.TryLoad(_ndb, message.FolderNid | (uint)NidType.ContentsTable);
        // Best effort on the list-view copy: the FlagStatus column is optional in some tables. The
        // authoritative item property is written below; the badge refreshes when the folder reloads.
        table?.TryPatchCell(message.Nid, Pid.FlagStatus, BitConverter.GetBytes(flagged ? 2u : 0u));
        PropertyContext.TryPatchFixedUInt32(heap, Pid.FlagStatus, flagged ? 2u : 0u);
        message.Flagged = flagged;
    }

    [Obsolete("Not a real move: it only rewrites the NID parent while the row stays in the source folder's Contents BTree. Replaced by true contents-row moves (see docs/pst-editing-design.md Phase C).")]
    public void MoveMessage(MailSummary message, MailFolder destination)
    {
        EnsureWritable();
        if (message.FolderNid == destination.Nid) return;
        var node = _ndb.GetNode(message.Nid);
        _ndb.RewriteNidParent(node, new Nid(destination.Nid));
        message.FolderNid = destination.Nid;
    }

    public MailFolder? DeletedItemsFolder()
    {
        foreach (var f in _folders.Values)
        {
            if (f.Name.Equals("Deleted Items", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Equals("Trash", StringComparison.OrdinalIgnoreCase))
                return f;
        }
        return null;
    }

    public void Dispose() => _ndb.Dispose();

    private void LoadFolders()
    {
        foreach (var node in _ndb.Nodes)
        {
            if (node.Nid.Type != NidType.NormalFolder && node.Nid.Value != SpecialNids.RootFolder)
                continue;
            try
            {
                var heap = HeapOnNode.Load(_ndb, node);
                var bag = PropertyContext.Read(heap);
                var name = bag.GetString(Pid.DisplayName, _ansi);
                if (string.IsNullOrWhiteSpace(name))
                    name = node.Nid.Value == SpecialNids.RootFolder ? "Root" : $"Folder 0x{node.Nid.Value:X}";
                var parent = node.Parent.Value;
                _folders[node.Nid.Value] = new MailFolder
                {
                    Nid = node.Nid.Value,
                    Name = name,
                    ParentNid = parent,
                    ContentCount = bag.GetInt(Pid.ContentCount),
                    UnreadCount = bag.GetInt(Pid.ContentUnread),
                    HasSubfolders = bag.GetInt(Pid.Subfolders) != 0
                };
            }
            catch
            {
                _folders[node.Nid.Value] = new MailFolder
                {
                    Nid = node.Nid.Value,
                    Name = node.Nid.Value == SpecialNids.RootFolder ? "Root" : $"Folder 0x{node.Nid.Value:X}",
                    ParentNid = node.Parent.Value
                };
            }
        }

        foreach (var folder in _folders.Values)
        {
            if (folder.ParentNid != 0 && _folders.TryGetValue(folder.ParentNid, out var parent) && parent.Nid != folder.Nid)
                parent.Children.Add(folder);
        }

        foreach (var folder in _folders.Values)
            folder.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        if (_folders.TryGetValue(SpecialNids.RootFolder, out var root))
            _root = root;
        else
            _root = _folders.Values.FirstOrDefault(f => f.ParentNid == 0) ?? _folders.Values.FirstOrDefault();
    }

    private List<MailRecipient> ReadRecipients(HeapOnNode messageHeap, Encoding ansi)
    {
        var list = new List<MailRecipient>();
        var recNid = (uint)NidType.RecipientTable;
        if (!messageHeap.SubNodes.TryGetValue(recNid, out var sub) &&
            !messageHeap.SubNodes.TryGetValue(0x612, out sub))
        {
            foreach (var s in messageHeap.SubNodes.Values)
            {
                if (s.Nid.Type == NidType.RecipientTable) { sub = s; break; }
            }
        }
        if (sub == null) return list;
        try
        {
            var heap = HeapOnNode.Load(_ndb, sub.Data, []);
            var table = TableContext.Load(heap);
            foreach (var row in table.Rows)
            {
                var name = table.CellString(row, Pid.DisplayName, ansi);
                var email = table.CellString(row, Pid.SmtpAddress, ansi);
                if (string.IsNullOrWhiteSpace(email))
                    email = table.CellString(row, Pid.EmailAddress, ansi);
                var kind = (RecipientKind)table.CellInt(row, Pid.RecipientType, 1);
                if (kind is not (RecipientKind.To or RecipientKind.Cc or RecipientKind.Bcc))
                    kind = RecipientKind.To;
                list.Add(new MailRecipient { Name = name, Email = email, Kind = kind });
            }
        }
        catch
        {
            // recipient table optional
        }
        return list;
    }

    private List<MailAttachment> ReadAttachments(HeapOnNode messageHeap, Encoding ansi)
    {
        var list = new List<MailAttachment>();
        SubNodeEntry? tableSub = null;
        foreach (var s in messageHeap.SubNodes.Values)
        {
            if (s.Nid.Type == NidType.AttachmentTable) { tableSub = s; break; }
        }
        IEnumerable<SubNodeEntry> attachNodes = messageHeap.SubNodes.Values.Where(s => s.Nid.Type == NidType.Attachment);
        if (tableSub != null)
        {
            try
            {
                var heap = HeapOnNode.Load(_ndb, tableSub.Data, []);
                var table = TableContext.Load(heap);
                foreach (var row in table.Rows)
                {
                    var nid = row.RowId;
                    if ((nid & 0x1F) != (uint)NidType.Attachment)
                        nid = (nid & 0xFFFFFFE0) | (uint)NidType.Attachment;
                    var name = table.CellString(row, Pid.AttachLongFilename, ansi);
                    if (string.IsNullOrWhiteSpace(name))
                        name = table.CellString(row, Pid.AttachFilename, ansi);
                    list.Add(new MailAttachment
                    {
                        Nid = nid,
                        FileName = string.IsNullOrWhiteSpace(name) ? "attachment.bin" : name,
                        MimeTag = table.CellString(row, Pid.AttachMimeTag, ansi),
                        Size = table.CellInt(row, Pid.AttachSize),
                        Method = table.CellInt(row, Pid.AttachMethod),
                        ContentId = table.CellString(row, Pid.AttachContentId, ansi)
                    });
                }
                if (list.Count > 0) return list;
            }
            catch
            {
                // fall back to walking attachment PCs
            }
        }

        foreach (var s in attachNodes)
        {
            try
            {
                var heap = HeapOnNode.Load(_ndb, s.Data, []);
                var bag = PropertyContext.Read(heap);
                var name = StringProps.Best(bag, ansi, Pid.AttachLongFilename, Pid.AttachFilename, Pid.DisplayName);
                list.Add(new MailAttachment
                {
                    Nid = s.Nid.Value,
                    FileName = string.IsNullOrWhiteSpace(name) ? "attachment.bin" : name,
                    MimeTag = bag.GetString(Pid.AttachMimeTag, ansi),
                    Size = bag.GetInt(Pid.AttachSize),
                    Method = bag.GetInt(Pid.AttachMethod),
                    ContentId = bag.GetString(Pid.AttachContentId, ansi)
                });
            }
            catch
            {
                list.Add(new MailAttachment { Nid = s.Nid.Value, FileName = "attachment.bin" });
            }
        }
        return list;
    }

    private static string DecodeHtml(PropertyBag bag, Encoding ansi)
    {
        if (!bag.TryGet(Pid.BodyHtml, out var html)) return "";
        if (html.Type == PropType.Unicode || html.Type == PropType.String8)
            return bag.GetString(Pid.BodyHtml, ansi);
        if (html.Raw.Length == 0) return "";
        if (html.Raw.Length >= 2 && html.Raw[1] == 0)
            return BinaryUtil.DecodeString(html.Raw, true, ansi);
        return ansi.GetString(html.Raw);
    }

    private Encoding EncodingFor(PropertyBag bag)
    {
        var cp = bag.GetInt(Pid.InternetCodepage);
        if (cp == 0) cp = bag.GetInt(Pid.MessageCodepage);
        if (cp == 0) return _ansi;
        return EncodingUtil.FromCodePage(cp);
    }

    private static IEnumerable<MailFolder> Walk(MailFolder folder)
    {
        yield return folder;
        foreach (var child in folder.Children)
        foreach (var n in Walk(child))
            yield return n;
    }

    private static bool Contains(string? hay, string needle) =>
        !string.IsNullOrEmpty(hay) && hay.Contains(needle, StringComparison.CurrentCultureIgnoreCase);

    internal static string CleanSubject(string subject)
    {
        if (string.IsNullOrEmpty(subject)) return subject;
        var i = 0;
        while (i < subject.Length && subject[i] < ' ') i++;
        return i == 0 ? subject : subject[i..];
    }

    private void EnsureWritable()
    {
        if (!CanWrite)
            throw new PstException("This PST was opened read-only. Re-open it with write access to edit.");
    }
}
