#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenPst;
using PstCore;

namespace OpenOutlook.PstNative
{
    /// <summary>
    /// <see cref="IPstEngine"/> backed by the native OpenPST library: the read path plus, when opened for writing, read/flag
    /// state, move, copy, delete and folder create/delete. Every write is one atomic, journaled native transaction.
    /// Search folders (nid type 3) are not part of the folder tree.
    /// </summary>
    public sealed class NativePstEngine : IPstEngine
    {
        const string ReadOnlyText = "This archive is open read-only.";

        readonly object _gate = new object();
        readonly PstFile _file;
        readonly Dictionary<uint, MailFolder> _folders = new Dictionary<uint, MailFolder>();
        readonly MailFolder _root;
        readonly uint _deletedItems;
        readonly bool _write;
        readonly string _path;
        bool _disposed;

        // Read/flag changes are write-behind: applied to the objects the UI holds at once, written to the file in one native
        // transaction after a short idle time (a commit costs ~100 ms of fsyncs however small it is). Anything that reads or
        // restructures the file flushes first, and so do Dispose and Flush().
        const int FlushDelayMs = 1500;
        readonly Dictionary<uint, (int Read, int Flag)> _pending = new Dictionary<uint, (int, int)>();
        readonly System.Threading.Timer _flushTimer;

        /// <summary>Raised on a thread-pool thread when a background (write-behind) flush fails; the changes in it were not saved.</summary>
        public event Action<Exception>? BackgroundWriteFailed;

        public PstHeader Header { get; }
        public string DisplayName { get; }
        public bool CanWrite => _write;
        /// <summary>True when opening for writing rolled back the journal of an interrupted write.</summary>
        public bool RecoveredFromInterruptedWrite => _file.Recovered;
        public MailFolder Root => _root;

        NativePstEngine(string path, PstFile file, bool write)
        {
            _file = file;
            _write = write;
            _path = path;
            _flushTimer = new System.Threading.Timer(_ => TimerFlush(), null, Timeout.Infinite, Timeout.Infinite);
            Header = ReadHeader(path);
            string name = file.DisplayName.Trim();
            DisplayName = name.Length > 0 && name.Length <= 256 && !name.Any(char.IsControl)
                ? name : System.IO.Path.GetFileNameWithoutExtension(path);
            _deletedItems = file.DeletedItems;
            _root = new MailFolder { Nid = file.RootFolder, Name = "Root", ParentNid = file.RootFolder }; // same as the managed engine
            _folders[_root.Nid] = _root;
            Load(_root);
        }

        /// <summary>
        /// Opens read-only, or for writing when <paramref name="write"/> is true (takes the single-writer lock and rolls back
        /// the journal of an interrupted write). Throws <see cref="PstCore.PstException"/> when the native library is missing,
        /// the file is unsupported or (for writing) locked / not writable.
        /// </summary>
        public static NativePstEngine Open(string path, bool write = false)
        {
            if (!NativeLibraryLoader.IsAvailable)
                throw new PstCore.PstException("The native PST library is not available.");
            string full = System.IO.Path.GetFullPath(path);
            if (write) PstEditLock.Acquire(full);
            PstFile file;
            try { file = new PstFile(full, write); }
            catch (OpenPst.PstException e)
            {
                if (write) PstEditLock.Release(full);
                throw new PstCore.PstException(e.Message, e);
            }
            try { return new NativePstEngine(full, file, write); }
            catch
            {
                file.Dispose();
                if (write) PstEditLock.Release(full);
                throw;
            }
        }

