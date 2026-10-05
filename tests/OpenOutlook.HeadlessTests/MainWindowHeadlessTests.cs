using System.Net;
using System.Reflection;
using Xunit;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenOutlook.Desktop;
using OpenOutlook.PstNative;
using PstCore;

namespace OpenOutlook.HeadlessTests;

/// <summary>
/// Drives the real MainWindow in Avalonia's headless platform (no window ever appears on screen): opens a copy of
/// the OPENOUTLOOK_TEST_PST fixture, selects mail, sends keys and takes screenshots. Screenshots go to
/// OO_HEADLESS_OUT (default: a temp folder).
/// </summary>
public sealed class MainWindowHeadlessTests
{
    private static string? Fixture()
    {
        var path = Environment.GetEnvironmentVariable("OPENOUTLOOK_TEST_PST");
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !OpenPst.NativeLibraryLoader.IsAvailable ? null : path;
    }

    /// <summary>The account registry lives in a scratch folder shared by the tests of this run (a registry holds at most two accounts per provider), so each test starts it empty.</summary>
    private static OpenOutlook.Auth.ConnectedAccountRegistry ResetRegistry()
    {
        var registry = new OpenOutlook.Auth.ConnectedAccountRegistry();
        registry.Save([]);
        return registry;
    }

    private static string OutDir()
    {
        var dir = Environment.GetEnvironmentVariable("OO_HEADLESS_OUT") ?? Path.Combine(Path.GetTempPath(), "oo-headless-shots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        w.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        w.CaptureRenderedFrame()?.Save(Path.Combine(OutDir(), name + ".png"));
    }

    private static async Task WaitUntil(Func<bool> condition, int ms = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static T Call<T>(object target, string name, params object?[] args) =>
        (T)target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, args)!;

    private static void CallVoid(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, args);

    private static async Task<(MainWindow Window, string Copy)> OpenFixtureAsync(string fixture)
    {
        var copy = Path.Combine(Path.GetTempPath(), "oo-headless-" + Guid.NewGuid().ToString("N") + ".pst");
        File.Copy(fixture, copy);
        var window = new MainWindow { Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Call<Task<bool>>(window, "OpenArchiveAsync", copy, false);
        var tree = window.FindControl<TreeView>("FolderTree")!;
        var items = tree.GetLogicalDescendants().OfType<TreeViewItem>().Where(i => i.Tag?.GetType().Name == "FolderSelection").ToList();
        TreeViewItem? best = null;
        var bestCount = 0;
        foreach (var item in items)
        {
            var folder = (MailFolder)item.Tag!.GetType().GetProperty("Folder")!.GetValue(item.Tag)!;
            if (folder.ContentCount > bestCount) { best = item; bestCount = folder.ContentCount; }
        }
        Assert.NotNull(best);
        tree.SelectedItem = best;
        var list = window.FindControl<DataGrid>("MessageList")!;
        await WaitUntil(() => list.CollectionView?.Cast<object>().Any() == true);
        return (window, copy);
    }

    private static List<object> MailRows(DataGrid list) =>
        list.CollectionView!.Cast<object>().Where(o => o.GetType().Name == "MessageListRow").ToList();

    private static void Cleanup(string path)
    {
        foreach (var f in new[] { path, path + ".lck", path + ".journal" })
            try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { }
    }

    [AvaloniaFact]
    public async Task Window_opens_an_archive_lists_mail_and_reads_a_message()
    {
        var fixture = Fixture();
        if (fixture is null) return;
        var (window, copy) = await OpenFixtureAsync(fixture);
        try
        {
            var list = window.FindControl<DataGrid>("MessageList")!;
            Shot(window, "01-folder-list");
            list.SelectedItem = MailRows(list)[0];
            var subject = window.FindControl<TextBlock>("SubjectText")!;
            await WaitUntil(() => !string.IsNullOrEmpty(subject.Text) && subject.Text != "Select a folder to begin" && subject.Text != "Select a message");
            Shot(window, "02-message-read");
            Assert.False(string.IsNullOrEmpty(subject.Text));
            Assert.NotEqual("Select a folder to begin", subject.Text);
        }
        finally { window.Close(); Cleanup(copy); }
    }

    [AvaloniaFact]
    public async Task Space_bar_pages_the_message_then_moves_to_the_next_one()
    {
        var fixture = Fixture();
        if (fixture is null) return;
        var (window, copy) = await OpenFixtureAsync(fixture);
        try
        {
            var list = window.FindControl<DataGrid>("MessageList")!;
            var rows = MailRows(list);
            Assert.True(rows.Count >= 2);
            list.SelectedItem = rows[0];
            var subject = window.FindControl<TextBlock>("SubjectText")!;
            await WaitUntil(() => !string.IsNullOrEmpty(subject.Text) && subject.Text != "Select a folder to begin");
            list.Focus();
            // enough Space presses to run off the end of any message: the selection must move on
            for (var i = 0; i < 400 && ReferenceEquals(list.SelectedItem, rows[0]); i++)
            {
                list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, Source = list });
                await WaitUntil(() => false, 30);
            }
            Shot(window, "03-after-space");
            Assert.False(ReferenceEquals(list.SelectedItem, rows[0]), "Space never advanced to the next message");
        }
        finally { window.Close(); Cleanup(copy); }
    }

