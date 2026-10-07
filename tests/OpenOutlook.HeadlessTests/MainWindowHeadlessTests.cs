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
    public async Task Message_list_header_has_its_own_menu_and_importance_and_flag_columns()
    {
        var fixture = Fixture();
        if (fixture is null) return;
        var (window, copy) = await OpenFixtureAsync(fixture);
        try
        {
            var list = window.FindControl<DataGrid>("MessageList")!;
            Assert.Equal(7, list.Columns.Count);
            Assert.False(list.Columns[5].IsVisible);                                            // Importance and Flag are choices, not defaults
            Assert.False(list.Columns[6].IsVisible);
            window.SetColumnVisible(5, true);
            window.SetColumnVisible(6, true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(list.Columns[5].IsVisible && list.Columns[6].IsVisible);
            Assert.Equal(0, list.Columns[5].DisplayIndex);                                      // next to the paperclip, at the left
            Shot(window, "17-importance-flag-columns");
            var byDate = (Avalonia.Collections.DataGridCollectionView)list.ItemsSource!;
            Assert.Single(byDate.GroupDescriptions);                                            // date sections are on
            window.SortMessageList(3, ascending: true);                                         // sorting by Received keeps them
            Assert.Single(((Avalonia.Collections.DataGridCollectionView)list.ItemsSource!).GroupDescriptions);
            window.SortMessageList(5, ascending: false);
            var view = (Avalonia.Collections.DataGridCollectionView)list.ItemsSource!;
            Assert.Equal("ImportanceSort", view.SortDescriptions[0].PropertyPath);
            window.GroupMessageListBy(1);                                                       // Group By This Field on From
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("FromDisplay", ((Avalonia.Collections.DataGridCollectionView)list.ItemsSource!).GroupDescriptions.OfType<Avalonia.Collections.DataGridPathGroupDescription>().Single().PropertyName);
            window.SetColumnVisible(5, false);
            window.SetColumnVisible(6, false);
            Assert.False(list.Columns[5].IsVisible);
        }
        finally { window.Close(); Cleanup(copy); }
    }

    [AvaloniaFact]
    public async Task Arrange_by_chip_follows_the_arrangement_and_the_menu_choices_group_and_sort()
    {
        var fixture = Fixture();
        if (fixture is null) return;
        var (window, copy) = await OpenFixtureAsync(fixture);
        try
        {
            var list = window.FindControl<DataGrid>("MessageList")!;
            var chip = window.FindControl<Button>("ArrangeByButton")!;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("By date", (string)chip.Content!);                                  // the default is the date sections
            window.ArrangeMessageListBy(1);                                                     // From: grouped by sender and ordered by it
            Dispatcher.UIThread.RunJobs();
            var view = (Avalonia.Collections.DataGridCollectionView)list.ItemsSource!;
            Assert.Equal("FromDisplay", view.GroupDescriptions.OfType<Avalonia.Collections.DataGridPathGroupDescription>().Single().PropertyName);
            Assert.Equal("FromSort", view.SortDescriptions[0].PropertyPath);
            Assert.Contains("By from", (string)chip.Content!);
            window.ArrangeMessageListBy(4);                                                     // Size cannot be grouped: ordered only
            Dispatcher.UIThread.RunJobs();
            view = (Avalonia.Collections.DataGridCollectionView)list.ItemsSource!;
            Assert.Empty(view.GroupDescriptions);
            Assert.Equal("SizeBytes", view.SortDescriptions[0].PropertyPath);
            Assert.Contains("By size", (string)chip.Content!);
            window.ArrangeMessageListBy(3);                                                     // back to the date sections
            Dispatcher.UIThread.RunJobs();
            view = (Avalonia.Collections.DataGridCollectionView)list.ItemsSource!;
            Assert.Single(view.GroupDescriptions);
            Assert.Empty(view.SortDescriptions);
            Assert.Contains("By date", (string)chip.Content!);
        }
        finally { window.Close(); Cleanup(copy); }
    }

    [AvaloniaFact]
    public async Task Deleting_the_highlighted_message_highlights_the_one_that_takes_its_place()
    {
        var fixture = Fixture();
        if (fixture is null) return;
        var (window, copy) = await OpenFixtureAsync(fixture);
        try
        {
            var list = window.FindControl<DataGrid>("MessageList")!;
            var rows = MailRows(list);
            Assert.True(rows.Count >= 3, "the fixture folder needs at least three messages");
            var doomed = rows[1];
            var expected = rows[2];                                                             // the next message down
            list.SelectedItem = doomed;
            Dispatcher.UIThread.RunJobs();
            await window.ExecuteMailActionAsync("delete");
            await WaitUntil(() => !MailRows(list).Contains(doomed) && list.SelectedItem is not null);
            Assert.DoesNotContain(doomed, MailRows(list));
            Assert.Equal(((MessageListRow)expected).Summary.Nid, ((MessageListRow)list.SelectedItem!).Summary.Nid);
        }
        finally { window.Close(); Cleanup(copy); }
    }

    [AvaloniaFact]
    public async Task The_chosen_sort_survives_opening_another_folder_and_coming_back()
    {
        var fixture = Fixture();
        if (fixture is null) return;
        var (window, copy) = await OpenFixtureAsync(fixture);
        try
        {
            var list = window.FindControl<DataGrid>("MessageList")!;
            var tree = window.FindControl<TreeView>("FolderTree")!;
            var folders = tree.GetLogicalDescendants().OfType<TreeViewItem>().Where(i => i.Tag?.GetType().Name == "FolderSelection").ToList();
            var current = folders.First(i => ReferenceEquals(i, tree.SelectedItem));
            var other = folders.First(i => !ReferenceEquals(i, current));
            window.ArrangeMessageListBy(1);                                                     // From
            window.SortMessageList(1, ascending: false);                                        // ... descending
            Dispatcher.UIThread.RunJobs();
            tree.SelectedItem = other;
            await Task.Delay(400); Dispatcher.UIThread.RunJobs();
            tree.SelectedItem = current;
            await WaitUntil(() => list.CollectionView?.Cast<object>().Any() == true);
            var view = (Avalonia.Collections.DataGridCollectionView)list.ItemsSource!;
            Assert.Equal("FromSort", view.SortDescriptions[0].PropertyPath);
            Assert.Equal(System.ComponentModel.ListSortDirection.Descending, view.SortDescriptions[0].Direction);
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

    private sealed class FakeHost : IAccountSettingsHost
    {
        public List<OpenOutlook.Auth.ConnectedAccount> Accounts_ = [
            new(OpenOutlook.Auth.OAuthProvider.MicrosoftConsumers, "acc1", "me@hotmail.test", "c", DateTimeOffset.UtcNow, ["Mail.ReadWrite"]),
            new(OpenOutlook.Auth.OAuthProvider.Google, "acc2", "me@gmail.test", "c", DateTimeOffset.UtcNow, ["https://www.googleapis.com/auth/gmail.modify"])];
        public string? Default;
        public List<string> Log = [];
        public List<(string Id, OpenOutlook.Mirror.MirrorAccountSettings S)> Saved = [];
        public IReadOnlyList<OpenOutlook.Auth.ConnectedAccount> Accounts() => Accounts_;
        public string? DefaultAccountId => Default;
        public void SetDefaultAccount(string accountId) { Default = accountId; Log.Add("default " + accountId); }
        public Task AddAccountAsync() { Log.Add("add"); return Task.CompletedTask; }
        public Task RepairAccountAsync(OpenOutlook.Auth.ConnectedAccount account) { Log.Add("repair " + account.AccountId); return Task.CompletedTask; }
        public Task<string?> RemoveAccountAsync(OpenOutlook.Auth.ConnectedAccount account, Window owner) { Log.Add("remove " + account.AccountId); Accounts_.Remove(account); return Task.FromResult<string?>("removed"); }
        public IReadOnlyList<DataFileRow> DataFiles() =>
        [
            new("me@hotmail.test", "C:/mail/me@hotmail.test.pst", "Mailbox copy", 5 * 1024 * 1024, "up to date", "acc1", new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc), new OpenOutlook.Mirror.MirrorAccountSettings(), true),
            new("me@gmail.test", "C:/mail/me@gmail.test.pst", "Mailbox copy", 0, "", "acc2", null, new OpenOutlook.Mirror.MirrorAccountSettings(), true, false),
            new("rmarrash_outlook", "D:/email/rmarrash_outlook.pst", "Outlook data file", 700 * 1024 * 1024, "editable")
        ];
        public Task<string?> AddDataFileAsync(Window owner) { Log.Add("adddata"); return Task.FromResult<string?>(null); }
        public Task<string?> AddDataFileByPathAsync(string path) { Log.Add("addbypath " + path); return Task.FromResult<string?>("added"); }
        public Task<string?> RemoveDataFileAsync(DataFileRow row) { Log.Add("removedata " + row.Name); return Task.FromResult<string?>("closed"); }
        public void OpenFileLocation(DataFileRow row) => Log.Add("open " + row.Path);
        public Task SyncNowAsync(string accountId) { Log.Add("sync " + accountId); return Task.CompletedTask; }
        public Task<string?> ChangeLocationAsync(string accountId, Window owner) => Task.FromResult<string?>(null);
        public void SaveMirrorSettings(string accountId, OpenOutlook.Mirror.MirrorAccountSettings settings) => Saved.Add((accountId, settings));
        public List<OpenOutlook.JunkCleaner.JunkCleanerAccountSettings> JunkSaved = [];
        public IReadOnlyList<OpenOutlook.Auth.ConnectedAccount> JunkAccounts() => Accounts_.Where(x => x.Provider == OpenOutlook.Auth.OAuthProvider.MicrosoftConsumers).ToList();
        public OpenOutlook.JunkCleaner.JunkCleanerAccountSettings JunkSettings(string accountId) =>
            JunkSaved.LastOrDefault(j => j.AccountId == accountId) ?? new OpenOutlook.JunkCleaner.JunkCleanerAccountSettings { AccountId = accountId, Keywords = ["temu"] };
        public void SaveJunkSettings(OpenOutlook.JunkCleaner.JunkCleanerAccountSettings settings) => JunkSaved.Add(settings);
        public Task<string?> CleanJunkNowAsync(string accountId, Window owner) { Log.Add("cleanjunk " + accountId); return Task.FromResult<string?>("Moved 2"); }
        public Task<string?> ImportJunkConfigAsync(string accountId, Window owner) { Log.Add("importjunk " + accountId); return Task.FromResult<string?>(null); }
        public IReadOnlyList<string> JunkLog() => ["2026-10-05 10:00  me@hotmail.test  moved \"win\" from promo@temu.example [From keyword]"];
    }

    [AvaloniaFact]
    public void The_junk_cleaner_tab_edits_settings_and_runs_a_clean()
    {
        var host = new FakeHost();
        var window = new AccountSettingsWindow(host, startOnJunk: true);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            Assert.Equal(2, tabs.SelectedIndex);
            Assert.Contains("Junk Cleaner", tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString()));
            var accounts = window.GetVisualDescendants().OfType<ComboBox>().First();
            Assert.Single(accounts.Items);                                                      // Microsoft accounts only
            var keywords = window.GetVisualDescendants().OfType<TextBox>().First(t => !t.IsReadOnly && t.AcceptsReturn);
            Assert.Equal("temu", keywords.Text);
            var enable = window.GetVisualDescendants().OfType<CheckBox>().First(c => c.Content?.ToString()!.StartsWith("Use the Junk") == true);
            enable.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(host.JunkSaved.Last().Enabled);
            keywords.Text = "temu" + (char)10 + "shein";
            window.GetVisualDescendants().OfType<CheckBox>().First(c => c.Content?.ToString()!.Contains("no To") == true).IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["temu", "shein"], host.JunkSaved.Last().Keywords);
            Assert.True(host.JunkSaved.Last().Rules.DeleteMissingTo);
            var log = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsReadOnly);
            Assert.Contains("temu.example", log.Text);
            window.GetVisualDescendants().OfType<Button>().First(b => Cap(b) == "Clean now…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("cleanjunk acc1", host.Log);
            Shot(window, "16-junk-cleaner-tab");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void The_status_bar_shows_the_state_of_the_mailbox_copies()
    {
        var window = new MainWindow { Width = 1300, Height = 700 };
        Dispatcher.UIThread.RunJobs();
        try
        {
            var indicator = window.FindControl<Button>("SyncIndicator")!;
            Assert.False(indicator.IsVisible);                                                  // no mailbox copy yet
            var account = new OpenOutlook.Auth.ConnectedAccount(OpenOutlook.Auth.OAuthProvider.MicrosoftConsumers, "acc1", "me@hotmail.test", "c", DateTimeOffset.UtcNow, ["Mail.ReadWrite"]);
            CallVoid(window, "SetMirrorInfo", account, SyncPhase.Syncing, "", 0, true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(indicator.IsVisible);
            Assert.Equal("Syncing…", window.FindControl<TextBlock>("SyncIndicatorText")!.Text);
            CallVoid(window, "SetMirrorInfo", account, SyncPhase.Offline, "", 3, true);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Offline · 3 changes waiting", window.FindControl<TextBlock>("SyncIndicatorText")!.Text);
            Assert.Contains("me@hotmail.test", ToolTip.GetTip(indicator)!.ToString());
        }
        finally { window.Close(); }
    }

    /// <summary>The caption of a toolbar button: its text, or the text next to its icon.</summary>
    private static string Cap(Button b) => b.Content is string s ? s : (b.Content as Control)?.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).FirstOrDefault() ?? (b.Content as StackPanel)?.Children.OfType<TextBlock>().Select(t => t.Text).FirstOrDefault() ?? "";

    [AvaloniaFact]
    public async Task The_ribbon_squeezes_its_groups_into_drop_downs_as_the_window_narrows()
    {
        var window = new MainWindow { Width = 2600, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var home = window.RibbonLayouts[0];
            async Task Settle() { for (var i = 0; i < 6; i++) { await Task.Delay(30); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } }
            await Settle();
            // the Home tab is selected: at a wide window nothing is collapsed
            var tabs = window.FindControl<TabControl>("RibbonTabs")!;
            var homeIndex = tabs.Items.OfType<TabItem>().ToList().FindIndex(t => t.Header?.ToString() == "Home");
            var layout = window.RibbonLayouts.First(l => l.GroupCount > 8);
            Assert.True(layout.CollapsedGroups.Count == 0, layout.Describe());
            Shot(window, "13-ribbon-wide");

            window.Width = 1300;
            await Settle();
            Assert.NotEmpty(layout.CollapsedGroups);                                            // the groups on the right are collapsed into buttons
            Assert.True(!layout.CollapsedGroups.Contains("New"), layout.Describe());                              // the first group stays open as long as it can
            Shot(window, "14-ribbon-narrow");

            window.Width = 520;
            await Settle();
            var narrowest = layout.CollapsedGroups.Count;
            Assert.True(narrowest > 3);
            Shot(window, "15-ribbon-narrowest");

            // a collapsed group's drop-down still holds its real buttons
            var button = window.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b)?.ToString() == layout.CollapsedGroups[0]);
            var inner = (StackPanel)button.Content!;
            var glyph = inner.Children[0];
            Assert.True(glyph is ContentControl { Template: not null } && glyph.Bounds.Width > 10 && glyph.Bounds.Height > 10 && glyph.GetVisualDescendants().Count() > 3, $"{glyph.GetType().Name} {glyph.Bounds} visual={string.Join(",", glyph.GetVisualDescendants().Select(v => v.GetType().Name))} visible={glyph.IsVisible} opacity={glyph.Opacity}");
            Assert.NotNull(button.Flyout);
            Assert.IsType<Border>(((Flyout)button.Flyout!).Content);

            window.Width = 2600;
            await Settle();
            Assert.Empty(layout.CollapsedGroups);                                               // widening brings everything back
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => (b.Tag as string) == "reply");     // and the real Reply button is in the ribbon again
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Account_settings_dialog_has_email_and_data_files_tabs_like_outlook()
    {
        var host = new FakeHost();
        var window = new AccountSettingsWindow(host);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Shot(window, "11-account-settings-email");
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            Assert.Equal(["Email", "Data Files", "Junk Cleaner"], tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString()).ToArray());
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
            string TipOf(string caption) => ToolTip.GetTip(buttons.First(b => Cap(b) == caption))?.ToString() ?? "";
            Assert.Equal("To be implemented", TipOf("Change…"));

            // the first account is the default until another is chosen
            var emailList = window.GetVisualDescendants().OfType<ListBox>().First();
            var items = emailList.Items.OfType<ListBoxItem>().ToList();
            Assert.Equal(2, items.Count);
            Assert.Contains(items[0].GetVisualDescendants().OfType<Border>(), b => (b.Tag as string) == "default");        // the drawn check mark
            Assert.Contains(items[0].GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "me@hotmail.test");
            emailList.SelectedItem = items[1];
            Dispatcher.UIThread.RunJobs();
            buttons.First(b => Cap(b) == "Set as Default").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("acc2", host.Default);
            emailList = window.GetVisualDescendants().OfType<ListBox>().First();
            Assert.Contains(emailList.Items.OfType<ListBoxItem>().ElementAt(1).GetVisualDescendants().OfType<Border>(), b => (b.Tag as string) == "default");
            buttons.First(b => Cap(b) == "Repair…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("repair acc2", host.Log);

            // the data files tab lists mailbox copies and opened PST files with Outlook's toolbar
            tabs.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Shot(window, "12-account-settings-data-files");
            buttons = window.GetVisualDescendants().OfType<Button>().ToList();            // the Data Files tab exists now
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains("D:/email/rmarrash_outlook.pst", texts);
            Assert.Contains("C:/mail/me@hotmail.test.pst", texts);
            Assert.Contains("Not available yet", texts);                                           // Gmail copies come later
            Assert.Contains(texts, t => t == "700 MB");
            foreach (var caption in new[] { "Add…", "Add by path…", "Settings…", "Set as Default", "Remove", "Open File Location…" })
                Assert.Contains(buttons, b => Cap(b) == caption);
            Assert.Equal("To be implemented", ToolTip.GetTip(buttons.Last(b => Cap(b) == "Set as Default"))?.ToString());
            var fileList = window.GetVisualDescendants().OfType<ListBox>().Last();
            fileList.SelectedItem = fileList.Items.OfType<ListBoxItem>().Last();
            Dispatcher.UIThread.RunJobs();
            buttons.First(b => Cap(b) == "Open File Location…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("open D:/email/rmarrash_outlook.pst", host.Log);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Data_file_settings_dialog_saves_the_keep_window_and_attachment_limit()
    {
        var host = new FakeHost();
        var row = host.DataFiles()[0];
        var dialog = new DataFileSettingsDialog(host, row);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var months = dialog.GetVisualDescendants().OfType<ComboBox>().First(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Content?.ToString() == "12 months"));
            Assert.Equal("12 months", ((ComboBoxItem)months.SelectedItem!).Content);              // the default keep window
            months.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, host.Saved.Last().S.KeepMonths);
            Assert.True(host.Saved.Last().S.Enabled);
            Assert.Equal(25L << 20, host.Saved.Last().S.MaxAttachmentBytes);                      // the default attachment limit
            dialog.GetVisualDescendants().OfType<Button>().First(b => Cap(b) == "Sync now").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("sync acc1", host.Log);
        }
        finally { dialog.Close(); }
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
