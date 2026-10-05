using System.Reflection;
using OpenOutlook.PstNative;
using OpenPst;
using PstCore;

namespace OpenOutlook.Tests;

/// <summary>
/// Engine contract tests. The managed engine is NOT the reference: on large archives it misreads rows of big
/// contents tables (wrong sender / dates / read state on ~10% of messages of a 13k-message archive), so the native
/// engine is checked against the message properties themselves, and against the managed engine only where the
/// managed engine is reliable (folder tree, message id sets).
/// </summary>
public sealed class PstEngineContractTests
{
    static string? Fixture()
    {
        var path = Environment.GetEnvironmentVariable("OPENOUTLOOK_TEST_PST");
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
    }

    [Fact]
    public void Managed_store_implements_the_engine_contract()
    {
        Assert.True(typeof(IPstEngine).IsAssignableFrom(typeof(PstStore)));
        Assert.True(typeof(IPstEngine).IsAssignableFrom(typeof(NativePstEngine)));
        // The interface must not drift from PstStore's public instance surface.
        var engine = typeof(IPstEngine).GetMembers().Where(m => m.MemberType is MemberTypes.Method or MemberTypes.Property)
            .Select(m => m.Name).Where(n => n != "Dispose").ToHashSet();
        var store = typeof(PstStore).GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.MemberType is MemberTypes.Method or MemberTypes.Property)
            .Select(m => m.Name).ToHashSet();
        Assert.Empty(engine.Except(store));
    }

    [Fact]
    public void Managed_engine_opens_fixture_through_the_interface()
    {
        var path = Fixture();
        if (path is null) return;
        using IPstEngine engine = PstStore.Open(path, writable: false);
        Assert.NotEmpty(engine.AllFolders());
        Assert.NotNull(engine.Root);
    }

    [Fact]
    public void Native_library_loads_when_present_and_matches_expected_version()
    {
        if (!NativeLibraryLoader.IsAvailable) return; // native library is optional
        Assert.StartsWith(NativeLibraryLoader.ExpectedVersionPrefix, NativeLibraryLoader.Version);
    }

    [Fact]
    public void Factory_uses_native_for_reading_and_editing_and_never_writes_with_the_managed_engine()
    {
        var path = Fixture();
        if (path is null || !NativeLibraryLoader.IsAvailable) return;
        using (var ro = PstEngineFactory.Open(path, writable: false, out var why))
        {
            Assert.IsType<NativePstEngine>(ro);
            Assert.Null(why);
            Assert.False(ro.CanWrite);
        }
        var copy = TempCopy(path);
        try
        {
            using (var rw = PstEngineFactory.Open(copy, writable: true))
            {
                Assert.IsType<NativePstEngine>(rw);
                Assert.True(rw.CanWrite);
            }
            using var edit = PstEngineFactory.OpenEditable(copy, out var roWhy);
            Assert.IsType<NativePstEngine>(edit);
            Assert.True(edit.CanWrite);
            Assert.Null(roWhy);
        }
        finally { Cleanup(copy); }
    }

    /// <summary>Kinds of findings of the integrity check (numbers such as block counts and the NBT counter legitimately change).</summary>
    static List<string> Findings(IPstEngine e) => e.VerifyIntegrity().Where(l => l.StartsWith("  "))
        .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"0x[0-9a-fA-F]+|\d+", "#")).Distinct().ToList();   // kinds, not counts

    static string TempCopy(string source)
    {
        var copy = Path.Combine(Path.GetTempPath(), "oo-native-" + Guid.NewGuid().ToString("N") + ".pst");
        File.Copy(source, copy);
        return copy;
    }

    static void Cleanup(string path)
    {
        foreach (var f in new[] { path, path + ".lck", path + ".journal", path + ".bak" })
            try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { }
    }

    [Fact]
    public void Second_writer_is_refused_while_the_first_holds_the_lock_and_gets_read_only_instead()
    {
        var path = Fixture();
        if (path is null || !NativeLibraryLoader.IsAvailable) return;
        var copy = TempCopy(path);
        try
        {
            using (var first = NativePstEngine.Open(copy, write: true))
            {
                Assert.Throws<PstCore.PstException>(() => NativePstEngine.Open(copy, write: true));
                using var second = PstEngineFactory.OpenEditable(copy, out var why);
                Assert.False(second.CanWrite);
                Assert.Contains("already open for editing", why);
            }
            Assert.False(File.Exists(copy + ".lck"));          // released on dispose
            using var again = NativePstEngine.Open(copy, write: true);
            Assert.True(again.CanWrite);
        }
        finally { Cleanup(copy); }
    }

    [Fact]
    public void Native_edits_are_persisted_and_leave_the_integrity_check_unchanged()
    {
        var path = Fixture();
        if (path is null || !NativeLibraryLoader.IsAvailable) return;
        var copy = TempCopy(path);
        try
        {
            List<string> before;
            uint folderNid, msgNid, newFolderNid;
            bool wasRead;
            using (var e = NativePstEngine.Open(copy, write: true))
            {
                before = Findings(e);
                var folder = e.AllFolders().First(f => f.ContentCount >= 2 && f.Nid != e.DeletedItemsFolder()?.Nid && f.Name != "Root");
                folderNid = folder.Nid;
                var msgs = e.GetMessages(folder);
                var m = msgs[0];
                msgNid = m.Nid;
                wasRead = m.IsRead;
                int unread0 = folder.UnreadCount;

                e.SetReadState(m, !wasRead);
                e.SetFlagged(m, true);
                Assert.Equal(wasRead ? unread0 + 1 : unread0 - 1, folder.UnreadCount);

                var made = e.CreateFolder(folder.Nid, "OO Test Folder");
                Assert.Equal("OO Test Folder", made.Name);
                newFolderNid = made.Nid;
                e.CopyMessage(m, made);
                Assert.Equal(1, made.ContentCount);
                var copied = e.GetMessages(made).Single();
                e.MoveMessage(copied, folder);          // copy goes back to the source folder
                Assert.Equal(0, made.ContentCount);
                Assert.Equal(msgs.Count + 1, folder.ContentCount);
                var extra = e.GetMessages(folder).First(x => x.Nid != msgNid && !msgs.Any(o => o.Nid == x.Nid));
                e.DeleteMessage(extra);                 // purge the copy again
                Assert.Equal(msgs.Count, folder.ContentCount);
                Assert.Throws<PstCore.PstException>(() => e.DeleteFolder(folder.Nid));   // not empty
                e.DeleteFolder(made.Nid);
                Assert.Null(e.FindFolder(newFolderNid));
                Assert.Empty(Findings(e).Except(before));   // no new findings (editing may only repair old ones)
            }
            // everything survived a close and a fresh read-only open
            using var r = NativePstEngine.Open(copy);
            var f2 = r.FindFolder(folderNid)!;
            var m2 = r.GetMessages(f2).Single(x => x.Nid == msgNid);
            Assert.Equal(!wasRead, m2.IsRead);
            Assert.True(m2.Flagged);
            Assert.Null(r.FindFolder(newFolderNid));
            Assert.Empty(Findings(r).Except(before));
        }
        finally { Cleanup(copy); }
    }

    [Fact]
    public void Native_engine_write_members_fail_closed()
    {
        var path = Fixture();
        if (path is null || !NativeLibraryLoader.IsAvailable) return;
        using var native = NativePstEngine.Open(path);
        var folder = native.AllFolders().First(f => f.ContentCount > 0);
        var msg = native.GetMessages(folder)[0];
        Assert.Throws<PstCore.PstException>(() => native.SetReadState(msg, true));
        Assert.Throws<PstCore.PstException>(() => native.DeleteMessage(msg));
        Assert.Throws<PstCore.PstException>(() => native.CreateFolder(folder.Nid, "x"));
    }

    [Fact]
    public void Native_and_managed_engines_agree_on_folders_and_message_ids()
    {
        var path = Fixture();
        if (path is null || !NativeLibraryLoader.IsAvailable) return;
        using var managed = PstStore.Open(path, writable: false);
        using var native = NativePstEngine.Open(path);
        var m = managed.AllFolders().ToDictionary(f => f.Nid);
        var diffs = new List<string>();
        foreach (var nf in native.AllFolders())
        {
            // Every native folder must exist in the managed tree. (The reverse is not required: the managed engine also
            // lists folders that are missing from their parent's hierarchy table, which the native check reports.)
            if (!m.TryGetValue(nf.Nid, out var mf)) { diffs.Add($"folder {nf.Nid:x} ({nf.Name}) missing in managed"); continue; }
            if (nf.Name != mf.Name) diffs.Add($"folder {nf.Nid:x} name [{nf.Name}] vs [{mf.Name}]");
            if (nf.ContentCount != mf.ContentCount) diffs.Add($"folder {nf.Nid:x} count {nf.ContentCount} vs {mf.ContentCount}");
            var nids = native.GetMessages(nf).Select(x => x.Nid).OrderBy(x => x).ToArray();
            var mids = managed.GetMessages(mf).Select(x => x.Nid).OrderBy(x => x).ToArray();
            if (!nids.SequenceEqual(mids)) diffs.Add($"folder {nf.Nid:x} message ids differ: {nids.Length} vs {mids.Length}");
        }
        Assert.True(diffs.Count == 0, string.Join("\n", diffs.Take(20)));
    }

    [Fact]
    public void Native_message_rows_match_the_message_properties()
    {
        var path = Fixture();
        if (path is null || !NativeLibraryLoader.IsAvailable) return;
        using var native = NativePstEngine.Open(path);
        var diffs = new List<string>();
        int checkedCount = 0;
        foreach (var folder in native.AllFolders())
        {
            foreach (var row in native.GetMessages(folder))
            {
                if (checkedCount++ > 3000) break; // keep large fixtures fast
                var open = native.OpenMessage(new MailSummary { Nid = row.Nid, FolderNid = row.FolderNid }).Summary;
                void Chk(string what, object? a, object? b) { if (!Equals(a, b)) diffs.Add($"msg {row.Nid:x} {what}: row [{a}] vs message [{b}]"); }
                Chk("sent", row.Sent, open.Sent);
                Chk("read", row.IsRead, open.IsRead);
                Chk("flagged", row.Flagged, open.Flagged);
                Chk("class", row.MessageClass, open.MessageClass);
                Chk("importance", row.Importance, open.Importance);
                Chk("attach", row.HasAttachment, open.HasAttachment);
                if (row.Subject != "(no subject)") Chk("subject", row.Subject, open.Subject);
            }
        }
        Assert.True(diffs.Count == 0, $"{diffs.Count} differences:\n" + string.Join("\n", diffs.Take(30)));
    }

    [Fact]
    public void Native_attachment_bytes_respect_the_size_limit()
    {
        var path = Fixture();
        if (path is null || !NativeLibraryLoader.IsAvailable) return;
        using var native = NativePstEngine.Open(path);
        foreach (var folder in native.AllFolders())
            foreach (var row in native.GetMessages(folder).Where(r => r.HasAttachment))
            {
                var open = native.OpenMessage(row);
                var att = open.Attachments.FirstOrDefault(a => a.Method == 1 && a.Size > 0);
                if (att is null) continue;
                var data = native.ReadAttachmentData(row, att, int.MaxValue);
                Assert.True(data.Length > 0 && data.Length <= att.Size);
                var ex = Assert.Throws<PstCore.PstException>(() => native.ReadAttachmentData(row, att, data.Length - 1));
                Assert.Equal("Attachment exceeds the permitted size.", ex.Message);
                return;
            }
    }

    [Fact]
    public void Read_and_flag_changes_are_written_behind_in_batches_and_survive_close()
    {
        var path = Fixture();
        if (path is null || !NativeLibraryLoader.IsAvailable) return;
        var copy = TempCopy(path);
        try
        {
            uint folderNid;
            List<(uint Nid, bool Read)> expected;
            using (var e = NativePstEngine.Open(copy, write: true))
            {
                var folder = e.AllFolders().Where(f => f.Name != "Root").OrderByDescending(f => f.ContentCount).First();
                folderNid = folder.Nid;
                var msgs = e.GetMessages(folder).Take(40).ToList();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var m in msgs) e.SetReadState(m, !m.IsRead);       // queued: no file write per call
                sw.Stop();
                Assert.True(sw.ElapsedMilliseconds < 1000, $"queueing {msgs.Count} changes took {sw.ElapsedMilliseconds} ms");
                expected = msgs.Select(m => (m.Nid, m.IsRead)).ToList();
                // an opened message reports the queued state even before it is on disk
                var opened = e.OpenMessage(new MailSummary { Nid = expected[0].Nid, FolderNid = folderNid });
                Assert.Equal(expected[0].Read, opened.Summary.IsRead);
                // dispose flushes
            }
            using var r = NativePstEngine.Open(copy);
            var rows = r.GetMessages(r.FindFolder(folderNid)!).ToDictionary(m => m.Nid);
            foreach (var (nid, read) in expected) Assert.Equal(read, rows[nid].IsRead);
        }
        finally { Cleanup(copy); }
    }
}