        /// <summary>The file header, read here (not through the managed engine) so that ANSI, 4K-page and OST files work too.</summary>
        static PstHeader ReadHeader(string path)
        {
            var b = new byte[0x240];
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
            {
                int got = 0, n;
                while (got < b.Length && (n = fs.Read(b, got, b.Length - got)) > 0) got += n;
                if (got < 0x210) throw new PstCore.PstException("File is too small to be a PST.");
            }
            ushort wVer = BitConverter.ToUInt16(b, 10);
            bool unicode = wVer >= 23;
            return new PstHeader
            {
                Path = path,
                Format = unicode ? PstFormatKind.Unicode : PstFormatKind.Ansi,
                WVer = wVer,
                WVerClient = BitConverter.ToUInt16(b, 12),
                CryptMethod = (PstCryptMethod)(unicode ? b[0x201] : b[0x1CD]),
                FileEof = unicode ? BitConverter.ToUInt64(b, 0xB4 + 4) : BitConverter.ToUInt32(b, 0xA4 + 4),
                NbtRootBid = unicode ? BitConverter.ToUInt64(b, 0xB4 + 36) : BitConverter.ToUInt32(b, 0xA4 + 20),
                NbtRootIb = unicode ? BitConverter.ToUInt64(b, 0xB4 + 44) : BitConverter.ToUInt32(b, 0xA4 + 24),
                BbtRootBid = unicode ? BitConverter.ToUInt64(b, 0xB4 + 52) : BitConverter.ToUInt32(b, 0xA4 + 28),
                BbtRootIb = unicode ? BitConverter.ToUInt64(b, 0xB4 + 60) : BitConverter.ToUInt32(b, 0xA4 + 32),
                AMapValid = unicode ? b[0xB4 + 68] : b[0xA4 + 36],
                Unique = BitConverter.ToUInt32(b, 0x28),
            };
        }

        void Load(MailFolder parent)
        {
            foreach (var f in _file.Children(parent.Nid))
            {
                if ((f.Nid & 0x1F) != 2 || _folders.ContainsKey(f.Nid)) continue; // normal folders only
                var m = new MailFolder
                {
                    Nid = f.Nid,
                    Name = string.IsNullOrWhiteSpace(f.Name) ? $"Folder 0x{f.Nid:X}" : f.Name,
                    ParentNid = parent.Nid,
                    ContentCount = f.ContentCount,
                    UnreadCount = f.UnreadCount,
                    HasSubfolders = f.HasSubfolders,
                };
                _folders[m.Nid] = m;
                parent.Children.Add(m);
                if (f.HasSubfolders) Load(m);
            }
        }

        public IEnumerable<MailFolder> AllFolders() =>
            _folders.Values.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase);

        public MailFolder? FindFolder(uint nid) => _folders.TryGetValue(nid, out var f) ? f : null;

        public MailFolder? DeletedItemsFolder() =>
            _deletedItems != 0 && _folders.TryGetValue(_deletedItems, out var f) ? f : null;

        static DateTime Local(DateTime? utc) => utc?.ToLocalTime() ?? DateTime.MinValue;

        static string CleanSubject(string s) => string.IsNullOrWhiteSpace(s) ? "(no subject)" : s.Trim();

        static MailSummary ToSummary(uint folderNid, PstMessageRow r, string? from = null)
        {
            var nid = r.Nid;
            if ((nid & 0x1F) != 4) nid = (nid & 0xFFFFFFE0) | 4;
            var sent = Local(r.Sent);
            var received = Local(r.Received);
            return new MailSummary
            {
                Nid = nid,
                FolderNid = folderNid,
                Subject = CleanSubject(r.Subject),
                From = from ?? r.Sender,
                To = r.To,
                Received = received == DateTime.MinValue ? sent : received,
                Sent = sent,
                IsRead = (r.Flags & PstMessageFlags.Read) != 0,
                HasAttachment = r.HasAttachments,
                Flagged = r.FlagStatus != 0,
                Size = (int)Math.Min(r.Size, int.MaxValue),
                MessageClass = r.MessageClass,
                Importance = r.Importance,
            };
        }

        PstMessage OpenNative(uint nid)
        {
            try { return _file.OpenMessage(nid); }
            catch (OpenPst.PstException e) { throw new PstCore.PstException($"Message 0x{nid:X} could not be opened: {e.Message}", e); }
        }

        string SenderOf(PstMessageRow r)
        {
            try
            {
                using var m = _file.OpenMessage(r.Nid);
                var name = m.SenderName.Trim();
                if (name.Length == 0) name = (m.Str(0x0042) ?? "").Trim();
                if (name.Length == 0) name = m.SenderEmail.Trim();
                return name;
            }
            catch (OpenPst.PstException) { return r.Sender; }
        }

