using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>
/// The three Reading Pane options that only take effect while reading (single-key reading with the space bar,
/// automatic full-screen reading in portrait orientation, always preview messages) and the archive
/// check-and-repair command.
/// </summary>
public partial class MainWindow
{
    /// <summary>Without "Always preview messages", a message bigger than this is not rendered in the reading pane
    /// when it is selected (rendering a huge body or its attachments' previews would stall the list); it opens on
    /// double-click as before.</summary>
    private const int LargePreviewBytes = 10 * 1024 * 1024;

    private bool _portraitReading;
    private GridLength[]? _savedPaneWidths;

    private bool AnyFullWindowOverlayOpen =>
        BackstageHost.IsVisible || OptionsHost.IsVisible || EditorOptionsHost.IsVisible || ReadingPaneHost.IsVisible;

    /// <summary>The interactive HTML view is a native web control: it is drawn by the operating system above everything Avalonia paints, so the File screen,
    /// Options and their dialogs cannot cover it. It is hidden while any of them is open and comes back when the last one closes.</summary>
    private void InitializeWebViewOverlayGuard()
    {
        void Apply()
        {
            if (!_webViewAvailable) return;
            var show = _embeddedHtmlActive && !AnyFullWindowOverlayOpen;
            if (MainHtmlWebView.IsVisible != show) MainHtmlWebView.IsVisible = show;
        }
        foreach (var host in new Control[] { BackstageHost, OptionsHost, EditorOptionsHost, ReadingPaneHost })
            host.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) Apply(); };
        MainHtmlWebView.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty && MainHtmlWebView.IsVisible && AnyFullWindowOverlayOpen) MainHtmlWebView.IsVisible = false;
        };
    }

    private void InitializeReadingOptions()
    {
        InitializeWebViewOverlayGuard();
        InitMessageDrag();                                       // drag and right-click work in every kind of folder, not only after an archive folder loaded
        // Tunnel: the grid itself would otherwise consume Space (it toggles row selection).
        MessageList.AddHandler(KeyDownEvent, MessageListKeyDownTunnel, RoutingStrategies.Tunnel);
        MessageList.AddHandler(TappedEvent, MessageListTappedForPortrait, RoutingStrategies.Bubble, handledEventsToo: true);
        MessageList.SelectionChanged += (_, _) => { if (MessageList.SelectedItem is null) ExitPortraitReading(); };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || !_portraitReading || OptionsHost.IsVisible || BackstageHost.IsVisible) return;
            ExitPortraitReading();
            e.Handled = true;
        }, RoutingStrategies.Bubble);
        SizeChanged += (_, _) => { if (_portraitReading && !IsPortrait) ExitPortraitReading(); };
    }

    // ---- single key reading (space bar) ----

    private void MessageListKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_options.ReadPaneSingleKeyReading) return;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) return;
        if (MessageList.SelectedItem is null) return;
        e.Handled = true;
        _ = SingleKeyReadAsync(up: (e.KeyModifiers & KeyModifiers.Shift) != 0);
    }

    /// <summary>Space scrolls the open message down a page (Shift+Space up); at the end it moves on to the next
    /// message (previous at the top), like Outlook.</summary>
    private async Task SingleKeyReadAsync(bool up)
    {
        try
        {
            if (MainHtmlWebView.IsVisible)
            {
                var direction = up ? -1 : 1;
                var result = await MainHtmlWebView.InvokeScript(
                    "(function(){var d=document.scrollingElement||document.documentElement;" +
                    $"var m=d.scrollHeight-window.innerHeight;if({(up ? "d.scrollTop<=0" : "d.scrollTop>=m-1")})return 'edge';" +
                    $"window.scrollBy(0,{direction}*window.innerHeight*0.9);return 'ok';}})()");
                if (result is null || !result.Contains("edge", StringComparison.Ordinal)) return;
            }
            else if (ReaderScrollViewer.IsVisible)
            {
                var sv = ReaderScrollViewer;
                var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                var page = Math.Max(40, sv.Viewport.Height * 0.9);
                if (!up && sv.Offset.Y < max - 1)
                { sv.Offset = new Vector(sv.Offset.X, Math.Min(max, sv.Offset.Y + page)); return; }
                if (up && sv.Offset.Y > 0)
                { sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y - page)); return; }
            }
            SelectNeighbourMessage(up ? -1 : 1);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        { AppLog.Error("reading-pane", ex, "single-key reading failed"); }
    }

    private void SelectNeighbourMessage(int step)
    {
        if (MessageList.CollectionView is not { } view) return;
        var rows = view.Cast<object>().Where(o => o is MessageListRow or GraphMessageListRow).ToList();
        var index = rows.IndexOf(MessageList.SelectedItem!);
        if (index < 0 || index + step < 0 || index + step >= rows.Count) return;
        MessageList.SelectedItem = rows[index + step];
        MessageList.ScrollIntoView(rows[index + step], null);
        if (ReaderScrollViewer.IsVisible)
            ReaderScrollViewer.Offset = step < 0
                ? new Vector(0, Math.Max(0, ReaderScrollViewer.Extent.Height - ReaderScrollViewer.Viewport.Height))
                : new Vector(0, 0);
    }

    // ---- automatic full-screen reading in portrait orientation ----

    private bool IsPortrait => Bounds.Width > 0 && Bounds.Height > Bounds.Width;

    private void MessageListTappedForPortrait(object? sender, TappedEventArgs e)
    {
        if (!_options.ReadPaneFullScreenPortrait || !IsPortrait || MessageList.SelectedItem is null) return;
        var row = e.Source as DataGridRow ?? (e.Source as Visual)?.FindAncestorOfType<DataGridRow>();
        if (row is not null) EnterPortraitReading();
    }

    private void EnterPortraitReading()
    {
        if (_portraitReading || !_options.ReadPaneFullScreenPortrait || !IsPortrait) return;
        var columns = PaneGrid.ColumnDefinitions;
        _savedPaneWidths = columns.Select(c => c.Width).ToArray();
        for (var i = 0; i < 4; i++) columns[i].Width = new GridLength(0);
        columns[4].Width = new GridLength(1, GridUnitType.Star);
        _portraitReading = true;
        ReaderBackButton.IsVisible = true;
    }

    private void ExitPortraitReading()
    {
        if (!_portraitReading) return;
        _portraitReading = false;
        ReaderBackButton.IsVisible = false;
        if (_savedPaneWidths is { } saved)
            for (var i = 0; i < saved.Length && i < PaneGrid.ColumnDefinitions.Count; i++)
                PaneGrid.ColumnDefinitions[i].Width = saved[i];
        _savedPaneWidths = null;
        MessageList.Focus();
    }

    private void ReaderBackClicked(object? sender, RoutedEventArgs e) => ExitPortraitReading();

    // ---- always preview messages ----

    private bool PreviewBlocked(MailSummary summary) =>
        !_options.ReadPaneAlwaysPreview && summary.Size > LargePreviewBytes;

    private void ShowPreviewBlocked(MailSummary summary)
    {
        ClearReader();
        MainHtmlWebView.IsVisible = false;
        HtmlPagesPanel.IsVisible = false;
        SubjectText.Text = summary.Subject;
        SenderText.Text = summary.From;
        BodyText.Text = $"This message is large ({summary.Size / (1024.0 * 1024.0):0.#} MB), so it is not previewed here. " +
                        "Double-click it to open it, or turn on \"Always preview messages\" in View > Reading Pane options.";
        StatusText.Text = "Large message not previewed.";
    }

    // ---- check and repair an archive ----

    private async void CheckArchiveClicked(object? sender, RoutedEventArgs e)
    {
        // Every open data file can be checked, the mailbox copies behind the accounts included (they are not in the folder list, but they are
        // the files this computer writes most). The one you are looking at is chosen first.
        var files = _stores.Select(entry => new ArchiveRepairWindow.DataFile(entry.Key,
                IsMirrorCopy(entry.Key)
                    ? MirrorAccounts().FirstOrDefault(a => MirrorPathFor(a) == entry.Key)?.DisplayAddress + " (mailbox copy)"
                    : entry.Value.DisplayName,
                entry.Value.CanWrite))
            .OrderBy(f => IsMirrorCopy(f.Path) ? 0 : 1).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (files.Count == 0)
        {
            StatusText.Text = "Open a data file (.pst) or connect an account first.";
            return;
        }
        var activeAccount = _activeMicrosoftAccount ?? _activeGmailFolder?.Account;
        var first = _activePath ?? (activeAccount is null ? null : MirrorPathFor(activeAccount));
        CloseBackstage();
        CloseOptionsDialog();
        async Task<PstScanReport> Run(string path, Func<IPstEngine, PstScanReport> work)
        {
            if (!_stores.TryGetValue(path, out var store)) throw new InvalidOperationException("That data file is no longer open.");
            await _readerGate.WaitAsync();
            try { return await Task.Run(() => work(store)); }
            finally { _readerGate.Release(); }
        }
        var window = new ArchiveRepairWindow(files, files.FirstOrDefault(f => f.Path == first) ?? files[0],
            path => Run(path, store => store.Scan()),
            async path =>
            {
                var mirror = IsMirrorCopy(path);
                if (mirror && _mirrorBusy) throw new InvalidOperationException("This mailbox copy is being updated. Try again when it has finished.");
                if (mirror) _mirrorBusy = true;                                      // no sync writes to the copy while it is repaired
                try
                {
                    var report = await Run(path, store => store.Repair());
                    InvalidateFolderCache(path);
                    if (_activePath == path) await RefreshActivePstFolderAsync(path);
                    return report;
                }
                finally { if (mirror) _mirrorBusy = false; }
            });
        await window.ShowDialog(this);
    }
}

