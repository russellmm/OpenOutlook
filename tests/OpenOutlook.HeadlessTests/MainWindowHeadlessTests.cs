using System.Net;
using System.Reflection;
using Xunit;
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
        new OpenOutlook.Auth.ConnectedAccountRegistry().Upsert(new OpenOutlook.Auth.ConnectedAccount(
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
        new OpenOutlook.Auth.ConnectedAccountRegistry().Upsert(account);
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
}
