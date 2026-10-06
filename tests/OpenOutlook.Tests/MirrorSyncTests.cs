using System.Text;
using OpenOutlook.Mirror;
using OpenOutlook.PstNative;
using PstCore;
using Xunit;

namespace OpenOutlook.Tests;

public sealed class MirrorSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oo-mirror-sync-" + Guid.NewGuid().ToString("N"));
    public MirrorSyncTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static bool Native => OpenPst.NativeLibraryLoader.IsAvailable;

    private sealed class FakeServer : IMailSyncSource
    {
        public List<RemoteFolder> Folders = [];
        public Dictionary<string, List<RemoteMessage>> Messages = [];
        public Dictionary<string, string> Subjects = [];
        public HashSet<string> FailDownload = [];
        public Dictionary<string, int> OversizeBytes = [];
        public int Downloads;

        public Task<IReadOnlyList<RemoteFolder>> GetFoldersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteFolder>>(Folders.ToList());

        public Task<IReadOnlyList<RemoteMessage>> ListMessagesAsync(string folderId, DateTimeOffset? since, CancellationToken ct)
        {
            var all = Messages.TryGetValue(folderId, out var l) ? l : [];
            return Task.FromResult<IReadOnlyList<RemoteMessage>>(all.Where(m => since is null || m.Received >= since).ToList());
        }

        public Task<byte[]> GetMimeAsync(string messageId, CancellationToken ct)
        {
            Downloads++;
            if (FailDownload.Contains(messageId)) throw new IOException("download failed");
            var body = new StringBuilder("Hello from " + messageId + "\r\n");
            if (OversizeBytes.TryGetValue(messageId, out var n)) body.Append('x', n);
            var mime = $"From: Ann <ann@example.org>\r\nTo: me@example.org\r\nSubject: {Subjects[messageId]}\r\nMessage-ID: <{messageId}@example.org>\r\nDate: Mon, 05 Oct 2026 10:00:00 +0000\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n{body}";
            return Task.FromResult(Encoding.UTF8.GetBytes(mime));
        }

        public void Add(string folder, string id, string subject, bool read = false, bool flagged = false, int daysOld = 1)
        {
            if (!Messages.TryGetValue(folder, out var l)) Messages[folder] = l = [];
            l.Add(new RemoteMessage(id, "ck-" + id, DateTimeOffset.UtcNow.AddDays(-daysOld), read, flagged));
            Subjects[id] = subject;
        }
    }

    private static FakeServer Server()
    {
        var s = new FakeServer();
        s.Folders = [new("inbox", null, "Inbox", "inbox"), new("sent", null, "Sent Items", "sentitems"), new("del", null, "Deleted Items", "deleteditems"), new("proj", "inbox", "Projects", null)];
        s.Add("inbox", "m1", "First", read: false);
        s.Add("inbox", "m2", "Second", read: true, flagged: true);
        s.Add("sent", "m3", "Sent one", read: true);
        s.Add("del", "m4", "Trashed", read: true);
        s.Add("proj", "m5", "In a subfolder");
        return s;
    }

    private (IPstEngine Pst, SyncStateStore State) Open(string name)
    {
        var path = Path.Combine(_dir, name + ".pst");
        var pst = File.Exists(path) ? PstEngineFactory.Open(path, true) : PstEngineFactory.Create(path, "me@example.org");
        return (pst, new SyncStateStore(Path.Combine(_dir, name + ".sync")));
    }

    private static List<MailSummary> In(IPstEngine pst, string folder) => pst.GetMessages(pst.AllFolders().Single(f => f.Name == folder)).ToList();

    [Fact]
    public async Task The_local_copy_answers_a_folder_in_the_shape_of_the_server_list()
    {
        if (!Native) return;
        var server = Server();
        var (pst, state) = Open("local");
        using (pst) using (state)
        {
            Assert.Null(LocalMailboxReader.Read(pst, state, "inbox"));                          // nothing synchronised yet: ask the server
            await MirrorSyncEngine.SyncAsync(server, pst, state, new MirrorSyncOptions(12, 25 << 20), null, CancellationToken.None);
            var inbox = LocalMailboxReader.Read(pst, state, "inbox")!;
            Assert.Equal("Inbox", inbox.FolderName);
            Assert.Equal(["m1", "m2"], inbox.Messages.Select(m => m.Id).OrderBy(x => x));       // server ids, so actions still reach the server
            Assert.Equal(1, inbox.UnreadCount);
            Assert.True(inbox.Messages.Single(m => m.Id == "m2").IsFlagged);
            Assert.Equal("In a subfolder", LocalMailboxReader.Read(pst, state, "proj")!.Messages.Single().Subject);
            // a message deleted or flagged on this computer shows that at once, before the next sync
            var overlay = LocalMailboxReader.Read(pst, state, "inbox", new HashSet<string> { "m1" }, new Dictionary<string, bool> { ["m2"] = false })!;
            Assert.Equal(["m2"], overlay.Messages.Select(m => m.Id));
            Assert.False(overlay.Messages[0].IsFlagged);
        }
    }

    [Fact]
    public async Task First_sync_builds_the_folder_tree_and_downloads_the_messages()
    {
        if (!Native) return;
        var server = Server();
        var (pst, state) = Open("a");
        using (pst) using (state)
        {
            var result = await MirrorSyncEngine.SyncAsync(server, pst, state, new MirrorSyncOptions(), null, CancellationToken.None);
            Assert.Equal(0, result.Failed);
            Assert.Equal(5, result.MessagesAdded);
            Assert.Equal(3, result.FoldersCreated);                                     // Inbox, Sent Items, Projects; Deleted Items is the file's own
            var inbox = In(pst, "Inbox");
            Assert.Equal(["First", "Second"], inbox.Select(m => m.Subject).OrderBy(x => x).ToArray());
            Assert.False(inbox.Single(m => m.Subject == "First").IsRead);
            Assert.True(inbox.Single(m => m.Subject == "Second").IsRead);
            Assert.True(pst.OpenMessage(inbox.Single(m => m.Subject == "Second")).Summary.Flagged);
            Assert.Single(In(pst, "Projects"));
            Assert.Equal("Trashed", Assert.Single(pst.GetMessages(pst.DeletedItemsFolder()!)).Subject);   // the server's Deleted Items is the file's Deleted Items
            Assert.Equal("Inbox", pst.FindFolder(pst.AllFolders().Single(f => f.Name == "Projects").ParentNid)!.Name);
            Assert.Equal(5, state.MessageCount());
            Assert.Empty(pst.Scan().Findings);
        }
    }

    [Fact]
    public async Task A_second_sync_without_changes_downloads_nothing()
    {
        if (!Native) return;
        var server = Server();
        var (pst, state) = Open("b");
        using (pst) using (state)
        {
            await MirrorSyncEngine.SyncAsync(server, pst, state, new MirrorSyncOptions(), null, CancellationToken.None);
            var before = server.Downloads;
            var again = await MirrorSyncEngine.SyncAsync(server, pst, state, new MirrorSyncOptions(), null, CancellationToken.None);
            Assert.False(again.Changed);
            Assert.Equal(before, server.Downloads);
        }
    }

    [Fact]
    public async Task Server_changes_arrive_in_the_copy_and_it_stays_clean()
    {
        if (!Native) return;
        var server = Server();
        var (pst, state) = Open("c");
        using (pst) using (state)
        {
            var o = new MirrorSyncOptions();
            await MirrorSyncEngine.SyncAsync(server, pst, state, o, null, CancellationToken.None);

            server.Add("inbox", "m6", "Brand new");                                              // new message
            server.Messages["inbox"][0] = server.Messages["inbox"][0] with { IsRead = true, ChangeKey = "ck2" };      // First is read on the server
            server.Messages["inbox"][1] = server.Messages["inbox"][1] with { Flagged = false };    // Second loses its flag
            server.Messages["sent"].Clear();                                                        // Sent one deleted
            server.Folders[3] = server.Folders[3] with { Name = "Projects 2026" };                  // renamed subfolder
            server.Folders.RemoveAll(f => f.Id == "del");                                           // even a vanished Deleted Items must not touch the file's own
            var result = await MirrorSyncEngine.SyncAsync(server, pst, state, o, null, CancellationToken.None);

            Assert.Equal(0, result.Failed);
            Assert.Equal(1, result.MessagesAdded);
            Assert.Equal(1, result.MessagesRemoved);
            Assert.Equal(2, result.MessagesUpdated);
            var inbox = In(pst, "Inbox");
            Assert.Equal(3, inbox.Count);
            Assert.True(inbox.Single(m => m.Subject == "First").IsRead);
            Assert.False(pst.OpenMessage(inbox.Single(m => m.Subject == "Second")).Summary.Flagged);
            Assert.Empty(In(pst, "Sent Items"));
            Assert.Contains(pst.AllFolders(), f => f.Name == "Projects 2026");
            Assert.DoesNotContain(pst.AllFolders(), f => f.Name == "Projects");
            Assert.NotNull(pst.DeletedItemsFolder());

            server.Folders.RemoveAll(f => f.Id == "proj");                                          // subfolder deleted on the server
            result = await MirrorSyncEngine.SyncAsync(server, pst, state, o, null, CancellationToken.None);
            Assert.Equal(1, result.FoldersRemoved);
            Assert.DoesNotContain(pst.AllFolders(), f => f.Name.StartsWith("Projects"));
            Assert.Empty(pst.Scan().Findings);
        }
        // the file is complete and clean when opened again
        using var r = PstEngineFactory.Open(Path.Combine(_dir, "c.pst"));
        Assert.Empty(r.Scan().Findings);
    }

    [Fact]
    public async Task Messages_older_than_the_keep_window_are_not_mirrored_and_age_out()
    {
        if (!Native) return;
        var server = Server();
        server.Add("inbox", "old", "Ancient", daysOld: 400);
        var (pst, state) = Open("d");
        using (pst) using (state)
        {
            var result = await MirrorSyncEngine.SyncAsync(server, pst, state, new MirrorSyncOptions(KeepMonths: 12), null, CancellationToken.None);
            Assert.DoesNotContain(In(pst, "Inbox"), m => m.Subject == "Ancient");
            Assert.Equal(5, result.MessagesAdded);
            await MirrorSyncEngine.SyncAsync(server, pst, state, new MirrorSyncOptions(KeepMonths: 0), null, CancellationToken.None);      // everything
            Assert.Contains(In(pst, "Inbox"), m => m.Subject == "Ancient");
            var shrink = await MirrorSyncEngine.SyncAsync(server, pst, state, new MirrorSyncOptions(KeepMonths: 12), null, CancellationToken.None);
            Assert.Equal(1, shrink.MessagesRemoved);                                                // fell out of the window again
            Assert.DoesNotContain(In(pst, "Inbox"), m => m.Subject == "Ancient");
            Assert.Empty(pst.Scan().Findings);
        }
    }

    [Fact]
    public async Task A_failing_download_is_retried_next_time_and_a_huge_message_is_skipped_once()
    {
        if (!Native) return;
        var server = Server();
        server.FailDownload.Add("m1");
        server.Add("inbox", "huge", "Huge", read: true);
        server.OversizeBytes["huge"] = 40 * 1024 * 1024;
        var (pst, state) = Open("e");
        using (pst) using (state)
        {
            var o = new MirrorSyncOptions(KeepMonths: 12, MaxAttachmentBytes: 1024 * 1024);
            var first = await MirrorSyncEngine.SyncAsync(server, pst, state, o, null, CancellationToken.None);
            Assert.Equal(1, first.Failed);
            Assert.Equal("download failed", first.FirstError);
            Assert.Equal(1, first.Skipped);
            Assert.Equal(4, first.MessagesAdded);
            Assert.DoesNotContain(In(pst, "Inbox"), m => m.Subject == "First");
            server.FailDownload.Clear();
            var before = server.Downloads;
            var second = await MirrorSyncEngine.SyncAsync(server, pst, state, o, null, CancellationToken.None);
            Assert.Equal(0, second.Failed);
            Assert.Equal(1, second.MessagesAdded);
            Assert.Equal(before + 1, server.Downloads);                                              // only "First"; the huge one is remembered
            Assert.Contains(In(pst, "Inbox"), m => m.Subject == "First");
            Assert.Empty(pst.Scan().Findings);
        }
    }

    [Fact]
    public async Task A_folder_name_that_clashes_gets_a_suffix()
    {
        if (!Native) return;
        var server = Server();
        server.Folders.Add(new RemoteFolder("inbox2", null, "Inbox", null));
        server.Add("inbox2", "m7", "Other inbox");
        var (pst, state) = Open("f");
        using (pst) using (state)
        {
            var result = await MirrorSyncEngine.SyncAsync(server, pst, state, new MirrorSyncOptions(), null, CancellationToken.None);
            Assert.Equal(0, result.Failed);
            Assert.Contains(pst.AllFolders(), f => f.Name == "Inbox (2)");
            await MirrorSyncEngine.SyncAsync(server, pst, state, new MirrorSyncOptions(), null, CancellationToken.None);
            Assert.Equal(1, pst.AllFolders().Count(f => f.Name == "Inbox (2)"));                      // not renamed again on the next run
        }
    }
}