    [AvaloniaFact]
    public async Task Portrait_window_gives_the_whole_width_to_the_reading_pane_and_back_restores_it()
    {
        var fixture = Fixture();
        if (fixture is null) return;
        var (window, copy) = await OpenFixtureAsync(fixture);
        try
        {
            var grid = window.FindControl<Grid>("PaneGrid")!;
            var list = window.FindControl<DataGrid>("MessageList")!;
            list.SelectedItem = MailRows(list)[0];
            var before = grid.ColumnDefinitions.Select(c => c.Width).ToArray();
            window.Width = 620;
            window.Height = 1000;
            Dispatcher.UIThread.RunJobs();
            CallVoid(window, "EnterPortraitReading");
            Dispatcher.UIThread.RunJobs();
            Shot(window, "04-portrait-reading");
            var back = window.FindControl<Button>("ReaderBackButton")!;
            Assert.True(back.IsVisible);
            Assert.Equal(0, grid.ColumnDefinitions[2].Width.Value);
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(back.IsVisible);
            Assert.Equal(before, grid.ColumnDefinitions.Select(c => c.Width).ToArray());
            window.Width = 1440;
            window.Height = 900;
            Dispatcher.UIThread.RunJobs();
            CallVoid(window, "EnterPortraitReading");
            Assert.False(back.IsVisible);   // landscape windows never enter portrait reading
        }
        finally { window.Close(); Cleanup(copy); }
    }