        public IReadOnlyList<MailSummary> GetMessages(MailFolder folder)
        {
            lock (_gate)
            {
                FlushPending();
                // 4K-page (OST) tables carry a bogus sender column (it points at the message's change key), so the sender is read
                // from the message itself there; everywhere else the table row is right.
                bool fromMessage = Header.WVer >= 36;
                return _file.Messages(folder.Nid)
                    .Where(r => (r.Nid >> 5) != 0) // a row with node index 0 is a dangling table row, never a message
                    .Select(r => ToSummary(folder.Nid, r, fromMessage ? SenderOf(r) : null))
                    .OrderByDescending(m => m.Received == DateTime.MinValue ? m.Sent : m.Received)
                    .ToList();
            }
        }

        public MailMessage OpenMessage(MailSummary summary)
        {
            lock (_gate)
            {
                using var m = OpenNative(summary.Nid);
                string fromName = m.SenderName.Trim(), fromEmail = m.SenderEmail.Trim();
                string from = string.IsNullOrWhiteSpace(fromEmail) ? fromName : $"{fromName} <{fromEmail}>".Trim();
                long flags = m.Int(0x0E07);
                string? text = m.Body(PstBody.Text);
                bool hasHtml = m.Has(PstBody.Html), hasRtf = m.Has(PstBody.Rtf), hasText = m.Has(PstBody.Text);
                string html = hasHtml || hasRtf ? m.Html() ?? "" : "";
                string rtfHtml = "";
                if (hasRtf)
                {
                    try { rtfHtml = PstText.RtfToHtml(m.Body(PstBody.Rtf) ?? ""); }
                    catch (OpenPst.PstException) { rtfHtml = hasHtml ? "" : html; }
                }
                if (string.IsNullOrWhiteSpace(text)) text = m.Text() ?? "";

                var recipients = m.Recipients()
                    .Select(r => new MailRecipient { Name = r.Name, Email = r.Email, Kind = (RecipientKind)Math.Clamp(r.Type, 1, 3) })
                    .ToList();
                var attachments = m.Attachments()
                    .Select(a => new MailAttachment
                    {
                        Nid = a.Nid,
                        FileName = a.FileName,
                        MimeTag = a.MimeType,
                        Size = (int)Math.Min(a.Size, int.MaxValue),
                        Method = a.Method,
                        ContentId = a.ContentId,
                        IsHidden = a.Hidden,
                    })
                    .ToList();

                var sent = Local(m.Sent);
                var received = Local(m.Received);
                if (received == DateTime.MinValue) received = sent;
                summary.IsRead = (flags & 1) != 0;
                summary.Flagged = m.Int(0x1090) != 0;
                if (_pending.TryGetValue(summary.Nid, out var queued))
                {
                    if (queued.Read >= 0) summary.IsRead = queued.Read == 1;
                    if (queued.Flag >= 0) summary.Flagged = queued.Flag != 0;
                }
                string subject = CleanSubject(m.Subject);

                return new MailMessage
                {
                    Summary = new MailSummary
                    {
                        Nid = summary.Nid,
                        FolderNid = summary.FolderNid,
                        Subject = string.IsNullOrWhiteSpace(m.Subject) ? summary.Subject : subject,
                        From = string.IsNullOrWhiteSpace(from) ? summary.From : from,
                        To = m.To,
                        Received = received,
                        Sent = sent,
                        IsRead = summary.IsRead,
                        HasAttachment = attachments.Count > 0 || (flags & 0x10) != 0,
                        Flagged = summary.Flagged,
                        Size = (int)Math.Min(m.Int(0x0E08, summary.Size), int.MaxValue),
                        MessageClass = m.Str(0x001A) ?? "",
                        Importance = (int)m.Int(0x0017, 1),
                    },
                    BodyText = text ?? "",
                    BodyHtml = html,
                    BodyRtfHtml = rtfHtml,
                    HasHtml = hasHtml,
                    HasRtf = hasRtf,
                    HasText = hasText,
                    Headers = m.TransportHeaders,
                    Recipients = recipients,
                    Attachments = attachments,
                };
            }
        }

        public byte[] ReadAttachmentData(MailSummary message, MailAttachment attachment) =>
            ReadAttachmentData(message, attachment, int.MaxValue);

