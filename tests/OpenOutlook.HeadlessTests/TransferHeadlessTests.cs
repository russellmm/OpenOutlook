using System.Reflection;
using Xunit;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenOutlook.Desktop;
using OpenOutlook.PstNative;
using PstCore;

namespace OpenOutlook.HeadlessTests;

/// <summary>Copy and move between two data files through the window's own picker tree and transfer code (no private fixture: both files are created here).</summary>
public sealed class TransferHeadlessTests
{
    private static async Task WaitUntil(Func<bool> condition, int ms = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(25); }
        Dispatcher.UIThread.RunJobs();
    }

    private static T Call<T>(object target, string name, params object?[] args) =>
        (T)target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, args)!;

    private static string MakePst(string dir, string name, string folder, int messages)
    {
        var path = Path.Combine(dir, name + ".pst");
        using var e = PstEngineFactory.CreateDataFile(path, name);
        var f = e.CreateFolder(e.Root.Children.Single(c => c.Name == "Top of Outlook data file").Nid, folder);
        if (messages > 0)
            e.ImportMessages(f, Enumerable.Range(1, messages).Select(i => new MailImport
            {
                Subject = $"{name} message {i}", SenderName = "Ann", SenderEmail = "ann@example.org", BodyText = "Body " + i,
                BodyHtml = "<p>Body " + i + "</p>", Sent = new DateTime(2026, 10, 1, 12, i, 0, DateTimeKind.Utc), Received = new DateTime(2026, 10, 1, 12, i, 0, DateTimeKind.Utc),
                Recipients = { new ImportRecipient("Bob", "bob@example.org", RecipientKind.To) },
                Attachments = { new ImportAttachment("note.txt", "text/plain", "", System.Text.Encoding.UTF8.GetBytes("attachment " + i)) }
            }).ToList());
        return path;
    }

    private static (TreeViewItem Node, MailFolder Folder)? FolderNode(TreeView tree, string path, string name)
    {
        foreach (var item in tree.GetLogicalDescendants().OfType<TreeViewItem>())
        {
            if (item.Tag?.GetType().Name != "FolderSelection") continue;
            var tag = item.Tag;
            if ((string)tag.GetType().GetProperty("Path")!.GetValue(tag)! != path) continue;
            var folder = (MailFolder)tag.GetType().GetProperty("Folder")!.GetValue(tag)!;
            if (folder.Name == name) return (item, folder);
        }
        return null;
    }

    private static FolderPickItem? FindTarget(IEnumerable<FolderPickItem> items, Func<object, bool> match)
    {
        foreach (var item in items)
        {
            if (item.Tag is not null && match(item.Tag)) return item;
            if (FindTarget(item.Children, match) is { } deeper) return deeper;
        }
        return null;
    }

    private static async Task<(MainWindow Window, string A, string B, string Dir)> OpenTwoAsync(int messagesInA)
    {
        var dir = Path.Combine(Path.GetTempPath(), "oo-xfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var a = MakePst(dir, "Alpha", "Inbox2", messagesInA);
        var b = MakePst(dir, "Beta", "Filed", 0);
        var registry = new OpenOutlook.Auth.ConnectedAccountRegistry();
        registry.Save([]);
        var window = new MainWindow { Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(await Call<Task<bool>>(window, "OpenArchiveAsync", a, false));
        Assert.True(await Call<Task<bool>>(window, "OpenArchiveAsync", b, false));
        return (window, a, b, dir);
    }

    private static async Task SelectAllInAsync(MainWindow window, string path, string folderName)
    {
        var tree = window.FindControl<TreeView>("FolderTree")!;
        var (node, _) = FolderNode(tree, path, folderName) ?? throw new InvalidOperationException("folder not in the tree");
        tree.SelectedItem = node;
        var list = window.FindControl<DataGrid>("MessageList")!;
        await WaitUntil(() => list.CollectionView?.Cast<object>().Any() == true);
        list.SelectedItems.Clear();
        foreach (var row in list.CollectionView!.Cast<object>().Where(o => o.GetType().Name == "MessageListRow")) list.SelectedItems.Add(row);
    }

    /// <summary>The window's own open store for a file (a second open of a file the window holds for writing would fight over its lock).</summary>
    private static IPstEngine Store(MainWindow window, string path) =>
        ((Dictionary<string, IPstEngine>)typeof(MainWindow).GetField("_stores", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!)[path];

    private static int Count(MainWindow window, string path, string folder)
    {
        var e = Store(window, path);
        return e.GetMessages(e.AllFolders().Single(f => f.Name == folder)).Count;
    }

    [AvaloniaFact]
    public async Task The_picker_lists_every_open_data_file_and_the_current_folder_cannot_be_chosen()
    {
        if (!OpenPst.NativeLibraryLoader.IsAvailable) return;
        var (window, a, b, dir) = await OpenTwoAsync(3);
        try
        {
            await SelectAllInAsync(window, a, "Inbox2");
            var origin = Call<object>(window, "CurrentTransferOrigin")!;
            var roots = Call<List<FolderPickItem>>(window, "BuildTransferRoots", origin);
            Assert.Equal(["Alpha", "Beta"], roots.Select(r => r.Name).OrderBy(n => n).ToArray());
            Assert.All(roots, r => Assert.False(r.Selectable));
            var here = FindTarget(roots, t => t.GetType().Name == "PstTarget" && (string)t.GetType().GetProperty("Path")!.GetValue(t)! == a &&
                ((MailFolder)t.GetType().GetProperty("Folder")!.GetValue(t)!).Name == "Inbox2")!;
            Assert.False(here.Selectable);
            var there = FindTarget(roots, t => t.GetType().Name == "PstTarget" && (string)t.GetType().GetProperty("Path")!.GetValue(t)! == b &&
                ((MailFolder)t.GetType().GetProperty("Folder")!.GetValue(t)!).Name == "Filed")!;
            Assert.True(there.Selectable);
        }
        finally { window.Close(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [AvaloniaFact]
    public async Task Copy_then_move_between_two_data_files_keep_everything_and_both_files_stay_clean()
    {
        if (!OpenPst.NativeLibraryLoader.IsAvailable) return;
        var (window, a, b, dir) = await OpenTwoAsync(3);
        try
        {
            await SelectAllInAsync(window, a, "Inbox2");
            var origin = Call<object>(window, "CurrentTransferOrigin")!;
            var roots = Call<List<FolderPickItem>>(window, "BuildTransferRoots", origin);
            var target = FindTarget(roots, t => t.GetType().Name == "PstTarget" && (string)t.GetType().GetProperty("Path")!.GetValue(t)! == b &&
                ((MailFolder)t.GetType().GetProperty("Folder")!.GetValue(t)!).Name == "Filed")!.Tag!;

            await Call<Task>(window, "ExecuteTransferAsync", origin, target, true);       // copy
            var status = window.FindControl<TextBlock>("StatusText")!;
            Assert.Contains("Copied 3 messages", status.Text);
            Assert.Equal(3, Count(window, a, "Inbox2"));
            Assert.Equal(3, Count(window, b, "Filed"));

            await SelectAllInAsync(window, a, "Inbox2");
            origin = Call<object>(window, "CurrentTransferOrigin")!;
            await Call<Task>(window, "ExecuteTransferAsync", origin, target, false);      // move
            Assert.Contains("Moved 3 messages", status.Text);
            Assert.Equal(0, Count(window, a, "Inbox2"));
            Assert.Equal(6, Count(window, b, "Filed"));

            var dest = Store(window, b);
            Assert.Empty(dest.Scan().Findings);
            var first = dest.GetMessages(dest.AllFolders().Single(f => f.Name == "Filed")).First(m => m.Subject == "Alpha message 2");
            var opened = dest.OpenMessage(first);
            Assert.Equal("attachment 2", System.Text.Encoding.UTF8.GetString(dest.ReadAttachmentData(first, opened.Attachments.Single())));
            Assert.Empty(Store(window, a).Scan().Findings);
        }
        finally { window.Close(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [AvaloniaFact]
    public async Task Dropping_messages_of_one_data_file_on_another_is_left_to_the_engines_own_copy()
    {
        if (!OpenPst.NativeLibraryLoader.IsAvailable) return;
        var (window, a, b, dir) = await OpenTwoAsync(2);
        try
        {
            await SelectAllInAsync(window, a, "Inbox2");
            var tree = window.FindControl<TreeView>("FolderTree")!;
            var (_, srcFolder) = FolderNode(tree, a, "Inbox2")!.Value;
            var (destNode, _) = FolderNode(tree, b, "Filed")!.Value;
            var nids = Store(window, a).GetMessages(srcFolder).Select(m => m.Nid.ToString("x8"));
            var data = new Avalonia.Input.DataObject();
            data.Set(Avalonia.Input.DataFormats.Text, "OO_PST_MOVE\n" + a + "\n" + srcFolder.Nid.ToString("x8") + "\n" + string.Join("\n", nids) + "\n");
            Assert.False(Call<bool>(window, "IsCrossStoreDrop", data, destNode.Tag));
            Assert.False(Call<bool>(window, "IsCrossStoreDrop", new Avalonia.Input.DataObject(), destNode.Tag));      // not our payload at all
        }
        finally { window.Close(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
