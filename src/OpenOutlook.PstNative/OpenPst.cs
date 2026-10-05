// OpenPst.cs - C# (.NET 6+/8) P/Invoke wrapper for the OpenPST C library (openpst.dll / libopenpst.so).
// Copy this file into your project and put the native library next to your executable (or in the runtime's
// native-library search path). Every managed object copies the data it needs and frees the native array
// immediately, so nothing returned by this wrapper holds native memory except PstFile and PstMessage,
// which are IDisposable.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenPst
{
    public class PstException : Exception
    {
        public int Code { get; }
        public PstException(int code, string message) : base(message) { Code = code; }
    }

    internal static class Native
    {
        const string Lib = "openpst";

        [StructLayout(LayoutKind.Sequential)]
        internal struct FolderInfo
        {
            public uint nid, parent;
            public IntPtr name;
            public int content_count, unread_count, has_subfolders;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MsgRow
        {
            public uint nid, flags;
            public long sent, received, size;
            public int importance, has_attachments;
            public IntPtr subject, sender, to, cc, topic, message_class;
            public int flag_status;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Recipient { public IntPtr name, email; public int type; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Attachment
        {
            public uint index, nid;
            public IntPtr filename, mime, cid;
            public long size;
            public int method;
            public int hidden;
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr opst_version();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr opst_last_error();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint flags, out IntPtr p);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void opst_close(IntPtr p);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr opst_display_name(IntPtr p);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern uint opst_root_folder(IntPtr p);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern uint opst_ipm_root(IntPtr p);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern uint opst_deleted_items(IntPtr p);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_folder_children(IntPtr p, uint parent, out IntPtr arr, out UIntPtr count);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void opst_free_folders(IntPtr arr);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_folder_find(IntPtr p, uint start, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out uint nid);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_messages(IntPtr p, uint folder, out IntPtr arr, out UIntPtr count);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void opst_free_messages(IntPtr arr);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_msg_open(IntPtr p, uint nid, out IntPtr m);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void opst_msg_close(IntPtr m);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr opst_msg_str(IntPtr m, ushort pid);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern long opst_msg_i64(IntPtr m, ushort pid, long dflt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr opst_msg_body(IntPtr m, int kind, out UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern uint opst_msg_bodies(IntPtr m);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_msg_recipients(IntPtr m, out IntPtr arr, out UIntPtr count);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void opst_free_recipients(IntPtr arr);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_msg_attachments(IntPtr m, out IntPtr arr, out UIntPtr count);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void opst_free_attachments(IntPtr arr);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_attachment_data(IntPtr m, uint index, out IntPtr data, out UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr opst_msg_text(IntPtr m, out UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr opst_msg_html(IntPtr m, out UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_rtf_to_html(byte[] rtf, UIntPtr len, out IntPtr html, out UIntPtr htmlLen);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_rtf_to_text(byte[] rtf, UIntPtr len, out IntPtr text, out UIntPtr textLen);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_html_to_text(byte[] html, UIntPtr len, out IntPtr text, out UIntPtr textLen);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void opst_free(IntPtr p);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_search(IntPtr p, [MarshalAs(UnmanagedType.LPUTF8Str)] string query, ref SearchOpts opts, out IntPtr hits, out UIntPtr count);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void opst_free_hits(IntPtr arr);

        [StructLayout(LayoutKind.Sequential)] internal struct PurgeStats { public int folders, messages; }
        [StructLayout(LayoutKind.Sequential)] internal struct FixReport { public int rows_without_ids, dangling_idmap, messages_not_indexed, row_version_issues, nid_mark_issues, rowcell_issues, amap_issues, folder_issues, refs_issues; }
        [StructLayout(LayoutKind.Sequential)] internal struct CheckReport { public int problems, refs, nids, tables, idmap, xblocks, rowver, subnodes, rowcells, amap, folders; }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_recovered(IntPtr p);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_journal_pending(IntPtr p);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_folder_is_protected(IntPtr p, uint nid);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_folder_create(IntPtr p, uint parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string? cls, out uint nid);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_folder_rename(IntPtr p, uint nid, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_folder_move(IntPtr p, uint nid, uint dest);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_folder_delete(IntPtr p, uint nid, out int permanent, ref PurgeStats st);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_folder_purge(IntPtr p, uint nid, ref PurgeStats st);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_msgs_move(IntPtr p, uint[] nids, UIntPtr n, uint dest);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_msgs_copy(IntPtr p, uint[] nids, UIntPtr n, uint dest, uint[]? newNids);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_msgs_copy_to(IntPtr src, uint[] nids, UIntPtr n, IntPtr dst, uint dest, uint[]? newNids);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_msgs_delete(IntPtr p, uint[] nids, UIntPtr n, out UIntPtr moved, out UIntPtr purged);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_msgs_purge(IntPtr p, uint[] nids, UIntPtr n);
        [StructLayout(LayoutKind.Sequential)]
        internal struct ImportRecipient { public IntPtr name, email; public int type; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ImportAttachment { public IntPtr filename, mime, content_id, data; public UIntPtr len; public int hidden; public long modified; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ImportMsg
        {
            public IntPtr message_class, subject, sender_name, sender_email, body_text, body_html, transport_headers, message_id;
            public long sent, received;
            public int importance, read;
            public IntPtr recipients; public UIntPtr nrecipients;
            public IntPtr attachments; public UIntPtr nattachments;
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_msgs_import(IntPtr p, uint folder, [In] ImportMsg[] msgs, UIntPtr n, [Out] uint[] nids);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_msgs_set_state(IntPtr p, uint[] nids, UIntPtr n, int read, int flag);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_fix(IntPtr p, int apply, ref FixReport r);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int opst_check(IntPtr p, ref CheckReport r, byte[] text, UIntPtr cap);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void SearchProgress(IntPtr user, UIntPtr folderIndex, UIntPtr folderCount, IntPtr folderPath);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SearchOpts
        {
            public uint size, flags, limit, reserved;
            public IntPtr folders;
            public UIntPtr nfolders;
            public IntPtr cancel;            // int32_t*
            public IntPtr progress;          // function pointer or null
            public IntPtr user;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Hit
        {
            public uint nid, folder, flags;
            public int has_attachments;
            public long sent, received, size;
            public IntPtr folder_path, subject, sender, to;
        }

        /// <summary>Converts native bytes (length-prefixed UTF-8, malloc'd by the library) to a string and frees them.</summary>
        internal static string TakeUtf8(IntPtr p, UIntPtr len)
        {
            try
            {
                int n = checked((int)len);
                if (p == IntPtr.Zero || n == 0) return "";
                var b = new byte[n];
                Marshal.Copy(p, b, 0, n);
                return Encoding.UTF8.GetString(b);
            }
            finally { if (p != IntPtr.Zero) opst_free(p); }
        }

        internal static string? Str(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
        internal static string Err() => Str(opst_last_error()) ?? "unknown error";
        internal static void Check(int rc) { if (rc != 0) throw new PstException(rc, Err()); }
    }

    public sealed record PstFolder(uint Nid, uint Parent, string Name, int ContentCount, int UnreadCount, bool HasSubfolders);

    [Flags] public enum PstMessageFlags : uint { Read = 1, Unsent = 8, HasAttachments = 0x10 }

    public sealed record PstMessageRow(uint Nid, PstMessageFlags Flags, DateTime? Sent, DateTime? Received, long Size,
        int Importance, bool HasAttachments, string Subject, string Sender, string To, string Cc, string Topic, string MessageClass,
        int FlagStatus = 0);

    public sealed record PstRecipient(string Name, string Email, int Type);   // Type: 1 To, 2 Cc, 3 Bcc
    public sealed record PstAttachment(uint Index, uint Nid, string FileName, string MimeType, string ContentId, long Size, int Method, bool Hidden = false);

    public sealed record PstDeleteResult(bool Permanent, int Folders, int Messages);

    /// <summary>A recipient of an imported message. Type: 1 To, 2 Cc, 3 Bcc.</summary>
    public sealed record PstImportRecipient(string Name, string Email, int Type);

    /// <summary>An attachment of an imported message. ContentId (without angle brackets) marks an inline picture.</summary>
    public sealed record PstImportAttachment(string FileName, string MimeType, string ContentId, byte[] Data, bool Hidden = false, DateTime? Modified = null);

    /// <summary>Plain fields of a message to file into a PST folder (an EML file, a Graph message, ...). Text is UTF-16 here, UTF-8 in the library.</summary>
    public sealed class PstImportMessage
    {
        public string MessageClass { get; set; } = "IPM.Note";
        public string Subject { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string SenderEmail { get; set; } = "";
        public string BodyText { get; set; } = "";
        public string BodyHtml { get; set; } = "";
        public string TransportHeaders { get; set; } = "";
        public string MessageId { get; set; } = "";
        public DateTime? Sent { get; set; }
        public DateTime? Received { get; set; }
        /// <summary>0 low, 1 normal, 2 high.</summary>
        public int Importance { get; set; } = 1;
        public bool Read { get; set; }
        public List<PstImportRecipient> Recipients { get; } = new List<PstImportRecipient>();
        public List<PstImportAttachment> Attachments { get; } = new List<PstImportAttachment>();
    }
    public sealed record PstFixReport(int RowsWithoutIds, int DanglingIdMapRecords, int MessagesNotIndexed, int RowVersionIssues, int NidMarkIssues, int RowCellIssues = 0, int AmapIssues = 0, int FolderIssues = 0, int RefsIssues = 0)
    {
        public int Total => RowsWithoutIds + DanglingIdMapRecords + MessagesNotIndexed + RowVersionIssues + NidMarkIssues;
    }
    public sealed record PstCheckResult(int Problems, string Text);

    [Flags] public enum PstSearchFlags : uint { None = 0, Body = 1, SkipDeleted = 2 }

    /// <summary>One search result. <see cref="FolderPath"/> is relative to the root folder, e.g. "Top of Outlook data file/Inbox".</summary>
    public sealed record PstSearchHit(uint Nid, uint Folder, string FolderPath, PstMessageFlags Flags, DateTime? Sent, DateTime? Received,
        long Size, bool HasAttachments, string Subject, string Sender, string To);

    /// <summary>Text conversion helpers that do not need an open file.</summary>
    public static class PstText
    {
        static byte[] Bytes(string s) => Encoding.Latin1.GetBytes(s);

        /// <summary>RTF (as returned by <see cref="PstMessage.Body"/> for PstBody.Rtf) to a complete HTML page.</summary>
        public static string RtfToHtml(string rtf)
        {
            var b = Bytes(rtf);
            Native.Check(Native.opst_rtf_to_html(b, (UIntPtr)b.Length, out var p, out var n));
            return Native.TakeUtf8(p, n);
        }
        public static string RtfToText(string rtf)
        {
            var b = Bytes(rtf);
            Native.Check(Native.opst_rtf_to_text(b, (UIntPtr)b.Length, out var p, out var n));
            return Native.TakeUtf8(p, n);
        }
        public static string HtmlToText(string html)
        {
            var b = Encoding.UTF8.GetBytes(html);
            Native.Check(Native.opst_html_to_text(b, (UIntPtr)b.Length, out var p, out var n));
            return Native.TakeUtf8(p, n);
        }
    }

    public sealed class PstFile : IDisposable
    {
        IntPtr _h;
        public string Path { get; }
        /// <summary>Opens a file read-only.</summary>
        public PstFile(string path) : this(path, false) { }

        /// <summary>
        /// Opens a file; <paramref name="write"/> = true allows the editing methods below (close Outlook first, and work on a COPY of files
        /// you care about). A leftover journal of an interrupted write is rolled back when a file is opened for writing (<see cref="Recovered"/>).
        /// </summary>
        public PstFile(string path, bool write)
        {
            NativeLibraryLoader.Install();
            Native.Check(Native.opst_open(path, write ? 1u : 0u, out _h));
            Path = path;
            CanWrite = write;
        }
        public bool CanWrite { get; }
        /// <summary>True when opening rolled back the journal of an interrupted write.</summary>
        public bool Recovered => Native.opst_recovered(H) != 0;
        /// <summary>True when the file was opened read-only and an interrupted write left a journal: what you read may be half written.</summary>
        public bool JournalPending => Native.opst_journal_pending(H) != 0;
        IntPtr H => _h != IntPtr.Zero ? _h : throw new ObjectDisposedException(nameof(PstFile));

        public string DisplayName => Native.Str(Native.opst_display_name(H)) ?? "";
        public uint RootFolder => Native.opst_root_folder(H);
        public uint IpmRoot => Native.opst_ipm_root(H);
        public uint DeletedItems => Native.opst_deleted_items(H);

        internal static DateTime? FromFileTime(long ft)
        {
            if (ft <= 0) return null;
            try { return DateTime.FromFileTimeUtc(ft); } catch (ArgumentOutOfRangeException) { return null; }
        }

        static T[] ReadArray<T>(IntPtr arr, UIntPtr count) where T : struct
        {
            int n = checked((int)count), sz = Marshal.SizeOf<T>();
            var r = new T[n];
            for (int i = 0; i < n; i++) r[i] = Marshal.PtrToStructure<T>(arr + i * sz);
            return r;
        }

        public IReadOnlyList<PstFolder> Children(uint parent)
        {
            Native.Check(Native.opst_folder_children(H, parent, out var arr, out var cnt));
            try
            {
                var list = new List<PstFolder>();
                foreach (var f in ReadArray<Native.FolderInfo>(arr, cnt))
                    list.Add(new PstFolder(f.nid, f.parent, Native.Str(f.name) ?? "", f.content_count, f.unread_count, f.has_subfolders != 0));
                return list;
            }
            finally { Native.opst_free_folders(arr); }
        }

        /// <summary>"Inbox/Projects" style path below <paramref name="start"/> (0 = root folder).</summary>
        public uint FindFolder(string path, uint start = 0)
        {
            Native.Check(Native.opst_folder_find(H, start, path, out var nid));
            return nid;
        }

        public IReadOnlyList<PstMessageRow> Messages(uint folder)
        {
            Native.Check(Native.opst_messages(H, folder, out var arr, out var cnt));
            try
            {
                var list = new List<PstMessageRow>();
                foreach (var m in ReadArray<Native.MsgRow>(arr, cnt))
                    list.Add(new PstMessageRow(m.nid, (PstMessageFlags)m.flags, FromFileTime(m.sent), FromFileTime(m.received), m.size,
                        m.importance, m.has_attachments != 0, Native.Str(m.subject) ?? "", Native.Str(m.sender) ?? "",
                        Native.Str(m.to) ?? "", Native.Str(m.cc) ?? "", Native.Str(m.topic) ?? "", Native.Str(m.message_class) ?? "", m.flag_status));
                return list;
            }
            finally { Native.opst_free_messages(arr); }
        }

        public PstMessage OpenMessage(uint nid)
        {
            Native.Check(Native.opst_msg_open(H, nid, out var m));
            return new PstMessage(m);
        }

        /// <summary>
        /// Searches all folders. Query: words, "exact phrases", -word, from:/to:/cc:/subject:/body:, has:attachment, is:unread|read,
        /// after:/before: YYYY-MM-DD, folder:NAME. Case and accent insensitive. Throws PstException (code -6) for a bad date.
        /// Cancelling returns the hits found so far. Blocks the calling thread: run it on a worker thread from a UI.
        /// </summary>
        /// <param name="limit">0 = no limit.</param>
        /// <param name="progress">Optional: called before each folder with (index, count, folder path); runs on the searching thread.</param>
        public IReadOnlyList<PstSearchHit> Search(string query, PstSearchFlags flags = PstSearchFlags.None, uint limit = 0,
            System.Threading.CancellationToken cancel = default, Action<int, int, string>? progress = null)
        {
            var o = new Native.SearchOpts { size = (uint)Marshal.SizeOf<Native.SearchOpts>(), flags = (uint)flags, limit = limit };
            IntPtr flag = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(flag, 0);
            o.cancel = flag;
            Native.SearchProgress? cb = null;
            if (progress != null)
            {
                cb = (u, i, n, path) => progress((int)i, (int)n, Native.Str(path) ?? "");
                o.progress = Marshal.GetFunctionPointerForDelegate(cb);
            }
            using var reg = cancel.Register(() => { lock (this) { if (flag != IntPtr.Zero) Marshal.WriteInt32(flag, 1); } });
            try
            {
                int rc = Native.opst_search(H, query, ref o, out var arr, out var cnt);
                GC.KeepAlive(cb);
                Native.Check(rc);
                try
                {
                    var list = new List<PstSearchHit>();
                    foreach (var h in ReadArray<Native.Hit>(arr, cnt))
                        list.Add(new PstSearchHit(h.nid, h.folder, Native.Str(h.folder_path) ?? "", (PstMessageFlags)h.flags, FromFileTime(h.sent),
                            FromFileTime(h.received), h.size, h.has_attachments != 0, Native.Str(h.subject) ?? "", Native.Str(h.sender) ?? "", Native.Str(h.to) ?? ""));
                    return list;
                }
                finally { Native.opst_free_hits(arr); }
            }
            finally
            {
                lock (this) { Marshal.FreeHGlobal(flag); flag = IntPtr.Zero; }
            }
        }

        // ---- editing (every call is one atomic, journalled transaction) -------------------------------------------------------------

        /// <summary>Special folders (root, Top of data file, Inbox, Deleted Items, ...) cannot be renamed, moved or deleted.</summary>
        public bool IsProtected(uint folder)
        {
            int rc = Native.opst_folder_is_protected(H, folder);
            if (rc < 0) throw new PstException(rc, Native.Err());
            return rc == 1;
        }

        /// <summary>Creates a folder below <paramref name="parent"/> (not at the store root); returns its NID.</summary>
        public uint CreateFolder(uint parent, string name, string? containerClass = null)
        {
            Native.Check(Native.opst_folder_create(H, parent, name, containerClass, out var nid));
            return nid;
        }
        public void RenameFolder(uint folder, string name) => Native.Check(Native.opst_folder_rename(H, folder, name));
        public void MoveFolder(uint folder, uint newParent) => Native.Check(Native.opst_folder_move(H, folder, newParent));

        /// <summary>Outlook semantics: the first delete moves the folder to Deleted Items, deleting inside Deleted Items removes it for good.</summary>
        public PstDeleteResult DeleteFolder(uint folder)
        {
            var st = new Native.PurgeStats();
            Native.Check(Native.opst_folder_delete(H, folder, out int permanent, ref st));
            return new PstDeleteResult(permanent != 0, st.folders, st.messages);
        }
        /// <summary>Removes a folder, its subfolders and all their messages for good.</summary>
        public PstDeleteResult PurgeFolder(uint folder)
        {
            var st = new Native.PurgeStats();
            Native.Check(Native.opst_folder_purge(H, folder, ref st));
            return new PstDeleteResult(true, st.folders, st.messages);
        }

        public void MoveMessages(IReadOnlyList<uint> nids, uint destFolder)
        {
            var a = Arr(nids);
            Native.Check(Native.opst_msgs_move(H, a, (UIntPtr)a.Length, destFolder));
        }
        /// <summary>Copies messages inside this file; returns the new NIDs in the same order.</summary>
        public uint[] CopyMessages(IReadOnlyList<uint> nids, uint destFolder)
        {
            var a = Arr(nids);
            var n = new uint[a.Length];
            Native.Check(Native.opst_msgs_copy(H, a, (UIntPtr)a.Length, destFolder, n));
            return n;
        }
        /// <summary>Copies messages of THIS file into a folder of another file (opened for writing); named properties are translated.</summary>
        public uint[] CopyMessagesTo(PstFile destination, IReadOnlyList<uint> nids, uint destFolder)
        {
            var a = Arr(nids);
            var n = new uint[a.Length];
            Native.Check(Native.opst_msgs_copy_to(H, a, (UIntPtr)a.Length, destination.H, destFolder, n));
            return n;
        }
        /// <summary>Outlook semantics: messages move to Deleted Items; messages already there are removed for good.</summary>
        public (int Moved, int Purged) DeleteMessages(IReadOnlyList<uint> nids)
        {
            var a = Arr(nids);
            Native.Check(Native.opst_msgs_delete(H, a, (UIntPtr)a.Length, out var moved, out var purged));
            return ((int)moved, (int)purged);
        }
        /// <summary>Sets read state and/or flag status of messages in one transaction (message, contents-table row and the folder unread count are updated).
        /// <paramref name="read"/>: -1 unchanged, 0 unread, 1 read. <paramref name="flag"/>: -1 unchanged, else 0 none, 1 complete, 2 flagged.</summary>
        public void SetMessageState(IReadOnlyList<uint> nids, int read = -1, int flag = -1)
        {
            var a = Arr(nids);
            Native.Check(Native.opst_msgs_set_state(H, a, (UIntPtr)a.Length, read, flag));
        }
        /// <summary>Files a message built from plain fields into <paramref name="folder"/> (one atomic transaction) and returns its NID.</summary>
        public uint ImportMessage(uint folder, PstImportMessage message) => ImportMessages(folder, new[] { message })[0];

        /// <summary>
        /// Files several messages into <paramref name="folder"/> in ONE atomic transaction (one contents-table rewrite) and returns their NIDs.
        /// Everything is held in memory until the commit: keep a batch to a few dozen MB of payload.
        /// </summary>
        public uint[] ImportMessages(uint folder, IReadOnlyList<PstImportMessage> messages)
        {
            if (messages == null) throw new ArgumentNullException(nameof(messages));
            if (messages.Count == 0) return new uint[0];
            var owned = new List<IntPtr>();
            var pins = new List<GCHandle>();
            IntPtr Utf8(string text)
            {
                if (string.IsNullOrEmpty(text)) return IntPtr.Zero;
                var ptr = Marshal.StringToCoTaskMemUTF8(text);
                owned.Add(ptr);
                return ptr;
            }
            IntPtr Pin(byte[] bytes)
            {
                if (bytes == null || bytes.Length == 0) return IntPtr.Zero;
                var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                pins.Add(handle);
                return handle.AddrOfPinnedObject();
            }
            long FileTime(DateTime? t) => t.HasValue && t.Value > DateTime.MinValue ? t.Value.ToUniversalTime().ToFileTimeUtc() : 0;
            var blocks = new List<IntPtr>();           // AllocHGlobal arrays of recipients / attachments
            try
            {
                int rsz = Marshal.SizeOf<Native.ImportRecipient>(), asz = Marshal.SizeOf<Native.ImportAttachment>();
                var native = new Native.ImportMsg[messages.Count];
                for (int m = 0; m < messages.Count; m++)
                {
                    var message = messages[m] ?? throw new ArgumentNullException(nameof(messages));
                    var msg = new Native.ImportMsg
                    {
                        message_class = Utf8(message.MessageClass), subject = Utf8(message.Subject), sender_name = Utf8(message.SenderName),
                        sender_email = Utf8(message.SenderEmail), body_text = Utf8(message.BodyText), body_html = Utf8(message.BodyHtml),
                        transport_headers = Utf8(message.TransportHeaders), message_id = Utf8(message.MessageId),
                        sent = FileTime(message.Sent), received = FileTime(message.Received), importance = message.Importance, read = message.Read ? 1 : 0,
                    };
                    if (message.Recipients.Count > 0)
                    {
                        var recipients = Marshal.AllocHGlobal(rsz * message.Recipients.Count);
                        blocks.Add(recipients);
                        for (int i = 0; i < message.Recipients.Count; i++)
                            Marshal.StructureToPtr(new Native.ImportRecipient { name = Utf8(message.Recipients[i].Name), email = Utf8(message.Recipients[i].Email), type = message.Recipients[i].Type }, recipients + i * rsz, false);
                        msg.recipients = recipients; msg.nrecipients = (UIntPtr)message.Recipients.Count;
                    }
                    if (message.Attachments.Count > 0)
                    {
                        var attachments = Marshal.AllocHGlobal(asz * message.Attachments.Count);
                        blocks.Add(attachments);
                        for (int i = 0; i < message.Attachments.Count; i++)
                        {
                            var a = message.Attachments[i];
                            Marshal.StructureToPtr(new Native.ImportAttachment
                            {
                                filename = Utf8(a.FileName), mime = Utf8(a.MimeType), content_id = Utf8(a.ContentId), data = Pin(a.Data),
                                len = (UIntPtr)(a.Data?.Length ?? 0), hidden = a.Hidden ? 1 : 0, modified = FileTime(a.Modified)
                            }, attachments + i * asz, false);
                        }
                        msg.attachments = attachments; msg.nattachments = (UIntPtr)message.Attachments.Count;
                    }
                    native[m] = msg;
                }
                var nids = new uint[messages.Count];
                Native.Check(Native.opst_msgs_import(H, folder, native, (UIntPtr)native.Length, nids));
                return nids;
            }
            finally
            {
                foreach (var ptr in owned) Marshal.FreeCoTaskMem(ptr);
                foreach (var h in pins) h.Free();
                foreach (var block in blocks) Marshal.FreeHGlobal(block);
            }
        }

        public void PurgeMessages(IReadOnlyList<uint> nids)
        {
            var a = Arr(nids);
            Native.Check(Native.opst_msgs_purge(H, a, (UIntPtr)a.Length));
        }
        static uint[] Arr(IReadOnlyList<uint> l) { var a = new uint[l.Count]; for (int i = 0; i < a.Length; i++) a[i] = l[i]; return a; }

        /// <summary>Counts (apply = false, works read-only) or repairs (apply = true) the "minor inconsistencies" SCANPST reports in edited files.</summary>
        public PstFixReport Fix(bool apply = false)
        {
            var r = new Native.FixReport();
            Native.Check(Native.opst_fix(H, apply ? 1 : 0, ref r));
            return new PstFixReport(r.rows_without_ids, r.dangling_idmap, r.messages_not_indexed, r.row_version_issues, r.nid_mark_issues, r.rowcell_issues, r.amap_issues, r.folder_issues, r.refs_issues);
        }

        /// <summary>Read-only scan-style checks (a stand-in for much of SCANPST). <see cref="PstCheckResult.Text"/> has one line per finding.</summary>
        public PstCheckResult Check()
        {
            var r = new Native.CheckReport();
            var buf = new byte[1 << 20];
            Native.Check(Native.opst_check(H, ref r, buf, (UIntPtr)buf.Length));
            int n = Array.IndexOf(buf, (byte)0);
            return new PstCheckResult(r.problems, Encoding.UTF8.GetString(buf, 0, n < 0 ? buf.Length : n));
        }

        public void Dispose()
        {
            if (_h != IntPtr.Zero) { Native.opst_close(_h); _h = IntPtr.Zero; }
            GC.SuppressFinalize(this);
        }
        ~PstFile() { Dispose(); }
    }

    public enum PstBody { Text = 0, Html = 1, Rtf = 2 }

    public sealed class PstMessage : IDisposable
    {
        IntPtr _m;
        internal PstMessage(IntPtr m) { _m = m; }
        IntPtr M => _m != IntPtr.Zero ? _m : throw new ObjectDisposedException(nameof(PstMessage));

        public string Subject => Native.Str(Native.opst_msg_str(M, 0x0037)) ?? "";
        public string SenderName => Native.Str(Native.opst_msg_str(M, 0x0C1A)) ?? "";
        public string SenderEmail => Native.Str(Native.opst_msg_str(M, 0x5D01)) ?? Native.Str(Native.opst_msg_str(M, 0x0C1F)) ?? "";
        public string To => Native.Str(Native.opst_msg_str(M, 0x0E04)) ?? "";
        public string Cc => Native.Str(Native.opst_msg_str(M, 0x0E03)) ?? "";
        public string TransportHeaders => Native.Str(Native.opst_msg_str(M, 0x007D)) ?? "";
        public DateTime? Received => PstFile.FromFileTime(Native.opst_msg_i64(M, 0x0E06, 0));
        public DateTime? Sent => PstFile.FromFileTime(Native.opst_msg_i64(M, 0x0039, 0));
        /// <summary>String property by tag id (e.g. 0x001A message class); null when absent.</summary>
        public string Str(ushort pid) => Native.Str(Native.opst_msg_str(M, pid));
        /// <summary>Integer/boolean property by tag id; <paramref name="dflt"/> when absent.</summary>
        public long Int(ushort pid, long dflt = 0) => Native.opst_msg_i64(M, pid, dflt);
        public bool Has(PstBody kind) => (Native.opst_msg_bodies(M) & (1u << (int)kind)) != 0;

        /// <summary>Body as text (Text/Html as UTF-8 decoded to string; Rtf is the decompressed 7-bit RTF as Latin-1). Null when absent.</summary>
        public string? Body(PstBody kind)
        {
            var p = Native.opst_msg_body(M, (int)kind, out var len);
            if (p == IntPtr.Zero) return null;
            int n = checked((int)len);
            var bytes = new byte[n];
            Marshal.Copy(p, bytes, 0, n);
            return kind == PstBody.Rtf ? Encoding.Latin1.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Plain text for display / indexing: the text body, else the HTML or RTF body with formatting removed. Null when the message has no body.</summary>
        public string? Text()
        {
            var p = Native.opst_msg_text(M, out var len);
            return p == IntPtr.Zero ? null : CopyUtf8(p, len);
        }

        /// <summary>HTML for display: the HTML body, else the RTF body converted to HTML (formatting, links, tables and PNG/JPEG pictures kept). Null when neither exists.</summary>
        public string? Html()
        {
            var p = Native.opst_msg_html(M, out var len);
            return p == IntPtr.Zero ? null : CopyUtf8(p, len);
        }

        static string CopyUtf8(IntPtr p, UIntPtr len)
        {
            var b = new byte[checked((int)len)];
            if (b.Length > 0) Marshal.Copy(p, b, 0, b.Length);
            return Encoding.UTF8.GetString(b);
        }

        public IReadOnlyList<PstRecipient> Recipients()
        {
            Native.Check(Native.opst_msg_recipients(M, out var arr, out var cnt));
            try
            {
                var list = new List<PstRecipient>();
                int sz = Marshal.SizeOf<Native.Recipient>();
                for (int i = 0; i < (int)cnt; i++)
                {
                    var r = Marshal.PtrToStructure<Native.Recipient>(arr + i * sz);
                    list.Add(new PstRecipient(Native.Str(r.name) ?? "", Native.Str(r.email) ?? "", r.type));
                }
                return list;
            }
            finally { Native.opst_free_recipients(arr); }
        }

        public IReadOnlyList<PstAttachment> Attachments()
        {
            Native.Check(Native.opst_msg_attachments(M, out var arr, out var cnt));
            try
            {
                var list = new List<PstAttachment>();
                int sz = Marshal.SizeOf<Native.Attachment>();
                for (int i = 0; i < (int)cnt; i++)
                {
                    var a = Marshal.PtrToStructure<Native.Attachment>(arr + i * sz);
                    list.Add(new PstAttachment(a.index, a.nid, Native.Str(a.filename) ?? "", Native.Str(a.mime) ?? "", Native.Str(a.cid) ?? "", a.size, a.method, a.hidden != 0));
                }
                return list;
            }
            finally { Native.opst_free_attachments(arr); }
        }

        /// <summary>Exact payload length of an attachment without copying it (<see cref="PstAttachment.Size"/> is the on-disk size).</summary>
        public long AttachmentLength(uint index)
        {
            Native.Check(Native.opst_attachment_data(M, index, out _, out var len));
            return (long)len;
        }

        public byte[] AttachmentData(uint index)
        {
            Native.Check(Native.opst_attachment_data(M, index, out var p, out var len));
            var bytes = new byte[checked((int)len)];
            if (bytes.Length > 0) Marshal.Copy(p, bytes, 0, bytes.Length);
            return bytes;
        }

        public void Dispose()
        {
            if (_m != IntPtr.Zero) { Native.opst_msg_close(_m); _m = IntPtr.Zero; }
            GC.SuppressFinalize(this);
        }
        ~PstMessage() { Dispose(); }
    }
}