        public byte[] ReadAttachmentData(MailSummary message, MailAttachment attachment, int maxBytes,
            CancellationToken cancellationToken = default)
        {
            if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            cancellationToken.ThrowIfCancellationRequested();
            if (attachment.Method != 1)
                throw new PstCore.PstException("Only by-value attachments are supported.");
            try
            {
                lock (_gate)
                {
                    using var m = OpenNative(message.Nid);
                    var match = m.Attachments().FirstOrDefault(a => a.Nid == attachment.Nid)
                        ?? throw new PstCore.PstException("Attachment subnode was not found.");
                    if (m.AttachmentLength(match.Index) > maxBytes)
                        throw new PstCore.PstException("Attachment exceeds the permitted size.");
                    cancellationToken.ThrowIfCancellationRequested();
                    var data = m.AttachmentData(match.Index);
                    if (data.Length > maxBytes)
                        throw new PstCore.PstException("Attachment exceeds the permitted size.");
                    return data;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (PstCore.PstException) { throw; }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                throw new PstCore.PstException("Attachment data could not be read safely.");
            }
        }

        public IReadOnlyList<MailSummary> Search(string query, MailFolder? folder = null)
        {
            query = query.Trim();
            if (query.Length == 0) return Array.Empty<MailSummary>();
            HashSet<uint>? scope = null;
            if (folder != null)
            {
                scope = new HashSet<uint>();
                var stack = new Stack<MailFolder>();
                stack.Push(folder);
                while (stack.Count > 0)
                {
                    var f = stack.Pop();
                    if (!scope.Add(f.Nid)) continue;
                    foreach (var c in f.Children) stack.Push(c);
                }
            }
            lock (_gate)
            {
                FlushPending();
                IReadOnlyList<PstSearchHit> hits;
                try { hits = _file.Search(query, PstSearchFlags.Body); }
                catch (OpenPst.PstException e) { throw new PstCore.PstException(e.Message, e); }
                var rowsByFolder = new Dictionary<uint, Dictionary<uint, PstMessageRow>>();
                var results = new List<MailSummary>();
                foreach (var h in hits)
                {
                    if (!_folders.ContainsKey(h.Folder) || (scope != null && !scope.Contains(h.Folder))) continue;
                    if (!rowsByFolder.TryGetValue(h.Folder, out var rows))
                        rowsByFolder[h.Folder] = rows = _file.Messages(h.Folder).GroupBy(r => r.Nid).ToDictionary(g => g.Key, g => g.First());
                    results.Add(rows.TryGetValue(h.Nid, out var row)
                        ? ToSummary(h.Folder, row)
                        : ToSummary(h.Folder, new PstMessageRow(h.Nid, h.Flags, h.Sent, h.Received, h.Size, 1, h.HasAttachments,
                            h.Subject, h.Sender, h.To, "", "", "")));
                }
                return results;
            }
        }

        public IReadOnlyList<string> VerifyIntegrity()
        {
            lock (_gate)
            {
                FlushPending();
                var r = _file.Check();
                if (r.Problems == 0) return Array.Empty<string>();
                return r.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        static int Total(PstFixReport f) =>
            f.RowsWithoutIds + f.DanglingIdMapRecords + f.MessagesNotIndexed + f.RowVersionIssues + f.NidMarkIssues + f.RowCellIssues;

        static IReadOnlyList<string> Lines(PstCheckResult r) =>
            r.Problems == 0 ? Array.Empty<string>() : r.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        public PstScanReport Scan()
        {
            lock (_gate)
            {
                FlushPending();
                return Wrap(() => new PstScanReport(Lines(_file.Check()), Total(_file.Fix(false)), 0));
            }
        }

        public PstScanReport Repair()
        {
            RequireWrite();
            lock (_gate)
            {
                FlushPending();
                return Wrap(() =>
                {
                    var fixedCount = Total(_file.Fix(true));
                    return new PstScanReport(Lines(_file.Check()), Total(_file.Fix(false)), fixedCount);
                });
            }
        }

        void RequireWrite()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NativePstEngine));
            if (!_write) throw new PstCore.PstException(ReadOnlyText);
        }

