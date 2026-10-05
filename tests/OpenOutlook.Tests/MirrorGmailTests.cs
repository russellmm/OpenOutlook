using System.Net;
using System.Text;
using System.Text.Json;
using OpenOutlook.Mirror;
using OpenOutlook.PstNative;
using OpenOutlook.Providers.Google;
using PstCore;
using Xunit;

namespace OpenOutlook.Tests;

/// <summary>Phase 3: a Gmail mailbox mirrored into a PST, label folders with a copy per label, and changes from the copy turned into label changes.</summary>
public sealed class MirrorGmailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oo-mirror-gmail-" + Guid.NewGuid().ToString("N"));
    public MirrorGmailTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static bool Native => OpenPst.NativeLibraryLoader.IsAvailable;

    /// <summary>A small stateful Gmail: labels, messages with label sets, list queries (after:, is:unread, is:starred), raw download, modify / batchModify, trash, create label.</summary>
    private sealed class FakeGmail : HttpMessageHandler
    {
        public Dictionary<string, string> Labels = new()
        {
            ["INBOX"] = "INBOX", ["SENT"] = "SENT", ["DRAFT"] = "DRAFT", ["TRASH"] = "TRASH", ["STARRED"] = "STARRED", ["UNREAD"] = "UNREAD", ["CATEGORY_PROMOTIONS"] = "CATEGORY_PROMOTIONS",
            ["Label_1"] = "Work", ["Label_2"] = "Work/Reports"
        };
        public Dictionary<string, HashSet<string>> Msgs = [];
        public Dictionary<string, string> Subjects = [];
        public Dictionary<string, int> Age = [];                    // days old
        public List<string> Log = [];
        public bool Offline;
        private int _label = 3;

        public void Add(string id, string subject, int daysOld, params string[] labels) { Msgs[id] = [.. labels]; Subjects[id] = subject; Age[id] = daysOld; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("no network");
            var path = request.RequestUri!.AbsolutePath.Replace("/gmail/v1/users/me", "");
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var body = request.Content is null ? "" : request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            if (path != "/profile") Log.Add($"{request.Method} {path}");
            string json = "{}";
            if (path == "/profile") json = """{"emailAddress":"me@gmail.test"}""";
            else if (path == "/labels" && request.Method == HttpMethod.Get)
                json = JsonSerializer.Serialize(new { labels = Labels.Select(l => new { id = l.Key, name = l.Value, type = l.Key.StartsWith("Label_") ? "user" : "system" }) });
            else if (path == "/labels" && request.Method == HttpMethod.Post)
            {
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
                var id = "Label_" + ++_label;
                Labels[id] = name;
                json = JsonSerializer.Serialize(new { id, name, type = "user" });
            }
            else if (path == "/messages" && request.Method == HttpMethod.Get)
            {
                var label = query["labelIds"];
                var q = query["q"] ?? "";
                var ids = Msgs.Where(m => label is null || m.Value.Contains(label)).Select(m => m.Key);
                foreach (var tok in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (tok.StartsWith("after:")) { var d = DateTime.ParseExact(tok[6..], "yyyy/MM/dd", null); ids = ids.Where(i => DateTime.UtcNow.AddDays(-Age[i]).Date >= d.Date); }
                    else if (tok == "is:unread") ids = ids.Where(i => Msgs[i].Contains("UNREAD"));
                    else if (tok == "is:starred") ids = ids.Where(i => Msgs[i].Contains("STARRED"));
                }
                json = JsonSerializer.Serialize(new { messages = ids.Select(i => new { id = i }) });
            }
            else if (path.StartsWith("/messages/") && path.EndsWith("/modify"))
                Modify([path.Split('/')[2]], body);
            else if (path == "/messages/batchModify")
                Modify(JsonDocument.Parse(body).RootElement.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).ToList(), body);
            else if (path.EndsWith("/trash")) { var id = path.Split('/')[2]; Msgs[id].Remove("INBOX"); Msgs[id].Add("TRASH"); }
            else if (path.StartsWith("/messages/") && query["format"] == "raw")
            {
                var id = path.Split('/')[2];
                var mime = $"From: Ann <ann@example.org>\r\nTo: me@gmail.test\r\nSubject: {Subjects[id]}\r\nMessage-ID: <{id}@gmail.test>\r\nDate: Mon, 05 Oct 2026 10:00:00 +0000\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nBody of {id}\r\n";
                json = JsonSerializer.Serialize(new { id, raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(mime)).Replace('+', '-').Replace('/', '_').TrimEnd('=') });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json"), RequestMessage = request });
        }

        private void Modify(IReadOnlyList<string> ids, string body)
        {
            var doc = JsonDocument.Parse(body).RootElement;
            foreach (var id in ids)
            {
                if (doc.TryGetProperty("addLabelIds", out var add)) foreach (var l in add.EnumerateArray()) Msgs[id].Add(l.GetString()!);
                if (doc.TryGetProperty("removeLabelIds", out var rem)) foreach (var l in rem.EnumerateArray()) Msgs[id].Remove(l.GetString()!);
            }
        }
    }

    private static FakeGmail Server()
    {
        var g = new FakeGmail();
        g.Add("g1", "Inbox only", 3, "INBOX", "UNREAD");
        g.Add("g2", "Inbox and starred", 5, "INBOX", "STARRED");
        g.Add("g3", "In a user label", 8, "Label_1");
        g.Add("g4", "In a nested label", 9, "Label_2", "UNREAD");
        g.Add("g5", "Trashed", 4, "TRASH");
        g.Add("old", "Too old", 400, "INBOX");
        return g;
    }

    private static GmailMirrorSource Source(FakeGmail g, bool push = true) =>
        new(new GmailMailbox(new HttpClient(g), _ => ValueTask.FromResult("tok"), "me@gmail.test"), push);

    private (IPstEngine Pst, SyncStateStore State) Open(string name)
    {
        var path = Path.Combine(_dir, name + ".pst");
        var pst = File.Exists(path) ? PstEngineFactory.Open(path, true) : PstEngineFactory.Create(path, "me@gmail.test");
        return (pst, new SyncStateStore(Path.Combine(_dir, name + ".sync")));
    }

    private static MailFolder Folder(IPstEngine pst, string name) => pst.AllFolders().Single(f => f.Name == name);
    private static MailSummary Msg(IPstEngine pst, string folder, string subject) => pst.GetMessages(Folder(pst, folder)).Single(m => m.Subject == subject);
    private static Task<MirrorSyncResult> Sync(IMailSyncSource s, IPstEngine pst, SyncStateStore st) => MirrorSyncEngine.SyncAsync(s, pst, st, new MirrorSyncOptions(12), null, CancellationToken.None);

    [Fact]
    public async Task Labels_become_folders_and_a_message_appears_in_each_of_its_label_folders()
    {
        if (!Native) return;
        var gmail = Server();
        var (pst, state) = Open("a");
        using (pst) using (state)
        {
            var r = await Sync(Source(gmail), pst, state);
            Assert.Equal(0, r.Failed);
            var names = pst.AllFolders().Select(f => f.Name).ToList();
            foreach (var n in new[] { "Inbox", "Sent", "Drafts", "Starred", "Work", "Reports" }) Assert.Contains(n, names);
            Assert.DoesNotContain("UNREAD", names);                                              // pseudo labels are not folders
            Assert.DoesNotContain("CATEGORY_PROMOTIONS", names);
            Assert.DoesNotContain("Trash", names);                                               // Trash is the file's own Deleted Items
            Assert.Equal("Work", pst.FindFolder(Folder(pst, "Reports").ParentNid)!.Name);        // "Work/Reports" nests under "Work"
            Assert.Equal(["Inbox and starred", "Inbox only"], pst.GetMessages(Folder(pst, "Inbox")).Select(m => m.Subject).OrderBy(x => x).ToArray());
            Assert.Equal("Inbox and starred", Assert.Single(pst.GetMessages(Folder(pst, "Starred"))).Subject);       // a message with two labels is in both folders
            Assert.Equal("Trashed", Assert.Single(pst.GetMessages(pst.DeletedItemsFolder()!)).Subject);
            Assert.DoesNotContain(pst.AllFolders().SelectMany(f => pst.GetMessages(f)), m => m.Subject == "Too old");   // outside the keep window
            Assert.False(Msg(pst, "Inbox", "Inbox only").IsRead);                                // unread comes from the UNREAD label
            Assert.True(Msg(pst, "Inbox", "Inbox and starred").IsRead);
            Assert.True(pst.OpenMessage(Msg(pst, "Starred", "Inbox and starred")).Summary.Flagged);   // starred = flagged
            Assert.Empty(pst.Scan().Findings);

            var again = await Sync(Source(gmail), pst, state);
            Assert.False(again.Changed);
        }
    }

    [Fact]
    public async Task Reading_starring_and_moving_in_the_copy_become_label_changes()
    {
        if (!Native) return;
        var gmail = Server();
        var (pst, state) = Open("b");
        using (pst) using (state)
        {
            await Sync(Source(gmail), pst, state);
            pst.SetReadState(Msg(pst, "Inbox", "Inbox only"), true);
            pst.SetFlagged(Msg(pst, "Inbox", "Inbox only"), true);
            pst.MoveMessage(Msg(pst, "Work", "In a user label"), Folder(pst, "Reports"));
            var r = await Sync(Source(gmail), pst, state);
            Assert.Equal(0, r.Failed);
            Assert.Equal(3, r.Pushed);
            Assert.DoesNotContain("UNREAD", gmail.Msgs["g1"]);
            Assert.Contains("STARRED", gmail.Msgs["g1"]);
            Assert.DoesNotContain("Label_1", gmail.Msgs["g3"]);                                  // moved: the destination label is added, the source label removed
            Assert.Contains("Label_2", gmail.Msgs["g3"]);
            var again = await Sync(Source(gmail), pst, state);
            Assert.Equal(0, again.Pushed);
            Assert.Equal("In a user label", Assert.Single(pst.GetMessages(Folder(pst, "Reports")), m => m.Subject == "In a user label").Subject);
            Assert.Empty(pst.Scan().Findings);
        }
    }

    [Fact]
    public async Task Delete_goes_to_trash_and_permanent_delete_is_remembered_but_not_sent()
    {
        if (!Native) return;
        var gmail = Server();
        var (pst, state) = Open("c");
        using (pst) using (state)
        {
            await Sync(Source(gmail), pst, state);
            pst.MoveMessage(Msg(pst, "Inbox", "Inbox only"), pst.DeletedItemsFolder()!);          // Outlook's Delete
            var r = await Sync(Source(gmail), pst, state);
            Assert.Equal(1, r.Pushed);
            Assert.Contains("TRASH", gmail.Msgs["g1"]);
            Assert.DoesNotContain("INBOX", gmail.Msgs["g1"]);
            Assert.Contains(gmail.Log, l => l == "POST /messages/g1/trash");

            pst.DeleteMessage(pst.GetMessages(pst.DeletedItemsFolder()!).Single(m => m.Subject == "Inbox only"));      // delete again: permanent
            r = await Sync(Source(gmail), pst, state);
            Assert.Equal(0, r.Failed);                                                           // not an error: Gmail empties Trash itself
            Assert.Contains("TRASH", gmail.Msgs["g1"]);                                           // still on the server
            var again = await Sync(Source(gmail), pst, state);
            Assert.DoesNotContain(pst.GetMessages(pst.DeletedItemsFolder()!), m => m.Subject == "Inbox only");       // and not downloaded again
            Assert.False(again.Changed);
            Assert.Empty(pst.Scan().Findings);
        }
    }

    [Fact]
    public async Task A_folder_created_in_the_copy_becomes_a_label_and_offline_changes_wait()
    {
        if (!Native) return;
        var gmail = Server();
        var (pst, state) = Open("d");
        using (pst) using (state)
        {
            await Sync(Source(gmail), pst, state);
            var sub = pst.CreateFolder(Folder(pst, "Work").Nid, "Plans");
            pst.MoveMessage(Msg(pst, "Work", "In a user label"), sub);
            var r = await Sync(Source(gmail), pst, state);
            Assert.Equal(2, r.Pushed);
            var plans = gmail.Labels.Single(l => l.Value == "Work/Plans");                       // nested under its parent's name
            Assert.Contains(plans.Key, gmail.Msgs["g3"]);

            pst.SetReadState(Msg(pst, "Inbox", "Inbox only"), true);
            gmail.Offline = true;
            r = await Sync(Source(gmail), pst, state);
            Assert.True(r.Offline);
            Assert.Equal(1, r.PendingLocal);
            gmail.Offline = false;
            r = await Sync(Source(gmail), pst, state);
            Assert.Equal(1, r.Pushed);
            Assert.DoesNotContain("UNREAD", gmail.Msgs["g1"]);
            Assert.Empty(pst.Scan().Findings);
        }
    }

    private sealed class FlakyGmail(int failures, HttpStatusCode code, string body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.EndsWith("/profile", StringComparison.Ordinal) && ++Calls <= failures)
                return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body), RequestMessage = request });
            var json = path.EndsWith("/profile", StringComparison.Ordinal) ? """{"emailAddress":"me@gmail.test"}""" : """{"labels":[{"id":"INBOX","name":"INBOX","type":"system"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json), RequestMessage = request });
        }
    }

    [Theory]
    [InlineData(429, "{}")]
    [InlineData(403, """{"error":{"errors":[{"reason":"rateLimitExceeded"}]}}""")]
    public async Task A_rate_limited_request_is_repeated_after_a_pause(int status, string body)
    {
        var handler = new FlakyGmail(1, (HttpStatusCode)status, body);
        var box = new GmailMailbox(new HttpClient(handler), _ => ValueTask.FromResult("tok"), "me@gmail.test");
        var labels = await box.ListLabelNamesAsync();
        Assert.Equal("INBOX", Assert.Single(labels).Id);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_403_that_is_not_about_the_rate_limit_is_not_repeated()
    {
        var handler = new FlakyGmail(5, HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"insufficientPermissions"}]}}""");
        var box = new GmailMailbox(new HttpClient(handler), _ => ValueTask.FromResult("tok"), "me@gmail.test");
        await Assert.ThrowsAsync<GmailReadException>(() => box.ListLabelNamesAsync());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_message_in_several_labels_is_downloaded_once_per_run_and_a_read_only_sign_in_pushes_nothing()
    {
        if (!Native) return;
        var gmail = Server();
        var (pst, state) = Open("e");
        using (pst) using (state)
        {
            await Sync(Source(gmail, push: false), pst, state);
            Assert.Equal(1, gmail.Log.Count(l => l == "GET /messages/g2"));                       // in Inbox and Starred, fetched once
            pst.SetReadState(Msg(pst, "Inbox", "Inbox only"), true);
            var r = await Sync(Source(gmail, push: false), pst, state);
            Assert.Equal(0, r.Pushed);
            Assert.Contains("UNREAD", gmail.Msgs["g1"]);
        }
    }
}
