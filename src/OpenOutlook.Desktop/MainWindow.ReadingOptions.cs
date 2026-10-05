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
        if (_activePath is not { } path || !_stores.TryGetValue(path, out var store))
        {
            StatusText.Text = "Select a folder in an open archive (.pst) first.";
            return;
        }
        CloseBackstage();
        CloseOptionsDialog();
        async Task<PstScanReport> Run(Func<PstScanReport> work)
        {
            await _readerGate.WaitAsync();
            try { return await Task.Run(work); }
            finally { _readerGate.Release(); }
        }
        var window = new ArchiveRepairWindow(Path.GetFileName(path), store.CanWrite,
            () => Run(store.Scan),
            async () =>
            {
                var report = await Run(store.Repair);
                InvalidateFolderCache(path);
                await RefreshActivePstFolderAsync(path);
                return report;
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
    private readonly Func<Task<PstScanReport>> _scanFn;
    private readonly Func<Task<PstScanReport>> _repairFn;
    private readonly bool _canRepair;

    public ArchiveRepairWindow(string archiveName, bool canRepair, Func<Task<PstScanReport>> scan, Func<Task<PstScanReport>> repair)
    {
        _scanFn = scan; _repairFn = repair; _canRepair = canRepair;
        Title = $"Check and Repair - {archiveName}";
        Width = 720; Height = 520; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0),
            Children = { _scan, _repair, _close }
        };
        var panel = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(_log);
        Content = panel;
        _scan.Click += async (_, _) => await RunAsync(_scanFn, repaired: false);
        _repair.Click += async (_, _) => await RunAsync(_repairFn, repaired: true);
        _close.Click += (_, _) => Close();
        Opened += async (_, _) => await RunAsync(_scanFn, repaired: false);
    }

    private async Task RunAsync(Func<Task<PstScanReport>> work, bool repaired)
    {
        _scan.IsEnabled = _repair.IsEnabled = false;
        _log.Text = repaired ? "Repairing..." : "Scanning...";
        try
        {
            var report = await work();
            _log.Text = Summarize(report, repaired);
            _repair.IsEnabled = _canRepair && report.Fixable > 0;
        }
        catch (Exception ex) when (ex is PstException or IOException or InvalidOperationException or ObjectDisposedException)
        { _log.Text = $"The operation failed: {ex.Message}"; }
        finally { _scan.IsEnabled = true; }
    }

    private string Summarize(PstScanReport r, bool repaired)
    {
        var lines = new List<string>();
        if (repaired) lines.Add($"Repaired {r.Fixed} inconsistenc{(r.Fixed == 1 ? "y" : "ies")}.");
        if (r.Findings.Count == 0) lines.Add("No problems found. The archive is structurally sound.");
        else
        {
            lines.Add($"{r.Findings.Count} problem line{(r.Findings.Count == 1 ? "" : "s")} found; " +
                      (r.Fixable > 0 ? $"{r.Fixable} can be repaired automatically." : "none can be repaired automatically."));
            if (r.Fixable == 0)
                lines.Add("Make a copy of the file and run Microsoft's Inbox Repair Tool (SCANPST.EXE) on the copy.");
            else if (!_canRepair)
                lines.Add("This archive is open read-only, so it cannot be repaired.");
            lines.Add("");
            lines.AddRange(r.Findings.Take(500));
            if (r.Findings.Count > 500) lines.Add($"... and {r.Findings.Count - 500} more.");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
