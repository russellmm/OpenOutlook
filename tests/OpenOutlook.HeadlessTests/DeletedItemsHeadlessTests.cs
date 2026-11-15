using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenOutlook.Desktop;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.HeadlessTests;

/// <summary>A Microsoft Deleted Items folder: the "more items on the server" bar, and Delete that asks at once and sends batches in the background.</summary>
public sealed class DeletedItemsHeadlessTests
{
    private static async Task WaitUntil(Func<bool> condition, int ms = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(25); }
        Dispatcher.UIThread.RunJobs();
    }

    private static FieldInfo F(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>A Graph with 250 messages in Deleted Items (ids d0..d249, newest first); records what the program asks.</summary>
    private sealed class FakeDeletedItems : HttpMessageHandler
    {
        public const int Total = 250;
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Requests = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> PermanentlyDeleted = new();

        private static string MessageJson(int i) =>
            $$"""{"id":"d{{i}}","subject":"Subject d{{i}}","receivedDateTime":"2026-10-01T12:00:00Z","hasAttachments":false,"isRead":true,"bodyPreview":"","isDraft":false}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            Requests.Enqueue(request.Method + " " + path + query);
            string json = "{}";
            if (path == "/v1.0/me") json = """{"id":"headless-trash"}""";
            else if (path is "/v1.0/me/mailFolders/deleteditems" or "/v1.0/me/mailFolders/TRASH") json = $$"""{"id":"TRASH","totalItemCount":{{Total}},"unreadItemCount":0}""";
            else if (path == "/v1.0/me/mailFolders/TRASH/messages")
            {
                var skip = int.Parse(System.Text.RegularExpressions.Regex.Match(query, @"skip=(\d+)").Groups[1].Value);
                var top = int.Parse(System.Text.RegularExpressions.Regex.Match(query, @"top=(\d+)").Groups[1].Value);
                json = "{\"value\":[" + string.Join(",", Enumerable.Range(skip, Math.Max(0, Math.Min(top, Total - skip))).Select(MessageJson)) + "]}";
            }
            else if (path == "/v1.0/$batch")
            {
                using var doc = JsonDocument.Parse(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
                var responses = new List<string>();
                foreach (var part in doc.RootElement.GetProperty("requests").EnumerateArray())
                {
                    var url = part.GetProperty("url").GetString()!;
                    var id = Uri.UnescapeDataString(url.Split("/messages/")[1].Split('/', '?')[0]);
                    var rid = part.GetProperty("id").GetString();
                    if (part.GetProperty("method").GetString() == "GET")
                        responses.Add($$$"""{"id":"{{{rid}}}","status":200,"body":{"id":"{{{id}}}","parentFolderId":"TRASH"}}""");
                    else
                    {
                        PermanentlyDeleted.Enqueue(id);
                        responses.Add($$"""{"id":"{{rid}}","status":204,"body":null}""");
                    }
                }
                json = "{\"responses\":[" + string.Join(",", responses) + "]}";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json"), RequestMessage = request });
        }
    }

    private static (MainWindow Window, FakeDeletedItems Graph, object Folder, System.Collections.ObjectModel.ObservableCollection<GraphMessageListRow> Rows) Open()
    {
        new OpenOutlook.Auth.ConnectedAccountRegistry().Save([]);
        var window = new MainWindow { Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var account = new OpenOutlook.Auth.ConnectedAccount(OpenOutlook.Auth.OAuthProvider.MicrosoftConsumers, "headless-trash", "person@hotmail.test", "client",
            DateTimeOffset.UtcNow, ["Mail.ReadWrite", "Mail.Send"]);
        var graph = new FakeDeletedItems();
        F("_graphHttp").SetValue(window, new HttpClient(graph));
        F("_activeMicrosoftAccount").SetValue(window, account);
        var folder = Activator.CreateInstance(typeof(MainWindow).GetNestedType("MicrosoftFolderSelection", BindingFlags.NonPublic)!, account, "TRASH", "Deleted Items")!;
        F("_activeMicrosoftFolder").SetValue(window, folder);
        var session = new MicrosoftMailSession(account, new OpenOutlook.Auth.UnavailableSecretStore(), new HttpClient());
        typeof(MicrosoftMailSession).GetField("_accessToken", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, "headless-token");
        typeof(MicrosoftMailSession).GetField("_expiresAt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, DateTimeOffset.UtcNow.AddHours(1));
        ((System.Collections.IDictionary)F("_microsoftSessions").GetValue(window)!)[account.AccountId] = session;
        // the list as the local copy would show it: the 150 newest
        var rows = new System.Collections.ObjectModel.ObservableCollection<GraphMessageListRow>(Enumerable.Range(0, 150).Select(i =>
            new GraphMessageListRow(new GraphInboxMessage("d" + i, "Subject d" + i, "Sender", "person@hotmail.test", DateTimeOffset.UtcNow, 100, false, true, ""))));
        F("_graphRows").SetValue(window, rows);
        window.FindControl<DataGrid>("MessageList")!.ItemsSource = new Avalonia.Collections.DataGridCollectionView(rows);
        return (window, graph, folder, rows);
    }

    [AvaloniaFact]
    public async Task More_items_on_the_server_are_offered_and_fetched_on_a_click()
    {
        var (window, graph, folder, rows) = Open();
        try
        {
            var bar = window.FindControl<Border>("MoreOnServerBar")!;
            Assert.False(bar.IsVisible);
            await (Task)typeof(MainWindow).GetMethod("UpdateMoreOnServerAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [folder, (long)F("_folderVersion").GetValue(window)!, CancellationToken.None])!;
            Assert.True(bar.IsVisible, "250 on the server, 150 in the list");

            window.FindControl<Button>("MoreOnServerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => rows.Count == 250);
            Assert.Equal(250, rows.Count);
            Assert.Equal(250, rows.Select(r => r.Message.Id).Distinct().Count());
            Assert.Contains(graph.Requests, r => r.Contains("skip=150") && r.Contains("top=100"));
            await WaitUntil(() => !bar.IsVisible);
            Assert.False(bar.IsVisible, "everything is in the list now");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Delete_in_Deleted_Items_asks_at_once_then_deletes_in_batches_in_the_background()
    {
        var (window, graph, _, rows) = Open();
        try
        {
            var list = window.FindControl<DataGrid>("MessageList")!;
            list.SelectedItems.Add(rows[0]);
            list.SelectedItems.Add(rows[1]);
            list.SelectedItems.Add(rows[2]);
            await Task.Delay(300);                                              // the reading pane's own requests settle
            Dispatcher.UIThread.RunJobs();
            while (graph.Requests.TryDequeue(out _)) { }

            var delete = window.ExecuteMailActionAsync("delete");
            await WaitUntil(() => window.OwnedWindows.Count > 0, 3000);
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.Empty(graph.Requests);                                       // the question came up without asking Microsoft anything first
            Assert.Equal(150, rows.Count);                                      // and nothing was removed before the answer

            var confirm = FindButton(dialog, "Delete permanently");
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await delete;
            Assert.Equal(147, rows.Count);                                      // the rows leave the list as soon as it is confirmed
            await WaitUntil(() => graph.PermanentlyDeleted.Count == 3);
            Assert.Equal(["d0", "d1", "d2"], graph.PermanentlyDeleted.OrderBy(x => x).ToArray());
            Assert.Equal(2, graph.Requests.Count(r => r.StartsWith("POST /v1.0/$batch")));                // one batched check, one batched delete, not six calls
        }
        finally { window.Close(); }
    }

    private static Button FindButton(Window window, string text) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Button>().First(b => b.Content as string == text);
}
