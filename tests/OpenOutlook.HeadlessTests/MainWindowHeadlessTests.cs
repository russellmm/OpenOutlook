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
            var root = tree.Items.OfType<TreeViewItem>().Single(i => i.Tag is OpenOutlook.Auth.ConnectedAccount { Provider: OpenOutlook.Auth.OAuthProvider.Google });
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
}
