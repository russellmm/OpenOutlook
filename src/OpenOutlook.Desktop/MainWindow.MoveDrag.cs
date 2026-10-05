using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>
/// Drag messages onto archive folders to move them (Editing Mode only). The drop target accepts our
/// own text payload only, checks the destination store is writable, and runs every message through
/// IPstEngine.MoveMessage - the same native re-link engine the Delete-to-Deleted-Items path uses.
/// Read-only archives show a drop of none: no move can start against them.
/// </summary>
public partial class MainWindow
{
    private const string MovePayloadPrefix = "OO_PST_MOVE\n";
    private bool _moveDragWired;
    private Point _messageDragStart;
    private bool _messageDragCandidate;
    private bool _messageDragRightButton;
    private DateTime _lastDragEndUtc = DateTime.MinValue;

    /// <summary>A right-button drag ends with a release that Avalonia would otherwise treat as an
    /// ordinary right-click, popping the message context menu over the Move/Copy popup we just
    /// showed. Suppress the flyout for a moment after any drag finishes.</summary>
    private void MessageListContextRequested(object? sender, Avalonia.Controls.ContextRequestedEventArgs e)
    {
        if ((DateTime.UtcNow - _lastDragEndUtc).TotalMilliseconds < 600) e.Handled = true;
    }

    /// <summary>Attaches the message-list drag source once (called from the folder-load path where
    /// MessageList is guaranteed live).</summary>
    private void InitMessageDrag()
    {
        if (_moveDragWired) return;
        _moveDragWired = true;
        InitMessageContextMenu();
        InitMessageListFileDrop();
        MessageList.AddHandler(PointerPressedEvent, MessageListDragPressed, RoutingStrategies.Tunnel);
        MessageList.AddHandler(PointerMovedEvent, MessageListDragMoved, RoutingStrategies.Tunnel);
        MessageList.AddHandler(PointerReleasedEvent, (_, _) => _messageDragCandidate = false, RoutingStrategies.Tunnel);
        MessageList.AddHandler(PointerCaptureLostEvent, (_, _) => _messageDragCandidate = false, RoutingStrategies.Bubble);
        MessageList.AddHandler(ContextRequestedEvent, MessageListContextRequested, RoutingStrategies.Tunnel);
    }

