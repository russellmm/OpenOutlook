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
    public void Factory_prefers_native_for_read_only_and_falls_back_for_writing()
    {
        var path = Fixture();
        if (path is null || !NativeLibraryLoader.IsAvailable) return;
        using (var ro = PstEngineFactory.Open(path, writable: false, out var why))
        {
            Assert.IsType<NativePstEngine>(ro);
            Assert.Null(why);
            Assert.False(ro.CanWrite);
        }
        var copy = Path.Combine(Path.GetTempPath(), "oo-factory-" + Guid.NewGuid().ToString("N") + ".pst");
        File.Copy(path, copy);
        try
        {
            using var rw = PstEngineFactory.Open(copy, writable: true, out var why);
            Assert.IsType<PstStore>(rw);
            Assert.NotNull(why);
        }
        finally { try { File.Delete(copy); } catch (IOException) { } }
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
}
