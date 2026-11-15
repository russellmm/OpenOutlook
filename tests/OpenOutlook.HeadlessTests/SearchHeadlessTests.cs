using System.Reflection;
using Xunit;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpenOutlook.Desktop;
using OpenOutlook.PstNative;
using PstCore;

namespace OpenOutlook.HeadlessTests;

/// <summary>The title-bar search box: every data file, Deleted Items included, with a scope picker (no private fixture: the files are created here).</summary>
public sealed class SearchHeadlessTests
{
    private static async Task WaitUntil(Func<bool> condition, int ms = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(25); }
        Dispatcher.UIThread.RunJobs();
    }

    private static T Call<T>(object target, string name, params object?[] args) =>
        (T)target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, args)!;

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static MailImport Mail(string subject, string body) => new()
    {
        Subject = subject, SenderName = "Ann", SenderEmail = "ann@example.org", BodyText = body, BodyHtml = "<p>" + body + "</p>",
        Sent = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), Received = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
        Recipients = { new ImportRecipient("Bob", "bob@example.org", RecipientKind.To) }
    };

    private static string MakePst(string dir, string name, params (string Folder, string Subject, string Body)[] messages)
    {
        var path = Path.Combine(dir, name + ".pst");
        using var e = PstEngineFactory.CreateDataFile(path, name);
        var top = e.Root.Children.Single(c => c.Name == "Top of Outlook data file");
        foreach (var group in messages.GroupBy(m => m.Folder))
        {
            var folder = group.Key == "Deleted Items" ? e.DeletedItemsFolder()! : e.CreateFolder(top.Nid, group.Key);
            e.ImportMessages(folder, group.Select(m => Mail(m.Subject, m.Body)).ToList());
        }
        return path;
    }

    [AvaloniaFact]
    public async Task Search_covers_every_data_file_and_deleted_items_and_narrows_with_the_scope()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oo-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var a = MakePst(dir, "Alpha", ("Projects", "Quarterly zebra report", "numbers"), ("Projects", "Lunch", "pasta"), ("Deleted Items", "Old zebra invoice", "thrown away"));
        var b = MakePst(dir, "Beta", ("Filed", "Zebra contract", "signed"), ("Filed", "Holiday", "beach with a zebra in the body"));
        new OpenOutlook.Auth.ConnectedAccountRegistry().Save([]);
        var window = new MainWindow { Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(await Call<Task<bool>>(window, "OpenArchiveAsync", a, false));
        Assert.True(await Call<Task<bool>>(window, "OpenArchiveAsync", b, false));
        var box = window.FindControl<TextBox>("ArchiveSearchBox")!;
        var scope = window.FindControl<ComboBox>("SearchScopeBox")!;
        var list = window.FindControl<DataGrid>("MessageList")!;
        int Shown() => (list.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Count() ?? 0;
        async Task Search(string text)
        {
            box.Text = text;
            Call<object?>(window, "SearchClicked", null, new Avalonia.Interactivity.RoutedEventArgs());
            await WaitUntil(() => window.FindControl<TextBlock>("StatusText")!.Text?.Contains("match", StringComparison.OrdinalIgnoreCase) == true);
        }

        // all data files: subject hits in both files, the one in Deleted Items, and the body-only hit
        scope.SelectedIndex = 0;
        await Search("zebra");
        Assert.Equal(4, Shown());
        Assert.True(list.Columns.Last().IsVisible, "the In Folder column shows for search results");
        var places = (list.ItemsSource as System.Collections.IEnumerable)!.Cast<MessageListRow>().Select(r => r.FolderDisplay).ToList();
        Assert.Contains("Alpha › Deleted Items", places);
        Assert.Contains("Beta › Filed", places);

        // a hit from the second file opens with that file's own store
        var row = (list.ItemsSource as System.Collections.IEnumerable)!.Cast<MessageListRow>().First(r => r.StorePath == b);
        list.SelectedItem = row;
        await WaitUntil(() => Field<string?>(window, "_activePath") == b);
        Assert.Equal(b, Field<string?>(window, "_activePath"));

        // fielded search from the engine's syntax
        await Search("subject:lunch");
        Assert.Equal(1, Shown());
        await Search("nothing-matches-this");
        Assert.Equal(0, Shown());

        // current mailbox: open Alpha's folder, then search only Alpha
        var (node, _) = TransferHeadlessTests_FolderNode(window, a, "Projects");
        window.FindControl<TreeView>("FolderTree")!.SelectedItem = node;
        await WaitUntil(() => Shown() == 2);
        scope.SelectedIndex = 1;
        await Search("zebra");
        Assert.Equal(2, Shown());                       // Quarterly zebra report + Old zebra invoice (Deleted Items), none from Beta
        // current folder: Projects only (no subfolders): Deleted Items is not part of it
        window.FindControl<TreeView>("FolderTree")!.SelectedItem = node;
        await WaitUntil(() => Shown() == 2);
        scope.SelectedIndex = 2;
        await Search("zebra");
        Assert.Equal(1, Shown());

        window.Close();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static (TreeViewItem Node, MailFolder Folder) TransferHeadlessTests_FolderNode(MainWindow window, string path, string name)
    {
        foreach (var item in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window.FindControl<TreeView>("FolderTree")!).OfType<TreeViewItem>())
        {
            if (item.Tag?.GetType().Name != "FolderSelection") continue;
            var tag = item.Tag;
            if ((string)tag.GetType().GetProperty("Path")!.GetValue(tag)! != path) continue;
            var folder = (MailFolder)tag.GetType().GetProperty("Folder")!.GetValue(tag)!;
            if (folder.Name == name) return (item, folder);
        }
        throw new InvalidOperationException("folder node not found");
    }
}