    /// <summary>Right-click path for the same move engine: "Move to Folder..." opens a picker over
    /// the current archive's folders. Doubles as the headless-testable surface for drag moves.</summary>
    private void InitMessageContextMenu()
    {
        if (MessageList.ContextFlyout is not null) return;
        MenuItem Action(string header, string action)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await ExecuteMailActionAsync(action);
            return item;
        }
        var moveItem = new MenuItem { Header = "Move to Folder\u2026" };
        moveItem.Click += async (_, _) => await MoveViaDialogAsync(copy: false);
        var copyItem = new MenuItem { Header = "Copy to Folder\u2026" };
        copyItem.Click += async (_, _) => await MoveViaDialogAsync(copy: true);
        var archiveItem = new MenuItem { Header = "Copy to Archive Folder\u2026" };
        archiveItem.Click += async (_, _) => await CopyToArchiveViaDialogAsync();
        var flyout = new MenuFlyout();
        foreach (var entry in new[] { Action("Mark as Read", "read"), Action("Mark as Unread", "unread"), Action("Flag", "flag"), Action("Clear Flag", "unflag") })
            flyout.Items.Add(entry);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(moveItem);
        flyout.Items.Add(copyItem);
        flyout.Items.Add(archiveItem);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Action("Delete", "delete"));
        flyout.Opening += (_, _) =>
        {
            var gmail = _activeGmailFolder is not null;
            var pst = _activePath is not null;
            moveItem.IsEnabled = gmail || pst;
            copyItem.IsEnabled = gmail || pst;
            copyItem.Header = gmail ? "Add Label\u2026" : "Copy to Folder\u2026";
            archiveItem.IsVisible = pst;
        };
        MessageList.ContextFlyout = flyout;
    }

    private async Task MoveViaDialogAsync(bool copy)
    {
        InitMessageContextMenu();
        if (_activeGmailFolder is not null) { await MoveGmailViaDialogAsync(copy); return; }
        if (_activePath is not { } path || _activeFolder is not { } folder)
        {
            StatusText.Text = "Select a message in an archive first.";
            return;
        }
        var store = EnsureWritableStore(path);
        if (store is null) return;
        var nids = MessageList.SelectedItems.OfType<MessageListRow>().Select(r => r.Summary.Nid).ToArray();
        if (nids.Length == 0) return;
        // Only folders the user can actually see (same filter as the folder tree) - never the MAPI
        // system folders like Search Root or IPM_COMMON_VIEWS.
        FolderPickItem Pick(MailFolder f)
        {
            var item = new FolderPickItem(f.Name, f, f.UnreadCount, selectable: f.Nid != folder.Nid);
            foreach (var child in f.Children) item.Children.Add(Pick(child));
            return item;
        }
        var archiveRoot = new FolderPickItem(store.DisplayName, null, selectable: false);
        foreach (var rootFolder in PstFolderPresentation.VisibleRoots(store.Root)) archiveRoot.Children.Add(Pick(rootFolder));
        if (archiveRoot.Children.Count == 0) { StatusText.Text = "This archive has no other folder to move into."; return; }

        var noun = nids.Length == 1 ? "item" : "items";
        var dialog = new FolderPickerWindow(copy ? "Copy Items" : "Move Items", $"{(copy ? "Copy" : "Move")} the selected {noun} to:", [archiveRoot], async parent =>
        {
            var parentFolder = parent?.Tag as MailFolder ?? store.Root.Children.FirstOrDefault(c =>
                c.Name.Equals("Top of Outlook data file", StringComparison.OrdinalIgnoreCase)) ?? store.Root;
            var name = await PromptForFolderNameAsync(parentFolder.Name);
            if (string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                var created = await Task.Run(() => store.CreateFolder(parentFolder.Nid, name));
                InvalidateFolderCache(path);
                var parentNode = parent?.Tag is MailFolder pf ? FindFolderNode(FolderTree.Items.OfType<object>(), pf.Nid)
                    : FolderTree.Items.OfType<TreeViewItem>().FirstOrDefault(n => n.Tag is string p && p == path);
                if (parentNode is not null) { AddChildren(parentNode, created, path, new HashSet<uint>()); ApplyFolderOrder(parentNode); parentNode.IsExpanded = true; }
                StatusText.Text = $"Folder \"{created.Name}\" created.";
                return new FolderPickItem(created.Name, created);
            }
            catch (Exception ex) when (ex is PstException or IOException)
            {
                StatusText.Text = $"Could not create folder: {ex.Message}";
                return null;
            }
        });
        await dialog.ShowDialog(this);
        if (dialog.Result?.Tag is MailFolder dest)
            await MoveRowsToFolderAsync(path, folder.Nid, nids, dest, copy);
    }

    private void MessageListDragPressed(object? sender, PointerPressedEventArgs e)
    {
        _messageDragCandidate = false;
        var pressed = e.GetCurrentPoint(MessageList).Properties;
        // Left drag = move on drop (Outlook default); right drag = Move/Copy menu on drop.
        if (pressed.IsLeftButtonPressed) _messageDragRightButton = false;
        else if (pressed.IsRightButtonPressed) _messageDragRightButton = true;
        else return;
        if (MessageList.SelectedItems.OfType<MessageListRow>().Any() &&
            _activePath is { } path && _stores.ContainsKey(path) &&
            _activeFolder is { } folder)
        {
            _messageDragStart = e.GetPosition(MessageList);
            _messageDragCandidate = true;
        }
    }

    private async void MessageListDragMoved(object? sender, PointerEventArgs e)
    {
        if (!_messageDragCandidate) return;
        var pt = e.GetCurrentPoint(MessageList).Properties;
        var stillHeld = _messageDragRightButton ? pt.IsRightButtonPressed : pt.IsLeftButtonPressed;
        if (!stillHeld) { _messageDragCandidate = false; return; }
        var dx = e.GetPosition(MessageList).X - _messageDragStart.X;
        var dy = e.GetPosition(MessageList).Y - _messageDragStart.Y;
        if (dx * dx + dy * dy < 400) return;
        _messageDragCandidate = false;
        var path = _activePath;
        var folder = _activeFolder;
        if (path is null || folder is null) return;
        var nids = MessageList.SelectedItems.OfType<MessageListRow>().Select(r => r.Summary.Nid).ToArray();
        if (nids.Length == 0) return;
        var text = MovePayloadPrefix + path + "\n" + folder.Nid.ToString("x8") + "\n" +
                   string.Join("\n", nids.Select(n => n.ToString("x8"))) + "\n";
        var data = new DataObject();
        data.Set(DataFormats.Text, text);
        try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Move); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* drag aborted */ }
        finally { _lastDragEndUtc = DateTime.UtcNow; }
    }

    /// <summary>Marks a PST folder tree item as a drop target for message moves.</summary>
    private void EnableMoveDrops(TreeViewItem item)
    {
        DragDrop.SetAllowDrop(item, true);
        item.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (item.Tag is FolderSelection sel && CanAcceptMovePayload(e.Data, sel))
            {
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
            }
            else if (item.Tag is FolderSelection fileTarget && CanAcceptFileDrop(e.Data, fileTarget.Path))
            {
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
            }
            else
                e.DragEffects = DragDropEffects.None;
        });
        item.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (item.Tag is not FolderSelection sel) return;
            if (CanAcceptFileDrop(e.Data, sel.Path))
            {
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
                await HandleFileDropAsync(e.Data, sel.Path, sel.Folder);
                return;
            }
            var payload = ParseMovePayload(e.Data);
            if (payload is null || !CanAcceptMovePayload(e.Data, sel)) { e.DragEffects = DragDropEffects.None; return; }
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
            if (_messageDragRightButton)
            {
                // Right-button drop: Outlook's classic "Move Here / Copy Here / Cancel" popup.
                _messageDragRightButton = false;
                var menu = new MenuFlyout();
                var moveHere = new MenuItem { Header = "Move Here" };
                var copyHere = new MenuItem { Header = "Copy Here" };
                moveHere.Click += async (_, _) => await MoveRowsToFolderAsync(payload.Value.Path, payload.Value.SourceFolderNid, payload.Value.Nids, sel.Folder, copy: false, destPath: sel.Path);
                copyHere.Click += async (_, _) => await MoveRowsToFolderAsync(payload.Value.Path, payload.Value.SourceFolderNid, payload.Value.Nids, sel.Folder, copy: true, destPath: sel.Path);
                menu.Items.Add(moveHere);
                menu.Items.Add(copyHere);
                menu.ShowAt(item); // anchored to the folder that received the drop
            }
            else
                await MoveRowsToFolderAsync(payload.Value.Path, payload.Value.SourceFolderNid, payload.Value.Nids, sel.Folder, destPath: sel.Path);
        });
    }

    private static (string Path, uint SourceFolderNid, uint[] Nids)? ParseMovePayload(IDataObject data)
    {
        if (!data.Contains(DataFormats.Text)) return null;
        var text = data.GetText();
        if (text is null || !text.StartsWith(MovePayloadPrefix, StringComparison.Ordinal)) return null;
        var lines = text.Split('\n');
        if (lines.Length < 3) return null;
        var path = lines[1];
        if (path.Length == 0 || !uint.TryParse(lines[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var srcNid))
            return null;
        var nids = new List<uint>();
        for (var i = 3; i < lines.Length; i++)
            if (uint.TryParse(lines[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var nid))
                nids.Add(nid);
        return nids.Count == 0 ? null : (path, srcNid, nids.ToArray());
    }

    private bool CanAcceptMovePayload(IDataObject data, FolderSelection target)
    {
        var payload = ParseMovePayload(data);
        if (payload is not { } p) return false;
        if (!_stores.ContainsKey(p.Path)) return false;
        if (p.Path == target.Path) return p.SourceFolderNid != target.Folder.Nid;           // same folder - nothing to do
        return _stores.TryGetValue(target.Path, out var destination) && destination.CanWrite;   // another archive: copy across (and delete for a move)
    }

    private async Task MoveRowsToFolderAsync(string path, uint sourceFolderNid, uint[] nids, MailFolder destFolder, bool copy = false, string? destPath = null)
    {
        if (destPath is not null && destPath != path)
        {
            await CopyBetweenArchivesAsync(path, sourceFolderNid, nids, destPath, destFolder, copy);
            return;
        }
        var store = EnsureWritableStore(path);
        if (store is null) return;
        IReadOnlyList<MailSummary> sourceMessages;
        try
        {
            sourceMessages = _folderCache.TryGetValue((path, sourceFolderNid), out var cached) && cached.Count > 0
                ? cached
                : await Task.Run(() => store.GetMessages(store.AllFolders().First(f => f.Nid == sourceFolderNid)));
        }
        catch (Exception ex) when (ex is PstException or IOException)
        {
            StatusText.Text = $"Could not read the source folder: {ex.Message}";
            return;
        }
        var moved = 0;
        string? firstError = null;
        foreach (var nid in nids)
        {
            var summary = sourceMessages.FirstOrDefault(m => m.Nid == nid);
            if (summary is null) continue;
            try { if (copy) store.CopyMessage(summary, destFolder); else store.MoveMessage(summary, destFolder); moved++; }
            catch (Exception ex) when (ex is PstException or IOException) { firstError ??= ex.Message; }
        }
        if (_activePath == path && _activeFolder?.Nid == sourceFolderNid)
            _ = RefreshActivePstFolderAsync(path);
        InvalidateFolderCache(path); // badges/counts refresh on next folder render
        if (moved > 0 && !await VerifyOperationAsync(path)) return;
        StatusText.Text = moved > 0
            ? $"{(copy ? "Copied" : "Moved")} {moved} message{(moved == 1 ? "" : "s")} to {destFolder.Name} in {Path.GetFileName(path)}" +
              (firstError is null || moved == nids.Length ? " \u00b7 saved" : $" \u00b7 {nids.Length - moved} not moved: {firstError}")
            : $"Could not move: {firstError ?? "the dragged rows are no longer in the source folder"}";
    }
}