/// <summary>Scan results for one archive, with a Repair button for what the built-in repair can fix.</summary>
internal sealed class ArchiveRepairWindow : Window
{
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        FontFamily = new Avalonia.Media.FontFamily("Consolas,Menlo,monospace"), FontSize = 12.5,
        AcceptsReturn = true
    };
    private readonly Button _scan = new() { Content = "Scan again", MinWidth = 96 };
    private readonly Button _repair = new() { Content = "Repair", MinWidth = 96, IsEnabled = false };
    private readonly Button _close = new() { Content = "Close", MinWidth = 96 };
    private readonly ComboBox _files = new() { MinWidth = 360, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly Func<string, Task<PstScanReport>> _scanFn;
    private readonly Func<string, Task<PstScanReport>> _repairFn;

    /// <summary>One open data file: an archive, or the mailbox copy behind a connected account.</summary>
    public sealed record DataFile(string Path, string Name, bool CanRepair)
    {
        public override string ToString() => Name;
    }

    private DataFile Current => (DataFile)_files.SelectedItem!;

    public ArchiveRepairWindow(IReadOnlyList<DataFile> files, DataFile first, Func<string, Task<PstScanReport>> scan, Func<string, Task<PstScanReport>> repair)
    {
        _scanFn = scan; _repairFn = repair;
        Title = "Check and Repair";
        Width = 720; Height = 520; MinWidth = 480; MinHeight = 320;
        foreach (var file in files) _files.Items.Add(file);
        _files.SelectedItem = first;
        ToolTip.SetTip(_files, first.Path);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0),
            Children = { _scan, _repair, _close }
        };
        var chooser = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var label = new TextBlock { Text = "Data file:", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(label, Dock.Left);
        chooser.Children.Add(label);
        chooser.Children.Add(_files);
        var panel = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(chooser, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(chooser);
        panel.Children.Add(buttons);
        panel.Children.Add(_log);
        Content = panel;
        _scan.Click += async (_, _) => await RunAsync(_scanFn, repaired: false);
        _repair.Click += async (_, _) => await RunAsync(_repairFn, repaired: true);
        _close.Click += (_, _) => Close();
        _files.SelectionChanged += async (_, _) =>
        {
            if (_files.SelectedItem is not DataFile file) return;
            ToolTip.SetTip(_files, file.Path);
            if (IsVisible) await RunAsync(_scanFn, repaired: false);
        };
        Opened += async (_, _) => await RunAsync(_scanFn, repaired: false);
    }

    private async Task RunAsync(Func<string, Task<PstScanReport>> work, bool repaired)
    {
        var file = Current;
        _scan.IsEnabled = _repair.IsEnabled = _files.IsEnabled = false;
        _log.Text = (repaired ? "Repairing " : "Scanning ") + file.Name + "...";
        try
        {
            var report = await work(file.Path);
            _log.Text = Summarize(report, repaired, file);
            _repair.IsEnabled = file.CanRepair && report.Fixable > 0;
        }
        catch (Exception ex) when (ex is PstException or IOException or InvalidOperationException or ObjectDisposedException)
        { _log.Text = $"The operation failed: {ex.Message}"; }
        finally { _scan.IsEnabled = _files.IsEnabled = true; }
    }

    /// <summary>
    /// The report shown in the window. The checker's text mixes indented problem lines with one summary line per check ("refs: ... 0 problems"); only
    /// the indented lines are problems. The window used to count every line ("11 problem lines found" for one problem, 2026-10-16), so the problems
    /// are counted and listed first now, with the per-check summary below them.
    /// </summary>
    internal static string Summarize(PstScanReport r, bool repaired, DataFile file)
    {
        var lines = new List<string> { file.Path, "" };
        if (repaired) lines.Add($"Repaired {r.Fixed} item{(r.Fixed == 1 ? "" : "s")}.");
        var problems = r.Findings.Where(l => l.StartsWith("  ", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();
        var checks = r.Findings.Where(l => !l.StartsWith("  ", StringComparison.Ordinal)).ToList();
        if (problems.Count == 0 && r.Findings.Count > 0) { problems = r.Findings.ToList(); checks = []; }   // a report without the indented format
        var fixable = r.FixableItems ?? [];
        if (problems.Count == 0 && r.Fixable == 0) lines.Add("No problems found. The archive is structurally sound.");
        else if (problems.Count == 0)
        {
            // the repair's rules found what the checker does not list (SCANPST: "Only minor inconsistencies were found")
            lines.Add($"No structural problems found, but {r.Fixable} minor inconsistenc{(r.Fixable == 1 ? "y" : "ies")} " +
                      "(the kind SCANPST reports as minor) can be repaired automatically.");
            if (!file.CanRepair) lines.Add("This archive is open read-only, so it cannot be repaired.");
            if (fixable.Count > 0) { lines.Add(""); lines.AddRange(fixable.Select(l => "- " + l)); }
        }
        else
        {
            lines.Add($"{problems.Count} problem{(problems.Count == 1 ? "" : "s")} found; " +
                      (r.Fixable > 0 ? $"{r.Fixable} item{(r.Fixable == 1 ? "" : "s")} can be repaired automatically." : "nothing can be repaired automatically."));
            if (r.Fixable == 0)
                lines.Add(OperatingSystem.IsWindows()
                    ? "Make a copy of the file and run Microsoft's Inbox Repair Tool (SCANPST.EXE) on the copy."
                    : "Copy the file to a Windows PC and run Microsoft's Inbox Repair Tool (SCANPST.EXE) on the copy.");
            else if (!file.CanRepair)
                lines.Add("This archive is open read-only, so it cannot be repaired.");
            lines.Add("");
            lines.AddRange(problems.Take(500).Select(l => "- " + l));
            if (problems.Count > 500) lines.Add($"... and {problems.Count - 500} more.");
            if (fixable.Count > 0)
            {
                lines.Add("");
                lines.Add("The repair fixes:");
                lines.AddRange(fixable.Select(l => "- " + l));
            }
            if (checks.Count > 0)
            {
                lines.Add("");
                lines.Add("Checks run:");
                lines.AddRange(checks);
            }
        }
        return string.Join(Environment.NewLine, lines);
    }
}