        static T Wrap<T>(Func<T> f)
        {
            try { return f(); }
            catch (OpenPst.PstException e) { throw new PstCore.PstException(e.Message, e); }
        }

        void AdjustUnread(uint folderNid, int delta)
        {
            if (_folders.TryGetValue(folderNid, out var f)) f.UnreadCount = Math.Max(0, f.UnreadCount + delta);
        }

        public void SetReadState(MailSummary message, bool read)
        {
            RequireWrite();
            lock (_gate)
            {
                var had = _pending.TryGetValue(message.Nid, out var q);
                _pending[message.Nid] = (read ? 1 : 0, had ? q.Flag : -1);
                if (message.IsRead != read) AdjustUnread(message.FolderNid, read ? -1 : 1);
                message.IsRead = read;
                _flushTimer.Change(FlushDelayMs, Timeout.Infinite);
            }
        }

        public void SetFlagged(MailSummary message, bool flagged)
        {
            RequireWrite();
            lock (_gate)
            {
                var had = _pending.TryGetValue(message.Nid, out var q);
                _pending[message.Nid] = (had ? q.Read : -1, flagged ? 2 : 0);
                message.Flagged = flagged;
                _flushTimer.Change(FlushDelayMs, Timeout.Infinite);
            }
        }

        /// <summary>Writes queued read/flag changes now (one native transaction per distinct state). Throws when the write fails.</summary>
        public void Flush()
        {
            lock (_gate) FlushPending();
        }

        void FlushPending()
        {
            if (_pending.Count == 0 || _disposed) return;
            var batch = _pending.ToList();
            _pending.Clear();
            _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            foreach (var group in batch.GroupBy(kv => kv.Value))
            {
                var nids = group.Select(kv => kv.Key).ToList();
                Wrap(() => { _file.SetMessageState(nids, group.Key.Read, group.Key.Flag); return 0; });
            }
        }

        void TimerFlush()
        {
            try { lock (_gate) FlushPending(); }
            catch (Exception e) { BackgroundWriteFailed?.Invoke(e); }
        }

        public void MoveMessage(MailSummary message, MailFolder destFolder)
        {
            RequireWrite();
            lock (_gate)
            {
                FlushPending();
                Wrap(() => { _file.MoveMessages(new[] { message.Nid }, destFolder.Nid); return 0; });
                message.FolderNid = destFolder.Nid;
                SyncFolders();
            }
        }

        public void CopyMessage(MailSummary message, MailFolder destFolder)
        {
            RequireWrite();
            lock (_gate)
            {
                FlushPending();
                Wrap(() => _file.CopyMessages(new[] { message.Nid }, destFolder.Nid));
                SyncFolders();
            }
        }

        /// <summary>Removes the message for good (it does not go to Deleted Items; move it there first for Outlook's Delete).</summary>
        public void DeleteMessage(MailSummary message)
        {
            RequireWrite();
            lock (_gate)
            {
                FlushPending();
                Wrap(() => { _file.PurgeMessages(new[] { message.Nid }); return 0; });
                SyncFolders();
            }
        }

        /// <summary>Largest payload (bodies + attachments) written in one transaction: the transaction keeps its pages in memory until commit.</summary>
        const long ImportBatchBytes = 48L * 1024 * 1024;
        const int ImportBatchMessages = 200;

