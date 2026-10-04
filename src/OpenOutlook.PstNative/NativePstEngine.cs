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
    /// <see cref="IPstEngine"/> backed by the native OpenPST library. Phase 1: read path only (folders, messages, bodies,
    /// attachments, search, integrity check). Write members throw until the always-edit phase.
    /// Search folders (nid type 3) are not part of the folder tree, matching the managed engine.
    /// </summary>
    public sealed class NativePstEngine : IPstEngine
    {
        const string ReadOnlyText = "The native engine is read-only in this build.";

        readonly object _gate = new object();
        readonly PstFile _file;
        readonly Dictionary<uint, MailFolder> _folders = new Dictionary<uint, MailFolder>();
        readonly MailFolder _root;
        readonly uint _deletedItems;

        public PstHeader Header { get; }
        public string DisplayName { get; }
        public bool CanWrite => false;
        public MailFolder Root => _root;

        NativePstEngine(string path, PstFile file)
        {
            _file = file;
            Header = PstStore.Inspect(path);
            string name = file.DisplayName.Trim();
            DisplayName = name.Length > 0 && name.Length <= 256 && !name.Any(char.IsControl)
                ? name : System.IO.Path.GetFileNameWithoutExtension(path);
            _deletedItems = file.DeletedItems;
            _root = new MailFolder { Nid = file.RootFolder, Name = "Root", ParentNid = file.RootFolder }; // same as the managed engine
            _folders[_root.Nid] = _root;
            Load(_root);
        }

        /// <summary>Opens read-only. Throws <see cref="PstException"/> (PstCore) when the native library or the file is unsupported.</summary>
        public static NativePstEngine Open(string path)
        {
            if (!NativeLibraryLoader.IsAvailable)
                throw new PstCore.PstException("The native PST library is not available.");
            PstFile file;
            try { file = new PstFile(path); }
            catch (OpenPst.PstException e) { throw new PstCore.PstException(e.Message, e); }
            try { return new NativePstEngine(path, file); }
            catch { file.Dispose(); throw; }
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

        static MailSummary ToSummary(uint folderNid, PstMessageRow r)
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
                From = r.Sender,
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

        public IReadOnlyList<MailSummary> GetMessages(MailFolder folder)
        {
            lock (_gate)
            {
                return _file.Messages(folder.Nid)
                    .Where(r => (r.Nid >> 5) != 0) // a row with node index 0 is a dangling table row, never a message
                    .Select(r => ToSummary(folder.Nid, r))
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
                string html = m.Has(PstBody.Html) || m.Has(PstBody.Rtf) ? m.Html() ?? "" : "";
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
                var r = _file.Check();
                if (r.Problems == 0) return Array.Empty<string>();
                return r.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        static PstCore.PstException ReadOnly() => new PstCore.PstException(ReadOnlyText);
        public void SetReadState(MailSummary message, bool read) => throw ReadOnly();
        public void SetFlagged(MailSummary message, bool flagged) => throw ReadOnly();
        public void MoveMessage(MailSummary message, MailFolder destFolder) => throw ReadOnly();
        public void CopyMessage(MailSummary message, MailFolder destFolder) => throw ReadOnly();
        public void DeleteMessage(MailSummary message) => throw ReadOnly();
        public MailFolder CreateFolder(uint parentNid, string name) => throw ReadOnly();
        public void DeleteFolder(uint folderNid) => throw ReadOnly();

        public void Dispose()
        {
            lock (_gate) _file.Dispose();
        }
    }

    /// <summary>
    /// Picks the engine for a PST: native when it is available, read-only and the file is supported; otherwise the managed
    /// <see cref="PstStore"/>. Set OPENOUTLOOK_ENGINE=managed to force the managed engine.
    /// </summary>
    public static class PstEngineFactory
    {
        public static IPstEngine Open(string path, bool writable = false) => Open(path, writable, out _);

        /// <param name="fallbackReason">Why the managed engine was used instead of the native one (null when native was chosen).</param>
        public static IPstEngine Open(string path, bool writable, out string? fallbackReason)
        {
            if (writable) { fallbackReason = "writing is not supported by the native engine yet"; return PstStore.Open(path, true); }
            if (string.Equals(Environment.GetEnvironmentVariable("OPENOUTLOOK_ENGINE"), "managed", StringComparison.OrdinalIgnoreCase))
            {
                fallbackReason = "managed engine forced by OPENOUTLOOK_ENGINE";
                return PstStore.Open(path, false);
            }
            if (!NativeLibraryLoader.IsAvailable)
            {
                fallbackReason = "native library unavailable";
                return PstStore.Open(path, false);
            }
            try
            {
                var engine = NativePstEngine.Open(path);
                fallbackReason = null;
                return engine;
            }
            catch (PstCore.PstException e)
            {
                fallbackReason = e.Message;
                return PstStore.Open(path, false);
            }
        }
    }
}
