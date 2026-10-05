using System.Text;
using OpenOutlook.Mirror;
using OpenOutlook.PstNative;
using PstCore;
using Xunit;

namespace OpenOutlook.Tests;

/// <summary>Phase 2: changes made in the local copy reach the server, the server wins conflicts, and being offline only delays them.</summary>
public sealed class MirrorPushTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oo-mirror-push-" + Guid.NewGuid().ToString("N"));
    public MirrorPushTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static bool Native => OpenPst.NativeLibraryLoader.IsAvailable;

    private sealed class Server : IMailSyncSource, IMailSyncSink
    {
        public List<RemoteFolder> Folders = [new("inbox", null, "Inbox", "inbox"), new("del", null, "Deleted Items", "deleteditems"), new("work", null, "Work", null)];
        public Dictionary<string, List<RemoteMessage>> Messages = [];
        public Dictionary<string, string> Subjects = [];
        public List<string> Calls = [];
        public bool Offline;
        public HashSet<string> Refuse = [];
        private int _next;
        public bool CanPush => true;

        private void Check() { if (Offline) throw new HttpRequestException("no network"); }
        public Task<IReadOnlyList<RemoteFolder>> GetFoldersAsync(CancellationToken ct) { Check(); return Task.FromResult<IReadOnlyList<RemoteFolder>>(Folders.ToList()); }
        public Task<IReadOnlyList<RemoteMessage>> ListMessagesAsync(string folderId, DateTimeOffset? since, CancellationToken ct)
        { Check(); return Task.FromResult<IReadOnlyList<RemoteMessage>>(Messages.TryGetValue(folderId, out var l) ? l.ToList() : []); }
        public Task<byte[]> GetMimeAsync(string messageId, CancellationToken ct)
        {
            Check(); Calls.Add("get " + messageId);
            return Task.FromResult(Encoding.UTF8.GetBytes($"From: Ann <ann@example.org>\r\nTo: me@example.org\r\nSubject: {Subjects[messageId]}\r\nMessage-ID: <{messageId}@example.org>\r\nDate: Mon, 05 Oct 2026 10:00:00 +0000\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nBody of {messageId}\r\n"));
        }

        public void Add(string folder, string id, string subject, bool read = false, bool flagged = false)
        {
            if (!Messages.TryGetValue(folder, out var l)) Messages[folder] = l = [];
            l.Add(new RemoteMessage(id, "ck-" + id, DateTimeOffset.UtcNow.AddDays(-1), read, flagged));
            Subjects[id] = subject;
        }

        private (string Folder, int Index) Find(string id)
        {
            foreach (var (f, l) in Messages) { var i = l.FindIndex(m => m.Id == id); if (i >= 0) return (f, i); }
            throw new InvalidOperationException("no such message " + id);
        }

        public Task SetReadAsync(string id, bool read, CancellationToken ct)
        {
            Check(); Calls.Add($"read {id} {read}");
            if (Refuse.Contains(id)) throw new InvalidOperationException("refused");
            var (f, i) = Find(id); Messages[f][i] = Messages[f][i] with { IsRead = read };
            return Task.CompletedTask;
        }
        public Task SetFlaggedAsync(string id, bool flagged, CancellationToken ct)
        {
            Check(); Calls.Add($"flag {id} {flagged}");
            var (f, i) = Find(id); Messages[f][i] = Messages[f][i] with { Flagged = flagged };
            return Task.CompletedTask;
        }
        public Task<string> MoveAsync(string id, string dest, CancellationToken ct)
        {
            Check(); Calls.Add($"move {id} {dest}");
            var (f, i) = Find(id);
            var m = Messages[f][i]; Messages[f].RemoveAt(i);
            var newId = id + "-m" + ++_next;
            Subjects[newId] = Subjects[id];
            if (!Messages.TryGetValue(dest, out var l)) Messages[dest] = l = [];
            l.Add(m with { Id = newId });
            return Task.FromResult(newId);
        }
        public Task PurgeAsync(string id, CancellationToken ct)
        {
            Check(); Calls.Add("purge " + id);
            var (f, i) = Find(id); Messages[f].RemoveAt(i);
            return Task.CompletedTask;
        }
        public Task<string> CreateFolderAsync(string? parentId, string name, CancellationToken ct)
        {
            Check(); Calls.Add($"folder {parentId} {name}");
            var id = "f" + ++_next;
            Folders.Add(new RemoteFolder(id, parentId, name, null));
            return Task.FromResult(id);
        }
    }

    private static Server NewServer()
    {
        var s = new Server();
        s.Add("inbox", "m1", "First");
        s.Add("inbox", "m2", "Second");
        s.Add("inbox", "m3", "Third", read: true);
        return s;
    }

    private (IPstEngine Pst, SyncStateStore State) Open(string name)
    {
        var path = Path.Combine(_dir, name + ".pst");
        var pst = File.Exists(path) ? PstEngineFactory.Open(path, true) : PstEngineFactory.Create(path, "me@example.org");
        return (pst, new SyncStateStore(Path.Combine(_dir, name + ".sync")));
    }

    private static MailFolder Folder(IPstEngine pst, string name) => pst.AllFolders().Single(f => f.Name == name);
    private static MailSummary Msg(IPstEngine pst, string folder, string subject) => pst.GetMessages(Folder(pst, folder)).Single(m => m.Subject == subject);
    private static Task<MirrorSyncResult> Sync(Server s, IPstEngine pst, SyncStateStore st) => MirrorSyncEngine.SyncAsync(s, pst, st, new MirrorSyncOptions(), null, CancellationToken.None);

    [Fact]
    public async Task Reading_and_flagging_in_the_copy_reach_the_server()
    {
        if (!Native) return;
        var server = NewServer();
        var (pst, state) = Open("a");
        using (pst) using (state)
        {
            await Sync(server, pst, state);
            pst.SetReadState(Msg(pst, "Inbox", "First"), true);
            pst.SetFlagged(Msg(pst, "Inbox", "Second"), true);
            var r = await Sync(server, pst, state);
            Assert.Equal(2, r.Pushed);
            Assert.Equal(0, r.PendingLocal);
            Assert.True(server.Messages["inbox"].Single(m => m.Id == "m1").IsRead);
            Assert.True(server.Messages["inbox"].Single(m => m.Id == "m2").Flagged);
            var again = await Sync(server, pst, state);
            Assert.Equal(0, again.Pushed);                                                  // nothing is sent twice
            Assert.Empty(pst.Scan().Findings);
        }
    }

    [Fact]
    public async Task When_both_sides_changed_the_same_property_the_server_wins()
    {
        if (!Native) return;
        var server = NewServer();
        var (pst, state) = Open("b");
        using (pst) using (state)
        {
            await Sync(server, pst, state);
            pst.SetReadState(Msg(pst, "Inbox", "First"), true);                              // local: read
            server.Messages["inbox"][0] = server.Messages["inbox"][0] with { IsRead = true };  // server: read too (no difference to send)...
            pst.SetReadState(Msg(pst, "Inbox", "Second"), true);                             // local: read
            server.Messages["inbox"][1] = server.Messages["inbox"][1] with { IsRead = false, ChangeKey = "ck2", Flagged = true };   // server: changed its flag only
            var r = await Sync(server, pst, state);
            Assert.DoesNotContain(server.Calls, c => c.StartsWith("read m1"));              // same value on both sides: nothing to do
            Assert.Contains("read m2 True", server.Calls);                                  // a different property changed on the server: the read change still goes
            Assert.True(server.Messages["inbox"][1].IsRead);
            Assert.True(Msg(pst, "Inbox", "Second").IsRead);
            Assert.Empty(pst.Scan().Findings);
            Assert.Equal(0, r.Failed);
        }
    }

    [Fact]
    public async Task A_move_in_the_copy_moves_the_message_on_the_server_and_is_not_downloaded_again()
    {
        if (!Native) return;
        var server = NewServer();
        var (pst, state) = Open("c");
        using (pst) using (state)
        {
            await Sync(server, pst, state);
            var downloads = server.Calls.Count(c => c.StartsWith("get"));
            pst.MoveMessage(Msg(pst, "Inbox", "First"), Folder(pst, "Work"));
            var r = await Sync(server, pst, state);
            Assert.Equal(1, r.Pushed);
            Assert.Contains("move m1 work", server.Calls);
            Assert.Single(server.Messages["work"]);
            Assert.Equal(downloads, server.Calls.Count(c => c.StartsWith("get")));          // the moved message (new id on the server) is not fetched again
            Assert.Equal("First", Assert.Single(pst.GetMessages(Folder(pst, "Work"))).Subject);
            Assert.Equal(2, pst.GetMessages(Folder(pst, "Inbox")).Count);
            var again = await Sync(server, pst, state);
            Assert.False(again.Changed);
            Assert.Empty(pst.Scan().Findings);
        }
    }

    [Fact]
    public async Task Deleting_moves_to_deleted_items_and_deleting_there_removes_it_for_good()
    {
        if (!Native) return;
        var server = NewServer();
        var (pst, state) = Open("d");
        using (pst) using (state)
        {
            await Sync(server, pst, state);
            pst.MoveMessage(Msg(pst, "Inbox", "First"), pst.DeletedItemsFolder()!);          // Outlook's Delete
            var r = await Sync(server, pst, state);
            Assert.Equal(1, r.Pushed);
            Assert.Contains("move m1 del", server.Calls);
            Assert.Single(server.Messages["del"]);

            pst.DeleteMessage(pst.GetMessages(pst.DeletedItemsFolder()!).Single());          // delete again: permanent
            r = await Sync(server, pst, state);
            Assert.Equal(1, r.Pushed);
            Assert.Contains(server.Calls, c => c.StartsWith("purge m1-m"));
            Assert.Empty(server.Messages["del"]);
            Assert.Equal(2, pst.GetMessages(Folder(pst, "Inbox")).Count);
            Assert.Empty(pst.Scan().Findings);
        }
    }

    [Fact]
    public async Task Offline_changes_wait_and_are_sent_when_the_connection_is_back()
    {
        if (!Native) return;
        var server = NewServer();
        var (pst, state) = Open("e");
        using (pst) using (state)
        {
            await Sync(server, pst, state);
            pst.SetReadState(Msg(pst, "Inbox", "First"), true);
            pst.MoveMessage(Msg(pst, "Inbox", "Second"), Folder(pst, "Work"));
            server.Offline = true;
            var r = await Sync(server, pst, state);
            Assert.True(r.Offline);
            Assert.Equal(2, r.PendingLocal);
            Assert.Equal(0, r.Pushed);
            Assert.True(Msg(pst, "Inbox", "First").IsRead);                                  // the copy keeps working
            Assert.Single(pst.GetMessages(Folder(pst, "Work")));

            server.Offline = false;
            r = await Sync(server, pst, state);
            Assert.False(r.Offline);
            Assert.Equal(2, r.Pushed);
            Assert.True(server.Messages["inbox"].Single(m => m.Id == "m1").IsRead);
            Assert.Single(server.Messages["work"]);
            Assert.Empty(pst.Scan().Findings);
        }
    }

    [Fact]
    public async Task A_folder_created_in_the_copy_is_created_on_the_server_and_can_receive_messages()
    {
        if (!Native) return;
        var server = NewServer();
        var (pst, state) = Open("f");
        using (pst) using (state)
        {
            await Sync(server, pst, state);
            var inbox = Folder(pst, "Inbox");
            var sub = pst.CreateFolder(inbox.Nid, "Receipts");
            pst.MoveMessage(Msg(pst, "Inbox", "Third"), sub);
            var r = await Sync(server, pst, state);
            Assert.Equal(2, r.Pushed);
            Assert.Contains(server.Calls, c => c.StartsWith("folder inbox Receipts"));
            var created = server.Folders.Single(f => f.Name == "Receipts");
            Assert.Equal("inbox", created.ParentId);
            Assert.Single(server.Messages[created.Id]);
            Assert.Single(pst.GetMessages(Folder(pst, "Receipts")));
            var again = await Sync(server, pst, state);
            Assert.False(again.Changed);
            Assert.Empty(pst.Scan().Findings);
        }
    }

    [Fact]
    public async Task A_change_the_server_refuses_is_counted_and_tried_again_and_local_only_messages_stay()
    {
        if (!Native) return;
        var server = NewServer();
        var (pst, state) = Open("g");
        using (pst) using (state)
        {
            await Sync(server, pst, state);
            pst.SetReadState(Msg(pst, "Inbox", "First"), true);
            pst.ImportMessages(Folder(pst, "Inbox"), [new MailImport { Subject = "Only here", BodyText = "x", SenderEmail = "me@example.org" }]);
            server.Refuse.Add("m1");
            var r = await Sync(server, pst, state);
            Assert.Equal(1, r.Failed);
            Assert.Equal(1, r.PendingLocal);
            Assert.Equal(1, r.LocalOnly);
            Assert.Contains(pst.GetMessages(Folder(pst, "Inbox")), m => m.Subject == "Only here");     // the pull never removes it
            server.Refuse.Clear();
            r = await Sync(server, pst, state);
            Assert.Equal(1, r.Pushed);
            Assert.Equal(0, r.Failed);
            Assert.True(server.Messages["inbox"].Single(m => m.Id == "m1").IsRead);
            Assert.Empty(pst.Scan().Findings);
        }
    }
}