        public IReadOnlyList<MailSummary> ImportMessages(MailFolder folder, IReadOnlyList<MailImport> messages,
            IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            RequireWrite();
            var summaries = new List<MailSummary>(messages.Count);
            lock (_gate)
            {
                FlushPending();
                try
                {
                    int i = 0;
                    while (i < messages.Count)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var batch = new List<PstImportMessage>();
                        long bytes = 0;
                        int start = i;
                        while (i < messages.Count && batch.Count < ImportBatchMessages && (batch.Count == 0 || bytes < ImportBatchBytes))
                        {
                            var m = ToNative(messages[i]);
                            bytes += 1024 + m.BodyText.Length * 2L + m.BodyHtml.Length * 2L + m.Attachments.Sum(a => (long)a.Data.Length);
                            batch.Add(m);
                            i++;
                        }
                        var nids = Wrap(() => _file.ImportMessages(folder.Nid, batch));
                        for (int k = 0; k < batch.Count; k++) summaries.Add(ToSummary(folder.Nid, nids[k], messages[start + k]));
                        progress?.Report(i);
                    }
                }
                finally { if (summaries.Count > 0) SyncFolders(); }
            }
            return summaries;
        }

        public IReadOnlyList<MailSummary> CopyMessagesTo(IPstEngine destination, MailFolder destFolder, IReadOnlyList<MailSummary> messages)
        {
            if (destination is not NativePstEngine dest) throw new PstCore.PstException("Messages can only be copied between archives opened by the native engine.");
            if (ReferenceEquals(dest, this)) throw new PstCore.PstException("Source and destination are the same archive.");
            dest.RequireWrite();
            if (messages.Count == 0) return Array.Empty<MailSummary>();
            // lock both archives, always in the same order, so two copies in opposite directions cannot deadlock
            var (first, second) = string.CompareOrdinal(_path, dest._path) <= 0 ? (this, dest) : (dest, this);
            lock (first._gate)
            lock (second._gate)
            {
                FlushPending();
                dest.FlushPending();
                var nids = messages.Select(m => m.Nid).ToList();
                var created = Wrap(() => _file.CopyMessagesTo(dest._file, nids, destFolder.Nid));
                dest.SyncFolders();
                var result = new List<MailSummary>(messages.Count);
                for (int i = 0; i < messages.Count; i++)
                {
                    var s = messages[i];
                    result.Add(new MailSummary
                    {
                        Nid = created[i], FolderNid = destFolder.Nid, Subject = s.Subject, From = s.From, To = s.To, Received = s.Received, Sent = s.Sent,
                        IsRead = s.IsRead, HasAttachment = s.HasAttachment, Flagged = s.Flagged, Size = s.Size, MessageClass = s.MessageClass, Importance = s.Importance,
                    });
                }
                return result;
            }
        }

        static PstImportMessage ToNative(MailImport m)
        {
            var n = new PstImportMessage
            {
                MessageClass = m.MessageClass, Subject = m.Subject, SenderName = m.SenderName, SenderEmail = m.SenderEmail,
                BodyText = m.BodyText, BodyHtml = m.BodyHtml, TransportHeaders = m.TransportHeaders, MessageId = m.MessageId,
                Sent = m.Sent, Received = m.Received, Importance = m.Importance, Read = m.Read,
            };
            foreach (var r in m.Recipients) n.Recipients.Add(new PstImportRecipient(r.Name, r.Email, (int)r.Kind));
            foreach (var a in m.Attachments) n.Attachments.Add(new PstImportAttachment(a.FileName, a.MimeType, a.ContentId, a.Data, a.Hidden, a.Modified));
            return n;
        }

        static MailSummary ToSummary(uint folderNid, uint nid, MailImport m)
        {
            var sent = m.Sent?.ToLocalTime() ?? DateTime.MinValue;
            var received = m.Received?.ToLocalTime() ?? sent;
            return new MailSummary
            {
                Nid = nid,
                FolderNid = folderNid,
                Subject = CleanSubject(m.Subject),
                From = string.IsNullOrWhiteSpace(m.SenderName) ? m.SenderEmail : m.SenderName,
                To = string.Join("; ", m.Recipients.Where(r => r.Kind == RecipientKind.To).Select(r => string.IsNullOrWhiteSpace(r.Name) ? r.Email : r.Name)),
                Received = received == DateTime.MinValue ? sent : received,
                Sent = sent,
                IsRead = m.Read,
                HasAttachment = m.Attachments.Count > 0,
                Size = (int)Math.Min(1024L + m.BodyText.Length + m.BodyHtml.Length + m.Attachments.Sum(a => (long)a.Data.Length), int.MaxValue),
                MessageClass = m.MessageClass,
                Importance = m.Importance,
            };
        }

        public MailFolder CreateFolder(uint parentNid, string name)
        {
            RequireWrite();
            lock (_gate)
            {
                FlushPending();
                name = name.Trim();
                uint nid = Wrap(() => _file.CreateFolder(parentNid, name));
                SyncFolders();
                return _folders.TryGetValue(nid, out var f) ? f : throw new PstCore.PstException("The new folder could not be read back.");
            }
        }

        /// <summary>Removes an empty folder for good (refuses folders with subfolders or messages, and special folders).</summary>
        public void DeleteFolder(uint folderNid)
        {
            RequireWrite();
            lock (_gate)
            {
                FlushPending();
                if (!_folders.TryGetValue(folderNid, out var folder))
                    throw new PstCore.PstException("That folder is no longer present; nothing was changed.");
                if (folderNid == _root.Nid) throw new PstCore.PstException("The root of an archive cannot be deleted.");
                if (folder.Children.Count > 0)
                    throw new PstCore.PstException($"\"{folder.Name}\" still has subfolders; move or delete them first.");
                int count = Wrap(() => _file.Messages(folderNid).Count);
                if (count > 0)
                    throw new PstCore.PstException($"\"{folder.Name}\" still has {count} message{(count == 1 ? "" : "s")}; empty it first.");
                Wrap(() => { _file.PurgeFolder(folderNid); return 0; });
                SyncFolders();
            }
        }

        /// <summary>Brings the in-memory folder tree (the same objects the UI holds) in line with the file: counts, new and removed folders.</summary>
        void SyncFolders() => Sync(_root);

        void Sync(MailFolder parent)
        {
            var kids = _file.Children(parent.Nid).Where(f => (f.Nid & 0x1F) == 2).ToList();
            var keep = new HashSet<uint>(kids.Select(k => k.Nid));
            foreach (var gone in parent.Children.Where(c => !keep.Contains(c.Nid)).ToList())
            {
                parent.Children.Remove(gone);
                Forget(gone);
            }
            foreach (var f in kids)
            {
                if (!_folders.TryGetValue(f.Nid, out var m))
                {
                    m = new MailFolder
                    {
                        Nid = f.Nid,
                        Name = string.IsNullOrWhiteSpace(f.Name) ? $"Folder 0x{f.Nid:X}" : f.Name,
                        ParentNid = parent.Nid,
                    };
                    _folders[m.Nid] = m;
                    parent.Children.Add(m);
                }
                m.ContentCount = f.ContentCount;
                m.UnreadCount = f.UnreadCount;
                m.HasSubfolders = f.HasSubfolders;
                if (f.HasSubfolders || m.Children.Count > 0) Sync(m);
            }
            parent.HasSubfolders = parent.Children.Count > 0;
        }

        void Forget(MailFolder f)
        {
            _folders.Remove(f.Nid);
            foreach (var c in f.Children) Forget(c);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                Exception? flushError = null;
                try { FlushPending(); } catch (Exception e) { flushError = e; }
                _disposed = true;
                _flushTimer.Dispose();
                _file.Dispose();
                if (_write) PstEditLock.Release(_path);
                if (flushError != null) BackgroundWriteFailed?.Invoke(flushError);
            }
        }
    }

    /// <summary>
    /// Opens a PST with the native OpenPST engine - the only PST engine (the original managed one was retired). Unicode files with
    /// 512-byte pages can be edited; ANSI and 4K-page files (and files that are not writable or are locked by another window) open
    /// read-only.
    /// </summary>
    public static class PstEngineFactory
    {
        /// <summary>Read-only open.</summary>
        public static IPstEngine Open(string path) => NativePstEngine.Open(path);

        /// <summary>
        /// Opens for writing. Throws <see cref="PstCore.PstException"/> when the archive is locked by another window, not writable, an
        /// ANSI / 4K-page file, or the native library is missing.
        /// </summary>
        public static IPstEngine Open(string path, bool writable) => NativePstEngine.Open(path, writable);

        /// <summary>
        /// The app's normal open: editable by default (always-edit mode). When the archive cannot be opened for writing it opens
        /// read-only and <paramref name="readOnlyReason"/> says why. Throws when the file cannot be read at all.
        /// </summary>
        public static IPstEngine OpenEditable(string path, out string? readOnlyReason)
        {
            readOnlyReason = null;
            try { return NativePstEngine.Open(path, write: true); }
            catch (PstCore.PstException writeError)
            {
                var readOnly = NativePstEngine.Open(path);           // throws when the file is unreadable too
                readOnlyReason = writeError.Message;
                return readOnly;
            }
        }
    }
}
