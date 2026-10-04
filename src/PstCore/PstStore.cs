using System.Text;

namespace PstCore;

public sealed class PstStore : IPstEngine
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

    /// <summary>
    /// Phase C move: re-links a message into another folder of the same archive without ever copying
    /// the item itself (MS-PST §2.6.3.2.8). Steps, all dry-run validated before any byte is written:
    /// build a destination matrix record from the source row's cells (fixed cells copied verbatim,
    /// variable cell values appended as fresh heap items in the destination table's heap), grow the
    /// Row-ID BTree leaf with {rowID = message NID, row-index = new matrix slot}, extend or create the
    /// row matrix, switch the table node to the rebuilt heap (copy-on-write: shared template heaps are
    /// never modified in place - real archives share them with BBT cRef up to 6), unlink the source
    /// row (Phase B primitive), and repoint the message's NBT parent. Allocation follows the append-only
    /// design: free slots from the block map, rightmost-leaf BBT appends, no splits, file never grows;
    /// fAMapValid goes INVALID before the batch and VALID last, so a crash mid-move leaves Outlook's
    /// safe rebuild path rather than silent corruption. Unsupported shapes (multi-block table heaps,
    /// multi-leaf matrices, multi-level Row-ID BTrees, oversized cells) refuse with nothing written.
    /// Callers must use this inside a PstEditSession so a failure can roll back from the backup.
    /// </summary>
    internal Ndb NdbForProbe => _ndb;

    public void MoveMessage(MailSummary message, MailFolder destFolder) => MoveOrCopy(message, destFolder, isCopy: false);

    /// <summary>Duplicate a message into another folder while the original stays exactly where it
    /// is. The copy gets a fresh NODE_B handle sharing the source's data blocks (native PST
    /// duplication semantics - real archives share block bids with BBT cRef above 1) and its own
    /// row in the destination table carrying the copy's NID; nothing about the original is touched.</summary>
    public void CopyMessage(MailSummary message, MailFolder destFolder) => MoveOrCopy(message, destFolder, isCopy: true);

    private void MoveOrCopy(MailSummary message, MailFolder destFolder, bool isCopy)
    {
        EnsureWritable();
        if (!_ndb.CanAllocate)
            throw new PstException(_ndb.WriteBlocker ?? "this archive cannot be written into; nothing was changed.");
        if (destFolder.Nid == message.FolderNid)
            throw new PstException("The message is already in that folder.");

        var srcTable = TableContext.TryLoad(_ndb, message.FolderNid | (uint)NidType.ContentsTable)
            ?? throw new PstException("The source folder has no readable contents table; nothing was changed.");
        if (!srcTable.TryUnlinkRow(message.Nid, dryRun: true))
            throw new PstException("Could not locate this message's row in the source folder; nothing was changed.");

        var dstNode = _ndb.TryGetNode(destFolder.Nid | (uint)NidType.ContentsTable, out var tn) ? tn
            : throw new PstException("The destination folder has no contents table; nothing was changed.");
        var dstTable = TableContext.TryLoad(_ndb, dstNode.Nid.Value)
            ?? throw new PstException("The destination contents table could not be parsed; nothing was changed.");
        if (dstTable.RowIndexMap.ContainsKey(message.Nid))
            throw new PstException("The destination folder already contains this message.");
        // Multi-block heaps are supported when reached through a single-level BREF indirection:
        // appends go to the last block, header items get patched in their own block's copy, and one
        // same-size BREF page write flips every repointed entry atomically. HIDs survive because
        // they encode (block ordinal << 16 | slot), and ordinals are preserved positionally.
        var heapBlocks = dstTable.Heap.Blocks;
        byte[]? brefPlan = null;
        if (heapBlocks.Count > 1 || dstNode.Data.IsInternal)
        {
            if (!dstNode.Data.IsInternal)
                throw new PstException("The destination table's data reference is unsupported; nothing was changed.");
            var brefRaw = _ndb.ReadRawBlockForWrite(dstNode.Data);
            if (brefRaw == null || brefRaw.Length < 8 || brefRaw[0] != 1 || brefRaw[1] != 1)
                throw new PstException("The destination table heap uses multi-level indirection; not supported yet.");
            var cEntB = BinaryUtil.ReadU16(brefRaw, 2);
            if (cEntB != heapBlocks.Count || brefRaw.Length != 8 + cEntB * 8)
                throw new PstException("The destination table heap's indirection list does not match its blocks; nothing was changed.");
            brefPlan = brefRaw;
        }
        // A fragmented row matrix is fine: the grow case writes a fresh contiguous block, and the
        // reuse case patches whichever block holds the target slot (TryPatchSubNodeBytes walks the
        // block chain). No upfront leaf requirement.

        var srcRow = srcTable.Rows.FirstOrDefault(r => r.RowId == message.Nid)
            ?? throw new PstException("The source row could not be read; nothing was changed.");
        var srcRaw = srcTable.GetRowRaw(message.Nid)!;

        // Copies need their own NID; a fresh global-max index (same type bits as the original) is
        // the same allocation rule folder creation uses and cannot collide with anything present.
        uint rowNid = message.Nid;
        if (isCopy)
        {
            uint maxIdx = 0;
            foreach (var n in _ndb.Nodes) { var i = n.Nid.Value >> 5; if (i > maxIdx) maxIdx = i; }
            rowNid = ((maxIdx + 1) << 5) | (message.Nid & 0x1Fu);
            if (_ndb.TryGetNode(rowNid, out _))
                throw new PstException("NID collision detected; nothing was changed.");
        }

        // ---- plan: destination matrix record ----
        var rec = new byte[dstTable.RowSize];
        var appended = new List<byte[]>();
        var variableCellTargets = new List<(int AppendedIndex, TableColumn Col)>();
        foreach (var col in dstTable.Columns)
        {
            if (col.IbData + col.CbData > rec.Length) continue;
            byte[]? value = null;
            if (col.Id == Pid.LtpRowId)
            {
                value = BitConverter.GetBytes(rowNid);
            }
            else
            {
                var srcCol = srcTable.Columns.FirstOrDefault(c => c.Id == col.Id);
                if (srcCol != null && srcCol.Type == col.Type && srcCol.CbData == col.CbData
                    && srcRow.Cells.ContainsKey(col.Id))
                {
                    if (!PropType.IsVariable(col.Type) && PropType.FixedSize(col.Type) is > 0 and <= 8)
                    {
                        value = new byte[col.CbData];
                        Array.Copy(srcRaw, srcCol.IbData, value, 0, col.CbData); // verbatim fixed cell bytes
                    }
                    else if (PropType.IsVariable(col.Type))
                    {
                        var hnid = col.CbData >= 4
                            ? BinaryUtil.ReadU32(srcRaw, srcCol.IbData)
                            : (col.CbData == 2 ? BinaryUtil.ReadU16(srcRaw, srcCol.IbData) : 0u);
                        if (hnid != 0)
                        {
                            byte[] payload;
                            try { payload = srcTable.Heap.GetItem(hnid); }
                            catch (PstException) { payload = []; }
                            if (payload.Length > 3580)
                                throw new PstException(
                                    "This message has an oversized field in its folder row; moving it is not supported yet.");
                            variableCellTargets.Add((appended.Count, col));
                            appended.Add(payload);
                        }
                    }
                }
            }
            if (value == null) continue;
            if (value.Length != col.CbData) continue; // never write a wrong-width cell
            value.CopyTo(rec.AsSpan(col.IbData));
            var existByte = dstTable.Tci1 + (col.IBit >> 3);
            if (existByte < rec.Length)
                rec[existByte] |= (byte)(1 << (col.IBit & 7));
        }

        // ---- plan: Row-ID BTree growth (+ its new leaf becomes one appended heap item) ----
        var bthHdr = dstTable.Heap.GetItem(dstTable.HidRowIndex);
        if (bthHdr.Length < 8 || bthHdr[1] != 4 || bthHdr[3] != 0)
            throw new PstException("The destination Row-ID index has an unsupported shape; nothing was changed.");
        var oldRootHid = BinaryUtil.ReadU32(bthHdr, 4);
        var oldLeaf = oldRootHid == 0 ? [] : dstTable.Heap.GetItem(oldRootHid);
        if (oldLeaf.Length % 8 != 0)
            throw new PstException("The destination Row-ID leaf is misaligned; nothing was changed.");
        var newRowIndex = dstTable.MaxRowIndex() + 1;
        var liveRecords = new List<byte[]>();
        for (var o = 0; o + 8 <= oldLeaf.Length; o += 8)
        {
            if (oldLeaf[o] == 0 && oldLeaf[o + 1] == 0 && oldLeaf[o + 2] == 0 && oldLeaf[o + 3] == 0) continue; // zeroed slot from a delete
            liveRecords.Add(oldLeaf.AsSpan(o, 8).ToArray());
        }
        var newLeaf = new byte[(liveRecords.Count + 1) * 8];
        var lo = 0;
        foreach (var r in liveRecords) { r.CopyTo(newLeaf.AsSpan(lo)); lo += 8; }
        BinaryUtil.WriteU32(newLeaf, lo, rowNid); // bRef key must equal the row's own LtpRowId (fresh NID for copies)
        BinaryUtil.WriteU32(newLeaf, lo + 4, (uint)newRowIndex);

        // The row's existence bitmap must match what this table's own rows carry: we only OR bits
        // for columns we actually fill, and a partial bitmap makes the record unparseable (real
        // archives skip such rows). Seed from any live row of the destination matrix; empty tables
        // keep the exact-columns-only bitmap.
        if (dstTable.HnidRows != 0)
        {
            var templateRaw = dstTable.Heap.GetItem(dstTable.HnidRows);
            foreach (var tIdx in dstTable.RowIndexMap.Values)
            {
                long toff = (long)tIdx * dstTable.RowSize;
                if (toff + dstTable.RowSize > templateRaw.Length || dstTable.RowSize < dstTable.Tci1 + 1) continue;
                var seed = true;
                for (var q = 0; q < 4 && toff + q < templateRaw.Length; q++) if (templateRaw[toff + q] != 0) { seed = false; break; }
                if (seed) continue; // skip zeroed slots left by deletes
                for (var b = dstTable.Tci1; b < dstTable.RowSize; b++)
                    rec[b] |= templateRaw[toff + b];
                break;
            }
        }

        // Appended heap items: [0] = new BTH leaf, then variable cell payloads in order.
        appended.Insert(0, newLeaf);

        // ---- plan: rebuilt heap with appended items + repointed BTINFO.hidRoot / TCINFO.hnidRows ----
        var tailIdx = heapBlocks.Count - 1;
        var rebuiltTail = HeapOnNode.RebuildBlockWithAppends(heapBlocks[tailIdx].Data, appended, tailIdx, out var newHnids, hasHnhdr: tailIdx == 0)
            ?? throw new PstException("The destination table heap has a shape this editor will not guess; nothing was changed.");
        var modified = new Dictionary<int, byte[]> { [tailIdx] = rebuiltTail };
        int OrdinalOf(uint hid)
        {
            var (blk, off, _) = dstTable.Heap.LocateHid(new Hid(hid));
            var idx = -1;
            for (var bi = 0; bi < heapBlocks.Count; bi++) if (heapBlocks[bi].Bid.Value == blk.Bid.Value) { idx = bi; break; }
            if (idx < 0) throw new PstException("A header item of the destination table lives outside its blocks; nothing was changed.");
            return idx;
        }
        byte[] BufferOf(int idx)
        {
            if (!modified.TryGetValue(idx, out var buf)) modified[idx] = buf = heapBlocks[idx].Data.ToArray();
            return buf;
        }
        // Item offsets are unchanged by the rebuild (the item region is copied byte-identically), so
        // located offsets patch straight into the right block's copy.
        var btBufIdx = OrdinalOf(dstTable.HidRowIndex);
        var btBuf = BufferOf(btBufIdx);
        if (!HeapOnNode.TryGetAllocStart(btBuf, (int)(dstTable.HidRowIndex >> 5), out var btinfoOff) || btinfoOff + 8 > btBuf.Length)
            throw new PstException("The destination table's Row-ID header could not be located; nothing was changed.");
        HeapOnNode.PatchBytesInPlace(btBuf, btinfoOff + 4, BitConverter.GetBytes(newHnids[0])); // BTINFO.hidRoot -> grown leaf

        // Now that the appended items have HIDs, point the record's variable cells at them.
        foreach (var (appendedIndex, col) in variableCellTargets)
        {
            var hnidBytes = BitConverter.GetBytes(newHnids[appendedIndex + 1]); // [0] is the grown BTH leaf
            if (col.CbData == 2)
                rec[col.IbData] = hnidBytes[0];
            else
                hnidBytes.AsSpan(0, col.CbData).CopyTo(rec.AsSpan(col.IbData));
            var existByte = dstTable.Tci1 + (col.IBit >> 3);
            if (existByte < rec.Length)
                rec[existByte] |= (byte)(1 << (col.IBit & 7));
        }

        // ---- plan: matrix bytes (grow case) or in-place slot (reuse case) ----
        long matrixReuseOffset = -1;
        byte[]? matrixGrownTail = null;
        Bid matrixChainBidPlan = default;
        byte[]? matrixBytes = null;
        uint matrixSubNid = dstTable.HnidRows;
        var needNewMatrixBlock = false;
        if (dstTable.HnidRows == 0)
        {
            matrixBytes = new byte[dstTable.RowSize]; // slot 0 only
            rec.CopyTo(matrixBytes, 0);
            needNewMatrixBlock = true;
        }
        else
        {
            var oldMatrix = dstTable.Heap.GetItem(dstTable.HnidRows);
            // Slot placement must mirror the reader's addressing exactly (TableContext.Load): when the
            // matrix length is a multiple of rowSize it is contiguous; otherwise rows live in logical
            // full-block tiles with padding between physical fragments, and a fragmented chain's true
            // capacity is smaller than totalLen/rowSize. Reuse only when the reader would find the
            // slot inside the current bytes; otherwise grow by normalizing the whole matrix to one
            // contiguous block (which the reader then addresses flat).
            var trailerB = _ndb.Unicode ? 16 : 12;
            var blockDataB = 8192 - trailerB;
            long SlotOffset(long rowIndex) =>
                oldMatrix.Length >= (rowIndex + 1) * dstTable.RowSize && oldMatrix.Length % dstTable.RowSize == 0
                    ? rowIndex * dstTable.RowSize
                    : rowIndex / (blockDataB / dstTable.RowSize) * blockDataB +
                      rowIndex % (blockDataB / dstTable.RowSize) * dstTable.RowSize;
            var reuseOffset = SlotOffset(newRowIndex);
            if (reuseOffset >= 0 && reuseOffset + dstTable.RowSize <= oldMatrix.Length &&
                dstTable.Heap.TryPatchSubNodeBytes(dstTable.HnidRows, reuseOffset, rec, dryRun: true))
            {
                matrixReuseOffset = reuseOffset; // same-size in-place write at the reader-consistent slot
            }
            else if (oldMatrix.Length % dstTable.RowSize != 0 &&
                     dstTable.Heap.TryGetSubDataBid(dstTable.HnidRows, out var mChainBid) && mChainBid.IsInternal &&
                     newRowIndex / (blockDataB / dstTable.RowSize) == _ndb.ReadDataTreeBlocks(mChainBid).Count - 1 &&
                     _ndb.ReadDataTreeBlocks(mChainBid).Count > 1)
            {
                // Fragmented chain, slot beyond current bytes: grow the LAST fragment to its full
                // tile capacity (rows-per-block x rowSize, capped at Outlook's observed 8176 max) and
                // swap that one BREF entry. Existing rows keep byte-identical positions inside the
                // tail, so every other slot offset stays valid; ≤8176 keeps output Outlook-typical.
                var chain = _ndb.ReadDataTreeBlocks(mChainBid);
                var tail = chain[chain.Count - 1].Data;
                var rowsPerBlock = blockDataB / dstTable.RowSize;
                var tileBytes = rowsPerBlock * dstTable.RowSize;
                var newTailLen = Math.Min(Math.Max(tileBytes, (int)(newRowIndex % (uint)rowsPerBlock + 1) * dstTable.RowSize), 8176);
                if ((long)newTailLen * chain.Count < (newRowIndex + 1 - chain.Count) * dstTable.RowSize) newTailLen = Math.Min(newTailLen, 8176);
                if (newTailLen <= tail.Length)
                    throw new PstException("The destination folder's row matrix is full; not supported yet.");
                var grownTail = new byte[newTailLen];
                tail.CopyTo(grownTail, 0);
                var inTile = (int)(newRowIndex % (uint)rowsPerBlock);
                rec.CopyTo(grownTail.AsSpan(inTile * dstTable.RowSize));
                matrixGrownTail = grownTail;
                matrixChainBidPlan = mChainBid;
            }
            else
            {
                var needed = (newRowIndex + 1) * dstTable.RowSize;
                if (needed > 8176)
                    throw new PstException("The destination folder's row matrix cannot be extended further safely; nothing was changed.");
                matrixBytes = new byte[needed];
                foreach (var existingIdx in dstTable.RowIndexMap.Values)
                {
                    if (existingIdx >= newRowIndex) continue;
                    var srcOff = SlotOffset(existingIdx);
                    if (srcOff < 0 || srcOff + dstTable.RowSize > oldMatrix.Length) continue;
                    oldMatrix.AsSpan((int)srcOff, dstTable.RowSize).CopyTo(matrixBytes.AsSpan((int)existingIdx * dstTable.RowSize));
                }
                rec.CopyTo(matrixBytes.AsSpan(newRowIndex * dstTable.RowSize));
                needNewMatrixBlock = true;
            }
        }

        // New subnode NID for the empty-table case: next free index in this node's sub-node space.
        if (matrixSubNid == 0)
        {
            var maxIdx = 0;
            foreach (var sn in _ndb.ReadSubNodes(dstNode).Values)
            {
                var idx = (int)(sn.Nid.Value >> 5);
                if (idx > maxIdx) maxIdx = idx;
            }
            matrixSubNid = (uint)((maxIdx + 1) << 5) | (uint)NidType.Ltp;
            var tcBuf = BufferOf(OrdinalOf(dstTable.Heap.UserRoot));
            if (!HeapOnNode.TryGetAllocStart(tcBuf, (int)(dstTable.Heap.UserRoot >> 5), out var tcinfoOff) || tcinfoOff + 18 > tcBuf.Length)
                throw new PstException("The destination table's context header could not be located; nothing was changed.");
            HeapOnNode.PatchBytesInPlace(tcBuf, tcinfoOff + 14, BitConverter.GetBytes(matrixSubNid)); // TCINFO.hnidRows -> new subnode
        }

        // ---- plan: sub-node list entry for the matrix (patch in place, or create/extend the SLB) ----
        List<byte[]>? slbPayload = null; // non-null => need a new raw SLB block
        byte[]? patchedSlb = null;       // same-size in-place patch of the existing SLB leaf
        Bid slbOldBid = default;
        if (needNewMatrixBlock)
        {
            if (dstNode.Sub.Value == 0)
            {
                var entries = _ndb.ReadSubNodes(dstNode).Values.ToList();
                if (entries.Count >= 20)
                    throw new PstException("The destination folder has too many sub-nodes to extend safely.");
                slbPayload = [BuildSlbLeaf(entries, matrixSubNid, pendingMatrix: true)];
            }
            else
            {
                var raw = _ndb.ReadRawBlockForWrite(dstNode.Sub);
                if (raw == null || raw.Length < 8 || raw[0] != 2 || raw[1] != 0)
                    throw new PstException("The destination folder's sub-node tree is multi-level; not supported yet.");
                var cEnt = BinaryUtil.ReadU16(raw, 2);
                if (raw.Length != 8 + cEnt * 24)
                    throw new PstException("The destination folder's sub-node tree has an unexpected size; nothing was changed.");
                var found = false;
                var copy = raw.ToArray();
                for (var i = 0; i < cEnt; i++)
                {
                    if (BinaryUtil.ReadU32(copy, 8 + i * 24) != matrixSubNid) continue;
                    BinaryUtil.WriteU64(copy, 8 + i * 24 + 8, 0); // placeholder; real bid filled during execute
                    found = true;
                    break;
                }
                if (!found)
                {
                    if (cEnt >= 20) throw new PstException("The destination folder's sub-node list is full.");
                    var grown = new byte[raw.Length + 24];
                    raw.CopyTo(grown, 0);
                    BinaryUtil.WriteU16(grown, 2, (ushort)(cEnt + 1));
                    BinaryUtil.WriteU32(grown, 8 + cEnt * 24, matrixSubNid);
                    BinaryUtil.WriteU64(grown, 8 + cEnt * 24 + 8, 0); // placeholder
                    slbPayload = [grown];
                }
                else
                {
                    patchedSlb = copy;
                    slbOldBid = dstNode.Sub;
                }
            }
        }

        // ---- execute (order chosen so the folder view flips atomically at the NBT repoint) ----
        byte[]? matrixBrefSwap = null;
        var batch = _ndb.BeginAllocations();
        try
        {
            Bid newMatrixBid = default;
            if (needNewMatrixBlock)
                newMatrixBid = _ndb.AllocateAndWrite(batch, matrixBytes!, encrypt: true);

            if (slbPayload != null)
            {
                var entries2 = dstNode.Sub.Value == 0
                    ? _ndb.ReadSubNodes(dstNode).Values.ToList()
                    : ReadSlbEntries(_ndb.ReadRawBlockForWrite(dstNode.Sub)!)
                        .Select(t => new SubNodeEntry { Nid = new Nid(t.Nid), Data = t.Data, Sub = t.Sub })
                        .ToList();
                slbPayload[0] = BuildSlbLeaf(entries2, matrixSubNid, pendingMatrix: false, newMatrixBid);
                var slbBid = _ndb.AllocateAndWrite(batch, slbPayload[0], encrypt: false);
                _ndb.RewriteNbtSubBid(dstNode, slbBid);
            }
            else if (patchedSlb != null)
            {
                BinaryUtil.WriteU64(patchedSlb, FindSlbEntryOffset(patchedSlb, matrixSubNid) + 8, newMatrixBid.Value);
                _ndb.RewriteRawExternalBlock(slbOldBid, patchedSlb);
            }
            else if (!needNewMatrixBlock && matrixGrownTail == null && dstTable.HnidRows != 0)
            {
                // Reuse case: the slot for newRowIndex already lives inside the current matrix leaf.
                if (!dstTable.Heap.TryPatchSubNodeBytes(dstTable.HnidRows,
                        matrixReuseOffset, rec))
                    throw new PstException("Could not write the moved message's row into the destination matrix.");
            }

            if (matrixGrownTail != null)
            {
                var newTailBid = _ndb.AllocateAndWrite(batch, matrixGrownTail, encrypt: true);
                var chainBref = _ndb.ReadRawBlockForWrite(matrixChainBidPlan)!;
                BinaryUtil.WriteU64(chainBref, chainBref.Length - 8, newTailBid.Value); // last entry
                matrixBrefSwap = chainBref; // written just before the heap flip below
            }
            if (brefPlan == null)
            {
                var newHeapBid = _ndb.AllocateAndWrite(batch, modified[0], encrypt: true);
                if (matrixBrefSwap != null) _ndb.RewriteInternalBlockSameSize(matrixChainBidPlan, matrixBrefSwap);
                _ndb.RewriteNbtDataBid(dstNode, newHeapBid); // atomic flip to the grown table (row appears here)
            }
            else
            {
                var bref = brefPlan;
                foreach (var (idx, buf) in modified.OrderBy(kv => kv.Key))
                {
                    var nb = _ndb.AllocateAndWrite(batch, buf, encrypt: true);
                    BinaryUtil.WriteU64(bref, 8 + idx * 8, nb.Value);
                }
                if (matrixBrefSwap != null) _ndb.RewriteInternalBlockSameSize(matrixChainBidPlan, matrixBrefSwap);
                _ndb.RewriteInternalBlockSameSize(dstNode.Data, bref); // one page write flips every entry (row appears here)
            }

            var msgNode = _ndb.GetNode(message.Nid);
            if (isCopy)
                _ndb.AppendNbtEntry(rowNid, msgNode.Data, msgNode.Sub, destFolder.Nid); // duplicate handle; data bids shared like Outlook duplicates
            else
            {
                srcTable.TryUnlinkRow(message.Nid);
                _ndb.RewriteNidParent(msgNode, new Nid(destFolder.Nid));
            }

            _ndb.CommitAllocations(batch);
        }
        catch
        {
            // The batch may be half-applied on failure; the editing session's backup/rollback is the
            // safety net (docs/pst-editing-design.md). Do not attempt speculative repairs here.
            throw;
        }

        var unread = message.IsRead ? 0 : 1;
        if (!isCopy) AdjustFolderCounts(message.FolderNid, -1, -unread);
        AdjustFolderCounts(destFolder.Nid, +1, +unread);
        if (!isCopy) message.FolderNid = destFolder.Nid;
    }

    /// <summary>Remove an empty folder (no messages, no subfolders): its NODE_B handle and its
    /// contents-table handle are unlinked from the folder BTree leaf pages (entries shift left,
    /// cEnt decrements, page CRC recomputed - same-size atomic page writes). Nothing is freed;
    /// the NIDs stay retired. Non-empty folders are refused outright so no message can be orphaned.</summary>
    public void DeleteFolder(uint folderNid)
    {
        EnsureWritable();
        if (folderNid == Root.Nid) throw new PstException("The root of an archive cannot be deleted.");
        if (!_folders.TryGetValue(folderNid, out var folder))
            throw new PstException("That folder is no longer present; nothing was changed.");
        if (folder.Name.Equals("Deleted Items", StringComparison.OrdinalIgnoreCase) ||
            folder.Name.Equals("Trash", StringComparison.OrdinalIgnoreCase))
            throw new PstException("Outlook protects the Deleted Items folder from deletion.");
        if (folder.Children.Count > 0)
            throw new PstException($"\"{folder.Name}\" still has subfolders; move or delete them first.");
        int count;
        try { count = GetMessages(folder).Count; }
        catch (PstException) { count = 0; }
        if (count > 0)
            throw new PstException($"\"{folder.Name}\" still has {count} message{(count == 1 ? "" : "s")}; empty it first.");

        _ndb.DeleteNbtEntry(folderNid | (uint)NidType.ContentsTable);
        _ndb.DeleteNbtEntry(folderNid);

        if (_folders.TryGetValue(folder.ParentNid, out var parent))
        {
            parent.Children.Remove(folder);
            if (parent.Children.Count == 0)
            {
                try
                {
                    var ph = HeapOnNode.Load(_ndb, _ndb.GetNode(parent.Nid));
                    PropertyContext.TryPatchFixedUInt32(ph, Pid.Subfolders, 0);
                }
                catch (PstException) { /* cosmetic */ }
            }
        }
        _folders.Remove(folderNid);
    }

    /// <summary>
    /// Create a new mail folder inside parentNid (MS-PST 2.6.x): a fresh global-max NID keeps the
    /// folder BTree's rightmost-append order; the folder object is a property-context heap (BTH of
    /// {propId,type,hnid} records with fixed values inline, per 2.4.3/PC spec) and its contents
    /// table is a byte-clone of an existing EMPTY message table in this very file - column sets and
    /// structures stay exactly Outlook-shaped, and heap-local HIDs make clones self-consistent.
    /// Two NODE_B handles append to the rightmost NBT leaf; the parent's Subfolders flag flips on.
    /// Everything lands through the same allocator + session backup/verify safety as every other edit.
    /// </summary>
    public MailFolder CreateFolder(uint parentNid, string name)
    {
        EnsureWritable();
        if (!_ndb.CanAllocate)
            throw new PstException(_ndb.WriteBlocker ?? "this archive cannot be written into; nothing was changed.");
        name = name.Trim();
        if (name.Length == 0 || name.Length > 200)
            throw new PstException("Folder names must be between 1 and 200 characters; nothing was changed.");
        if (!_folders.TryGetValue(parentNid, out var parent))
            throw new PstException("The parent folder is no longer present; nothing was changed.");
        if (_folders.Values.Any(f => f.ParentNid == parentNid && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new PstException($"A folder named \"{name}\" already exists here; nothing was changed.");

        // Template: any empty, single-block contents table in this file (all message tables share
        // the same column layout within a file, so the clone is indistinguishable from native).
        byte[]? templateHeap = null;
        foreach (var f in _folders.Values)
        {
            if (!_ndb.TryGetNode(f.Nid | (uint)NidType.ContentsTable, out var tn)) continue;
            TableContext? tbl;
            try { tbl = TableContext.TryLoad(_ndb, tn.Nid.Value); } catch (PstException) { continue; }
            if (tbl is null || tbl.Rows.Count != 0 || tbl.HnidRows != 0 || tbl.Heap.Blocks.Count != 1) continue;
            templateHeap = tbl.Heap.Blocks[0].Data.ToArray();
            break;
        }
        if (templateHeap is null)
            throw new PstException("This archive has no empty message table to model a new folder on; nothing was changed.");

        uint maxIdx = 0;
        foreach (var n in _ndb.Nodes)
        {
            var i = n.Nid.Value >> 5;
            if (i > maxIdx) maxIdx = i;
        }
        var newIdx = maxIdx + 1;
        uint folderNid = (newIdx << 5) | (uint)NidType.NormalFolder;
        uint contentsNid = (newIdx << 5) | (uint)NidType.ContentsTable;
        if (_ndb.TryGetNode(folderNid, out _) || _ndb.TryGetNode(contentsNid, out _))
            throw new PstException("NID collision detected; nothing was changed.");

        var propHeap = BuildFolderPropertyHeap(name);

        var batch = _ndb.BeginAllocations();
        try
        {
            var propBid = _ndb.AllocateAndWrite(batch, propHeap, encrypt: true);
            var tableBid = _ndb.AllocateAndWrite(batch, templateHeap, encrypt: true);
            _ndb.AppendNbtEntry(folderNid, propBid, sub: default, parentNid);
            _ndb.AppendNbtEntry(contentsNid, tableBid, sub: default, folderNid);
            _ndb.CommitAllocations(batch);
        }
        catch
        {
            throw; // the editing session's backup/rollback is the safety net
        }

        if (!parent.HasSubfolders)
        {
            try
            {
                var ph = HeapOnNode.Load(_ndb, _ndb.GetNode(parentNid));
                PropertyContext.TryPatchFixedUInt32(ph, Pid.Subfolders, 1);
            }
            catch (PstException) { /* cosmetic: Outlook recomputes it */ }
        }

        var mf = new MailFolder { Nid = folderNid, Name = name, ParentNid = parentNid };
        _folders[folderNid] = mf;
        parent.Children.Add(mf);
        return mf;
    }

    /// <summary>Minimal spec-shaped property-context heap (HNHDR bClientSig=0xBC + BTH cbKey=2/cbEnt=6)
    /// carrying display name, container class IPF.Note, and zeroed counts; fixed values inline.</summary>
    private static byte[] BuildFolderPropertyHeap(string name)
    {
        var nameB = System.Text.Encoding.Unicode.GetBytes(name + "\0");
        var classB = System.Text.Encoding.Unicode.GetBytes("IPF.Note\0");
        // items: 1=BTH header, 2=leaf (5 records x 8), 3=name, 4=class
        var items = new List<byte[]>();
        var bth = new byte[8];
        bth[0] = 0xB5; bth[1] = 2; bth[2] = 6; bth[3] = 0; // bTypeBTH, cbKey, cbEnt, levels=leaf
        BinaryUtil.WriteU32(bth, 4, 2u << 5);               // hidRoot -> item 2 (the leaf)
        items.Add(bth);
        var leaf = new byte[5 * 8];
        void Rec(int i, ushort pid, ushort type, uint hnid)
        {
            BinaryUtil.WriteU16(leaf, i * 8, pid);
            BinaryUtil.WriteU16(leaf, i * 8 + 2, type);
            BinaryUtil.WriteU32(leaf, i * 8 + 4, hnid);
        }
        Rec(0, Pid.DisplayName, 0x001F, 3u << 5);   // PT_UNICODE -> item 3
        Rec(1, Pid.ContentCount, 0x0003, 0);        // PT_LONG inline
        Rec(2, Pid.ContentUnread, 0x0003, 0);
        Rec(3, Pid.Subfolders, 0x000B, 0);          // PT_BOOLEAN inline false
        Rec(4, 0x3613, 0x001F, 4u << 5);            // PR_CONTAINER_CLASS -> item 4
        items.Add(leaf);
        items.Add(nameB);
        items.Add(classB);

        var dataEnd = 12;
        foreach (var it in items) dataEnd += it.Length;
        var mapSize = 4 + (items.Count + 1) * 2;
        var buf = new byte[dataEnd + mapSize];
        BinaryUtil.WriteU16(buf, 0, (ushort)dataEnd);       // ibHnpm
        buf[2] = 0xEC;                                      // HN signature
        buf[3] = 0xBC;                                      // bClientSig: Property Context
        BinaryUtil.WriteU32(buf, 4, 1u << 5);               // hidUserRoot -> BTH header item
        var pos = 12;
        var bounds = new int[items.Count + 1];
        bounds[0] = 12;
        for (var i = 0; i < items.Count; i++)
        {
            items[i].CopyTo(buf.AsSpan(pos));
            bounds[i + 1] = pos + items[i].Length;
            pos += items[i].Length;
        }
        BinaryUtil.WriteU16(buf, dataEnd, (ushort)items.Count);
        for (var i = 0; i <= items.Count; i++)
            BinaryUtil.WriteU16(buf, dataEnd + 4 + i * 2, (ushort)bounds[i]);
        return buf;
    }

    private static List<(uint Nid, Bid Data, Bid Sub)> ReadSlbEntries(byte[] raw)
    {
        var list = new List<(uint, Bid, Bid)>();
        var cEnt = BinaryUtil.ReadU16(raw, 2);
        for (var i = 0; i < cEnt && 8 + i * 24 + 24 <= raw.Length; i++)
            list.Add((BinaryUtil.ReadU32(raw, 8 + i * 24),
                      new Bid(BinaryUtil.ReadU64(raw, 8 + i * 24 + 8)),
                      new Bid(BinaryUtil.ReadU64(raw, 8 + i * 24 + 16))));
        return list;
    }

    private static int FindSlbEntryOffset(byte[] raw, uint nid)
    {
        var cEnt = BinaryUtil.ReadU16(raw, 2);
        for (var i = 0; i < cEnt; i++)
            if (BinaryUtil.ReadU32(raw, 8 + i * 24) == nid) return 8 + i * 24;
        throw new PstException("Sub-node entry vanished during the move.");
    }

    private static byte[] BuildSlbLeaf(List<SubNodeEntry> existing, uint matrixNid, bool pendingMatrix, Bid matrixBid = default)
    {
        var entries = existing.Where(e => e.Nid.Value != matrixNid).ToList();
        var count = entries.Count + 1;
        var buf = new byte[8 + count * 24];
        buf[0] = 2; // SLB
        BinaryUtil.WriteU16(buf, 2, (ushort)count);
        var o = 8;
        foreach (var e in entries)
        {
            BinaryUtil.WriteU32(buf, o, e.Nid.Value);
            BinaryUtil.WriteU64(buf, o + 8, e.Data.Value);
            BinaryUtil.WriteU64(buf, o + 16, e.Sub.Value);
            o += 24;
        }
        BinaryUtil.WriteU32(buf, o, matrixNid);
        BinaryUtil.WriteU64(buf, o + 8, pendingMatrix ? 0UL : matrixBid.Value); // placeholder until allocated
        return buf;
    }


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