    [AvaloniaFact]
    public async Task Check_and_repair_window_scans_the_archive()
    {
        var fixture = Fixture();
        if (fixture is null) return;
        var (window, copy) = await OpenFixtureAsync(fixture);
        try
        {
            var stores = (System.Collections.IDictionary)typeof(MainWindow).GetField("_stores", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var store = (IPstEngine)stores[copy]!;
            var repair = new ArchiveRepairWindow("test.pst", store.CanWrite, () => Task.Run(store.Scan), () => Task.Run(store.Repair));
            repair.Show();
            var log = repair.GetVisualDescendants().OfType<TextBox>().First();
            await WaitUntil(() => log.Text is { Length: > 0 } t && t != "Scanning...");
            Shot(repair, "05-check-repair");
            Assert.False(string.IsNullOrWhiteSpace(log.Text));
            Assert.DoesNotContain("failed", log.Text!);
            repair.Close();
        }
        finally { window.Close(); Cleanup(copy); }
    }

    [AvaloniaFact]
    public async Task Gmail_account_appears_with_its_standard_folders_and_survives_selection()
    {
        // an account in the scratch registry (XDG_DATA_HOME is a temp folder, see TestAppBuilder); no network or keyring is needed to show the tree
        ResetRegistry().Upsert(new OpenOutlook.Auth.ConnectedAccount(
            OpenOutlook.Auth.OAuthProvider.Google, "gmail-account-1", "someone@gmail.test", "client", DateTimeOffset.UtcNow));
        var window = new MainWindow { Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var tree = window.FindControl<TreeView>("FolderTree")!;
            var root = tree.Items.OfType<TreeViewItem>().Single(i => i.Tag is OpenOutlook.Auth.ConnectedAccount { AccountId: "gmail-account-1" });
            Assert.Equal("someone@gmail.test", root.Header?.ToString());
            var names = root.Items.OfType<TreeViewItem>().Select(i => i.Header?.ToString()).ToList();
            foreach (var expected in new[] { "Inbox", "Starred", "Sent", "Drafts", "Spam", "Trash" }) Assert.Contains(expected, names);
            tree.SelectedItem = root.Items.OfType<TreeViewItem>().First();      // no token available: it must fail politely, not crash
            await WaitUntil(() => window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Could not load", StringComparison.Ordinal) == true, 8000);
            Shot(window, "06-gmail-tree");
            Assert.StartsWith("Could not load", window.FindControl<TextBlock>("StatusText")!.Text);
        }
        finally { window.Close(); }
    }

    /// <summary>A small stateful Gmail: labels per message, changed by modify / batchModify / trash.</summary>
    private sealed class FakeGmail(Dictionary<string, HashSet<string>> labels) : HttpMessageHandler
    {
        public readonly List<string> Log = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Replace("/gmail/v1/users/me", "");
            var body = request.Content is null ? "" : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            if (path != "/profile") Log.Add($"{request.Method} {path}");
            string json;
            if (path == "/profile") json = """{"emailAddress":"me@example.org"}""";
            else if (path == "/labels") json = """{"labels":[]}""";
            else if (path == "/messages" && request.Method == HttpMethod.Get)
            {
                var label = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["labelIds"];
                json = "{\"messages\":[" + string.Join(",", labels.Where(p => p.Value.Contains(label!)).Select(p => $"{{\"id\":\"{p.Key}\"}}")) + "]}";
            }
            else if (path.EndsWith("/modify", StringComparison.Ordinal) || path == "/messages/batchModify")
            {
                var doc = System.Text.Json.JsonDocument.Parse(body).RootElement;
                var ids = path == "/messages/batchModify" ? doc.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!) : [path.Split('/')[2]];
                foreach (var id in ids)
                {
                    foreach (var l in doc.GetProperty("addLabelIds").EnumerateArray()) labels[id].Add(l.GetString()!);
                    foreach (var l in doc.GetProperty("removeLabelIds").EnumerateArray()) labels[id].Remove(l.GetString()!);
                }
                json = "{}";
            }
            else if (path.EndsWith("/trash", StringComparison.Ordinal))
            {
                var id = path.Split('/')[2];
                labels[id].Remove("INBOX"); labels[id].Add("TRASH");
                json = "{}";
            }
            else if (path == "/messages/rich/attachments/att1") json = "{\"data\":\"YXR0YWNoZWQ\",\"size\":8}";
            else if (path == "/messages/rich")
            {
                json = "{\"id\":\"rich\",\"threadId\":\"thread-9\",\"labelIds\":[\"INBOX\"],\"internalDate\":\"1700000000000\",\"sizeEstimate\":2500,\"snippet\":\"s\"," +
                       "\"payload\":{\"mimeType\":\"multipart/mixed\",\"headers\":[{\"name\":\"From\",\"value\":\"Ann <ann@example.org>\"},{\"name\":\"Subject\",\"value\":\"Plans\"}," +
                       "{\"name\":\"To\",\"value\":\"me@example.org, Bob <bob@example.org>\"},{\"name\":\"Cc\",\"value\":\"carol@example.org\"},{\"name\":\"Message-ID\",\"value\":\"<m1@example.org>\"}]," +
                       "\"parts\":[{\"mimeType\":\"text/plain\",\"body\":{\"data\":\"aGVsbG8\"}}," +
                       "{\"mimeType\":\"text/plain\",\"filename\":\"notes.txt\",\"body\":{\"attachmentId\":\"att1\",\"size\":8}}]}}";
            }
            else if (path.StartsWith("/messages/", StringComparison.Ordinal))
            {
                var id = path.Split('/')[2];
                var labelsJson = string.Join(",", labels[id].Select(l => "\"" + l + "\""));
                json = "{\"id\":\"" + id + "\",\"threadId\":\"t\",\"labelIds\":[" + labelsJson + "],\"internalDate\":\"1700000000000\",\"sizeEstimate\":1500,\"snippet\":\"snippet " + id + "\"," +
                       "\"payload\":{\"mimeType\":\"text/plain\",\"headers\":[{\"name\":\"From\",\"value\":\"Ann <ann@example.org>\"},{\"name\":\"Subject\",\"value\":\"Subject " + id + "\"},{\"name\":\"To\",\"value\":\"me@example.org\"}]," +
                       "\"body\":{\"data\":\"aGVsbG8\"}}}";
            }
            else json = "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"), RequestMessage = request });
        }
    }

    [AvaloniaFact]
    public async Task Gmail_actions_change_the_server_and_the_list()
    {
        var labels = new Dictionary<string, HashSet<string>>
        {
            ["m1"] = ["INBOX", "UNREAD"], ["m2"] = ["INBOX", "UNREAD"], ["m3"] = ["INBOX"]
        };
        var server = new FakeGmail(labels);
        var account = new OpenOutlook.Auth.ConnectedAccount(OpenOutlook.Auth.OAuthProvider.Google, "gmail-account-2", "me@example.org", "client", DateTimeOffset.UtcNow,
            ["https://www.googleapis.com/auth/gmail.modify"]);
        ResetRegistry().Upsert(account);
        var window = new MainWindow { Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var boxes = (System.Collections.IDictionary)typeof(MainWindow).GetField("_gmailBoxes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            boxes[account.AccountId] = new OpenOutlook.Providers.Google.GmailMailbox(new HttpClient(server), _ => ValueTask.FromResult("tok"), "me@example.org");
            var tree = window.FindControl<TreeView>("FolderTree")!;
            var root = tree.Items.OfType<TreeViewItem>().Single(i => i.Tag is OpenOutlook.Auth.ConnectedAccount { Provider: OpenOutlook.Auth.OAuthProvider.Google });
            tree.SelectedItem = root.Items.OfType<TreeViewItem>().First(i => i.Header?.ToString()?.StartsWith("Inbox", StringComparison.Ordinal) == true);
            var list = window.FindControl<DataGrid>("MessageList")!;
            await WaitUntil(() => list.CollectionView?.Cast<object>().Count() == 3);
            Shot(window, "07-gmail-inbox");

            object Row(string id) => list.CollectionView!.Cast<object>().Single(r => r.GetType().GetProperty("Message")!.GetValue(r)!.GetType().GetProperty("Id")!.GetValue(r.GetType().GetProperty("Message")!.GetValue(r)) as string == id);
            async Task Act(string action)
            {
                await Call<Task>(window, "ExecuteMailActionAsync", action);
                Dispatcher.UIThread.RunJobs();
            }
            bool IsRead(object row) => (bool)row.GetType().GetProperty("Message")!.GetValue(row)!.GetType().GetProperty("IsRead")!.GetValue(row.GetType().GetProperty("Message")!.GetValue(row))!;

            list.SelectedItem = Row("m1");
            await Act("read");
            Assert.DoesNotContain("UNREAD", labels["m1"]);
            Assert.True(IsRead(Row("m1")));
            await Act("unread");
            Assert.Contains("UNREAD", labels["m1"]);
            await Act("flag");
            Assert.Contains("STARRED", labels["m1"]);

            // double-click / pop-out opens the Gmail message in its own window
            list.SelectedItem = Row("m1");
            var contentField = typeof(MainWindow).GetField("_gmailContent", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await WaitUntil(() => contentField.GetValue(window) is not null, 8000);
            await Call<Task>(window, "OpenSelectedMessageWindowAsync");
            Assert.Equal("Message opened in a separate window.", window.FindControl<TextBlock>("StatusText")!.Text);
            foreach (var w in ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime?)Application.Current?.ApplicationLifetime)?.Windows.OfType<MessageWindow>().ToList() ?? []) w.Close();

            list.SelectedItem = Row("m2");
            await Act("archive");
            Assert.DoesNotContain("INBOX", labels["m2"]);
            await WaitUntil(() => list.CollectionView!.Cast<object>().Count() == 2);

            list.SelectedItem = Row("m3");
            await Act("delete");
            Assert.Contains("TRASH", labels["m3"]);
            Assert.DoesNotContain("INBOX", labels["m3"]);
            await WaitUntil(() => list.CollectionView!.Cast<object>().Count() == 1);
            Shot(window, "08-gmail-after-actions");
            Assert.Contains("POST /messages/m1/modify", server.Log);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Native_web_view_is_hidden_while_the_file_screen_or_options_are_open()
    {
        var window = new MainWindow { Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var webViewField = typeof(MainWindow).GetField("_webViewAvailable", BindingFlags.NonPublic | BindingFlags.Instance)!;
            if (!(bool)webViewField.GetValue(window)!) return;                 // no web view on this system: nothing can overlay
            typeof(MainWindow).GetField("_embeddedHtmlActive", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            var web = window.FindControl<Control>("MainHtmlWebView")!;
            var backstage = window.FindControl<Control>("BackstageHost")!;
            var options = window.FindControl<Control>("OptionsHost")!;
            web.IsVisible = true;
            Assert.True(web.IsVisible);
            backstage.IsVisible = true;
            Assert.False(web.IsVisible);                                        // the File screen must not be covered by the native view
            web.IsVisible = true;
            Assert.False(web.IsVisible);                                        // and nothing may bring it back on top while it is open
            backstage.IsVisible = false;
            Assert.True(web.IsVisible);                                         // closing returns the message
            options.IsVisible = true;
            Assert.False(web.IsVisible);
            options.IsVisible = false;
            Assert.True(web.IsVisible);
            await Task.CompletedTask;
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Folder_picker_looks_and_behaves_like_outlooks_Move_Items()
    {
        var root = new FolderPickItem("someone@example.org", null, selectable: false);
        var inbox = new FolderPickItem("Inbox", "inbox", unread: 17, selectable: false);   // the folder the messages are already in
        var travel = new FolderPickItem("Travel", "travel", unread: 3);
        travel.Children.Add(new FolderPickItem("2026", "travel-2026"));
        root.Children.AddRange([inbox, new FolderPickItem("Drafts", "drafts", unread: 2), travel, new FolderPickItem("Trash", "trash")]);
        var created = 0;
        var picker = new FolderPickerWindow("Move Items", "Move the selected items to:", [root], async parent =>
        {
            created++;
            await Task.Yield();
            return new FolderPickItem("New one", "new-" + created);
        });
        picker.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var tree = picker.GetVisualDescendants().OfType<TreeView>().Single();
            var buttons = picker.GetVisualDescendants().OfType<Button>().Where(b => b.Content is string).ToDictionary(b => b.Content?.ToString()!);
            Assert.Equal(["OK", "Cancel", "New..."], buttons.Keys.OrderBy(k => k == "OK" ? 0 : k == "Cancel" ? 1 : 2));
            Assert.False(buttons["OK"].IsEnabled);                                        // nothing selected yet
            TreeViewItem Node(string tag) => tree.GetLogicalDescendants().OfType<TreeViewItem>().Single(n => (n.Tag as FolderPickItem)?.Tag as string == tag);
            tree.SelectedItem = Node("inbox");
            Assert.False(buttons["OK"].IsEnabled);                                        // the current folder is not a destination
            tree.SelectedItem = Node("travel-2026");
            Assert.True(buttons["OK"].IsEnabled);
            tree.SelectedItem = Node("trash");
            Shot(picker, "09-move-items-picker");
            buttons["New..."].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => created == 1);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("new-1", ((tree.SelectedItem as TreeViewItem)?.Tag as FolderPickItem)?.Tag);   // the new folder is added and selected
            buttons["OK"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("new-1", picker.Result?.Tag);
        }
        finally { picker.Close(); }
    }

    [AvaloniaFact]
    public void Right_click_menu_exists_before_any_archive_folder_has_loaded()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };     // not shown: the menu is built at construction, and showing would start the native web view
        try
        {
            var flyout = Assert.IsType<MenuFlyout>(window.FindControl<DataGrid>("MessageList")!.ContextFlyout);
            var headers = flyout.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToList();
            foreach (var expected in new[] { "Mark as Read", "Mark as Unread", "Flag", "Clear Flag", "Delete" })
                Assert.Contains(expected, headers);
            Assert.Contains(headers, h => h!.StartsWith("Move to Folder", StringComparison.Ordinal));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Online_drag_payload_round_trips_and_rejects_foreign_text()
    {
        var text = MainWindow.FormatOnlineDragPayload("acct-1", "INBOX", ["m1", "m2", "AAMk-long/id=="]);
        var parsed = MainWindow.ParseOnlineDragPayload(text)!.Value;
        Assert.Equal("acct-1", parsed.AccountId);
        Assert.Equal("INBOX", parsed.SourceFolderId);
        Assert.Equal(["m1", "m2", "AAMk-long/id=="], parsed.MessageIds);
        Assert.Null(MainWindow.ParseOnlineDragPayload("hello"));
        Assert.Null(MainWindow.ParseOnlineDragPayload(null));
        Assert.Null(MainWindow.ParseOnlineDragPayload("OpenOutlook-online-move:v1\nonly-account\n"));
    }

    [AvaloniaFact]
    public async Task Dropping_messages_on_a_Gmail_folder_moves_them_and_adding_a_label_copies()
    {
        var labels = new Dictionary<string, HashSet<string>>
        {
            ["m1"] = ["INBOX", "UNREAD"], ["m2"] = ["INBOX"], ["m3"] = ["INBOX"]
        };
        var server = new FakeGmail(labels);
        var account = new OpenOutlook.Auth.ConnectedAccount(OpenOutlook.Auth.OAuthProvider.Google, "gmail-account-3", "me@example.org", "client", DateTimeOffset.UtcNow,
            ["https://www.googleapis.com/auth/gmail.modify"]);
        ResetRegistry().Upsert(account);
        var window = new MainWindow { Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var boxes = (System.Collections.IDictionary)typeof(MainWindow).GetField("_gmailBoxes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            boxes[account.AccountId] = new OpenOutlook.Providers.Google.GmailMailbox(new HttpClient(server), _ => ValueTask.FromResult("tok"), "me@example.org");
            var tree = window.FindControl<TreeView>("FolderTree")!;
            var root = tree.Items.OfType<TreeViewItem>().Single(i => i.Tag is OpenOutlook.Auth.ConnectedAccount { AccountId: "gmail-account-3" });
            TreeViewItem Node(string header) => root.Items.OfType<TreeViewItem>().First(i => i.Header?.ToString()?.StartsWith(header, StringComparison.Ordinal) == true);
            tree.SelectedItem = Node("Inbox");
            var list = window.FindControl<DataGrid>("MessageList")!;
            await WaitUntil(() => list.CollectionView?.Cast<object>().Count() == 3);

            // what a drop on the Spam folder does
            await Call<Task>(window, "MoveOnlineAsync", new List<string> { "m1", "m2" }, Node("Spam").Tag!, false);
            Assert.True(labels["m1"].Contains("SPAM"), "status: " + window.FindControl<TextBlock>("StatusText")!.Text + " | server: " + string.Join(",", server.Log));
            Assert.Contains("SPAM", labels["m1"]); Assert.DoesNotContain("INBOX", labels["m1"]);
            Assert.Contains("SPAM", labels["m2"]); Assert.DoesNotContain("INBOX", labels["m2"]);
            await WaitUntil(() => list.CollectionView!.Cast<object>().Count() == 1);       // the list reloaded: only m3 is left in the Inbox

            // "Copy Here" keeps the message where it is and only adds the label
            await Call<Task>(window, "MoveOnlineAsync", new List<string> { "m3" }, Node("Spam").Tag!, true);
            Assert.Contains("SPAM", labels["m3"]); Assert.Contains("INBOX", labels["m3"]);

            // Gmail cannot keep a copy in Trash: copying there is a move
            await Call<Task>(window, "MoveOnlineAsync", new List<string> { "m3" }, Node("Trash").Tag!, true);
            Assert.Contains("TRASH", labels["m3"]); Assert.DoesNotContain("INBOX", labels["m3"]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Real_mouse_and_drop_events_move_a_Gmail_message_onto_a_folder()
    {
        var labels = new Dictionary<string, HashSet<string>> { ["m1"] = ["INBOX", "UNREAD"], ["m2"] = ["INBOX"] };
        var server = new FakeGmail(labels);
        var account = new OpenOutlook.Auth.ConnectedAccount(OpenOutlook.Auth.OAuthProvider.Google, "gmail-account-4", "me@example.org", "client", DateTimeOffset.UtcNow,
            ["https://www.googleapis.com/auth/gmail.modify"]);
        ResetRegistry().Upsert(account);
        var window = new MainWindow { Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var boxes = (System.Collections.IDictionary)typeof(MainWindow).GetField("_gmailBoxes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            boxes[account.AccountId] = new OpenOutlook.Providers.Google.GmailMailbox(new HttpClient(server), _ => ValueTask.FromResult("tok"), "me@example.org");
            var tree = window.FindControl<TreeView>("FolderTree")!;
            var root = tree.Items.OfType<TreeViewItem>().Single(i => i.Tag is OpenOutlook.Auth.ConnectedAccount { AccountId: "gmail-account-4" });
            TreeViewItem Node(string header) => root.Items.OfType<TreeViewItem>().First(i => i.Header?.ToString()?.StartsWith(header, StringComparison.Ordinal) == true);
            tree.SelectedItem = Node("Inbox");
            var list = window.FindControl<DataGrid>("MessageList")!;
            await WaitUntil(() => list.CollectionView?.Cast<object>().Count() == 2);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            // 1. the grid's drag handlers are attached without any archive having been opened, and pressing on a row (not yet selected) arms a drag
            Assert.True((bool)typeof(MainWindow).GetField("_moveDragWired", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!);
            var row = list.GetVisualDescendants().OfType<DataGridRow>().First();
            Point Center(Visual v) => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), window)!.Value;
            var rowPoint = Center(row);
            window.MouseDown(rowPoint, MouseButton.Left);
            Assert.True((bool)typeof(MainWindow).GetField("_messageDragCandidate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!);
            window.MouseMove(new Point(rowPoint.X + 40, rowPoint.Y + 4), RawInputModifiers.LeftMouseButton);                // past the drag threshold: the drag starts with the pressed row selected
            await WaitUntil(() => window.OnlineDragsStarted == 1, 3000);
            Assert.Equal(1, window.OnlineDragsStarted);                                   // (the system drag call itself never completes in the headless host)
            Assert.False((bool)typeof(MainWindow).GetField("_messageDragCandidate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!);
            window.MouseUp(new Point(rowPoint.X + 40, rowPoint.Y + 4), MouseButton.Left);

            var finalEffect = DragDropEffects.Move;
            window.AddHandler(DragDrop.DragOverEvent, (_, e) => finalEffect = e.DragEffects, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);   // what the drag ends up with, after every handler on the way

            // 2. the folder node accepts the drag payload of its own account and moves the dragged message
            var spam = Node("Spam");
            var spamPoint = Center(spam);
            var data = new DataObject();
            data.Set(DataFormats.Text, MainWindow.FormatOnlineDragPayload("gmail-account-4", "INBOX", ["m1"]));
            window.DragDrop(spamPoint, RawDragEventType.DragEnter, data, DragDropEffects.Move);
            window.DragDrop(spamPoint, RawDragEventType.DragOver, data, DragDropEffects.Move);
            Assert.Equal(DragDropEffects.Move, finalEffect);                              // the cursor must say "move allowed", not the red no-drop circle
            window.DragDrop(spamPoint, RawDragEventType.Drop, data, DragDropEffects.Move);
            await WaitUntil(() => labels["m1"].Contains("SPAM"), 5000);
            Assert.Contains("SPAM", labels["m1"]);
            Assert.DoesNotContain("INBOX", labels["m1"]);
            Assert.Contains("INBOX", labels["m2"]);                          // only the dragged message moved

            // 3. a drag from another account, or onto the folder it came from, is refused
            var foreign = new DataObject();
            foreign.Set(DataFormats.Text, MainWindow.FormatOnlineDragPayload("another-account", "INBOX", ["m2"]));
            finalEffect = DragDropEffects.Move;
            window.DragDrop(spamPoint, RawDragEventType.DragEnter, foreign, DragDropEffects.Move);     // a new drag session
            window.DragDrop(spamPoint, RawDragEventType.DragOver, foreign, DragDropEffects.Move);
            Assert.Equal(DragDropEffects.None, finalEffect);
            window.DragDrop(spamPoint, RawDragEventType.Drop, foreign, DragDropEffects.Move);
            await WaitUntil(() => false, 300);
            Assert.DoesNotContain("SPAM", labels["m2"]);
        }
        finally { window.Close(); }
    }

    private sealed class RecordingBackend(string address, string kind, bool reopen = false) : IComposeBackend
    {
        public List<string> Log { get; } = [];
        public ComposeDraft? LastDraft;
        public IReadOnlyList<ComposeFile>? LastFiles;
        public string Address => address;
        public string Kind => kind;
        public long MaxAttachmentBytes => kind == "Gmail" ? 25L * 1024 * 1024 : 150L * 1024 * 1024;
        public bool CanReopenDrafts => reopen;
        public string SavedMessage => "Draft saved.";
        public Task<LoadedDraft> LoadAsync(string draftId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SavedDraft> SaveAsync(string? draftId, ComposeDraft draft, IReadOnlyList<ComposeFile> files, IProgress<string> progress, CancellationToken cancellationToken = default)
        { Log.Add("save"); LastDraft = draft; LastFiles = files; foreach (var f in files) f.Saved = true; return Task.FromResult(new SavedDraft("draft-" + address, null)); }
        public Task SendAsync(string? draftId, ComposeDraft draft, IReadOnlyList<ComposeFile> files, IProgress<string> progress, CancellationToken cancellationToken = default)
        { Log.Add("send"); LastDraft = draft; LastFiles = files; return Task.CompletedTask; }
        public Task RemoveServerFileAsync(string draftId, string serverId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task Gmail_reply_reply_all_and_forward_are_prepared_from_the_original()
    {
        var account = new OpenOutlook.Auth.ConnectedAccount(OpenOutlook.Auth.OAuthProvider.Google, "gmail-account-3", "me@example.org", "client", DateTimeOffset.UtcNow,
            ["https://www.googleapis.com/auth/gmail.modify", "https://www.googleapis.com/auth/gmail.compose"]);
        ResetRegistry().Upsert(account);
        var window = new MainWindow { Width = 1200, Height = 800 };
        try
        {
            var server = new FakeGmail(new Dictionary<string, HashSet<string>>());
            var boxes = (System.Collections.IDictionary)typeof(MainWindow).GetField("_gmailBoxes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            boxes[account.AccountId] = new OpenOutlook.Providers.Google.GmailMailbox(new HttpClient(server), _ => ValueTask.FromResult("tok"), "me@example.org");
            var selectionType = typeof(MainWindow).GetNestedType("GmailFolderSelection", BindingFlags.NonPublic)!;
            var folder = Activator.CreateInstance(selectionType, account, "INBOX", "Inbox")!;
            var message = new OpenOutlook.Providers.Microsoft.GraphInboxMessage("rich", "Plans", "Ann <ann@example.org>", "me@example.org",
                DateTimeOffset.UtcNow, 100, true, true, "p");
            async Task<ComposeSeed> Seed(string action) => await Call<Task<ComposeSeed>>(window, "BuildGmailSeedAsync", folder, message, action);

            var reply = await Seed("reply");
            Assert.Equal("Ann <ann@example.org>", reply.To);
            Assert.Equal("Re: Plans", reply.Subject);
            Assert.Equal("<m1@example.org>", reply.InReplyTo);
            Assert.Equal("thread-9", reply.ThreadId);
            Assert.Contains("> hello", reply.Body);
            Assert.True(string.IsNullOrEmpty(reply.Cc));
            Assert.Null(reply.Files);

            var all = await Seed("replyAll");
            Assert.Contains("bob@example.org", all.Cc);
            Assert.Contains("carol@example.org", all.Cc);
            Assert.DoesNotContain("me@example.org", all.Cc);           // never reply to yourself
            Assert.DoesNotContain("ann@example.org", all.Cc);          // the sender is already in To

            var forward = await Seed("forward");
            Assert.Equal("Fw: Plans", forward.Subject);
            Assert.Contains("Forwarded message", forward.Body);
            Assert.Null(forward.InReplyTo);
            var file = Assert.Single(forward.Files!);
            Assert.Equal("notes.txt", file.Name);
            Assert.Equal("attached", System.Text.Encoding.UTF8.GetString(file.Data!));
            Assert.Contains("GET /messages/rich/attachments/att1", server.Log);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Compose_window_marks_unbuilt_buttons_locks_from_after_a_draft_and_sends_seed_files()
    {
        var backend = new RecordingBackend("me@gmail.test", "Gmail");
        var other = new RecordingBackend("me@hotmail.test", "Microsoft");
        var accounts = new List<ComposeAccount>
        {
            new("me@gmail.test", "Gmail", null, () => backend), new("me@hotmail.test", "Microsoft", null, () => other)
        };
        var seed = new ComposeSeed(To: "a@example.org", Subject: "Fwd: x", Body: "text",
            Files: [new ComposeFile { Name = "notes.txt", Data = [1, 2, 3], Size = 3 }]);
        var window = new ComposeWindow(accounts, accounts[0], seed) { Width = 1000, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var tips = window.GetVisualDescendants().OfType<Control>().Select(c => ToolTip.GetTip(c)?.ToString()).Where(t => t is not null).ToList();
            Assert.Contains(tips, t => t!.Contains("To be implemented", StringComparison.OrdinalIgnoreCase));

            var save = window.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b)?.ToString() == "Save draft");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => backend.Log.Contains("save"), 5000);
            Assert.Contains("save", backend.Log);
            Dispatcher.UIThread.RunJobs();
            var from = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Tag is ComposeAccount));
            from.SelectedIndex = 1;                                                    // a saved draft belongs to its account: the change is refused
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("me@gmail.test", window.SelectedAccount.Address);
            Assert.Equal(0, from.SelectedIndex);
            Assert.Equal("notes.txt", Assert.Single(backend.LastFiles!).Name);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Data_files_window_lists_accounts_and_saves_choices()
    {
        var rows = new List<DataFileRow>
        {
            new("acc1", "me@hotmail.test", "Microsoft", "C:/mail/me@hotmail.test.pst", 5 * 1024 * 1024, new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc), "up to date", new OpenOutlook.Mirror.MirrorAccountSettings(), true),
            new("acc2", "me@gmail.test", "Gmail", "C:/mail/me@gmail.test.pst", 0, null, "", new OpenOutlook.Mirror.MirrorAccountSettings(), false)
        };
        var saved = new List<(string Id, OpenOutlook.Mirror.MirrorAccountSettings S)>();
        var synced = new List<string>();
        var window = new DataFilesWindow(() => rows, id => { synced.Add(id); return Task.CompletedTask; }, _ => Task.FromResult<string?>(null), _ => { }, (id, s) => saved.Add((id, s)));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Shot(window, "11-data-files");
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains(texts, t => t.Contains("me@hotmail.test"));
            Assert.Contains(texts, t => t.Contains("5 MB") && t.Contains("up to date"));
            Assert.Contains(texts, t => t.Contains("not available yet"));                      // Gmail copies come later
            var months = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Content?.ToString() == "12 months"));
            Assert.Equal("12 months", ((ComboBoxItem)months.SelectedItem!).Content);           // default keep window
            months.SelectedIndex = 3;                                                          // 3 months
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, saved.Last().S.KeepMonths);
            Assert.True(saved.Last().S.Enabled);
            Assert.Equal(25L << 20, saved.Last().S.MaxAttachmentBytes);                        // default attachment cap
            var sync = window.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == "Sync now");
            sync.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["acc1"], synced);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Compose_window_looks_like_outlooks_and_sends_from_the_chosen_account()
    {
        var hotmail = new RecordingBackend("me@hotmail.test", "Microsoft");
        var gmail = new RecordingBackend("me@gmail.test", "Gmail");
        var locked = new ComposeAccount("old@gmail.test", "Gmail", "sign in again to allow sending", () => new RecordingBackend("old@gmail.test", "Gmail"));
        var accounts = new List<ComposeAccount>
        {
            new("me@hotmail.test", "Microsoft", null, () => hotmail), new("me@gmail.test", "Gmail", null, () => gmail), locked
        };
        var window = new ComposeWindow(accounts, accounts[0]) { Width = 1000, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Shot(window, "10-compose");
            // the From list offers every account; the one that cannot send is shown but not selectable
            var from = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Tag is ComposeAccount));
            var items = from.Items.OfType<ComboBoxItem>().ToList();
            Assert.Equal(3, items.Count);
            Assert.False(items[2].IsEnabled);
            Assert.Contains("sign in again", items[2].Content!.ToString());
            Assert.Equal("me@hotmail.test", window.SelectedAccount.Address);

            // switching the account before anything is saved changes who sends
            from.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("me@gmail.test", window.SelectedAccount.Address);

            var boxes = window.GetVisualDescendants().OfType<TextBox>().ToList();
            boxes.First(b => b.Watermark == "name@example.com").Text = "friend@example.org";
            var subject = boxes.Last(b => !b.AcceptsReturn && b.Watermark is null && b.IsVisible);
            subject.Text = "Hello";
            boxes.First(b => b.AcceptsReturn).Text = "Body text";
            var send = window.GetVisualDescendants().OfType<Button>().First(b => (b.Content as StackPanel)?.Children.OfType<TextBlock>().Any(t => t.Text == "Send") == true);
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => gmail.Log.Contains("send"), 5000);
            Assert.Contains("send", gmail.Log);
            Assert.Empty(hotmail.Log);                                                    // the other account was not used
            Assert.Equal("friend@example.org", gmail.LastDraft!.To);
            Assert.Equal("Body text", gmail.LastDraft.Body);
        }
        finally { window.Close(); }
    }
}
