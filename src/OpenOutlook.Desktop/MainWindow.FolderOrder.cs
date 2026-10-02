using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using OpenOutlook.Auth;

namespace OpenOutlook.Desktop;

/// <summary>
/// Reordering the folder pane. Rows can be moved with the right-click menu (Move Up / Move Down /
/// Move to Top) or dragged between siblings; the arrangement is saved per parent in
/// folder-order.json and re-applied whenever the tree is rebuilt, so it survives restarts.
///
/// Only sibling order changes. Nothing here moves a folder between stores — archives are opened
/// read-only, and cross-store moves would be real mail operations with none of the safety this pane
/// promises; drag therefore refuses to drop into another parent's subtree.
/// </summary>
public partial class MainWindow
{
    internal const string FolderDragFormat = "application/x-openoutlook-folder-key";

    private readonly FolderPaneOrderStore _folderOrderStore = new();
    private Dictionary<string, List<string>> _folderOrder = new();
    private TreeViewItem? _dragCandidate;
    private Point _dragCandidateStart;
    private TreeViewItem? _dragSourceItem;
    private TreeViewItem? _dropTargetItem;
    private bool _dropBefore;

    private void InitializeFolderOrder()
    {
        _folderOrder = _folderOrderStore.Load();
        DragDrop.SetAllowDrop(FolderTree, true);
        FolderTree.AddHandler(PointerPressedEvent, FolderDragPointerPressed, RoutingStrategies.Tunnel);
        FolderTree.AddHandler(PointerMovedEvent, FolderDragPointerMoved, RoutingStrategies.Tunnel);
        FolderTree.AddHandler(PointerReleasedEvent, (_, _) => _dragCandidate = null, RoutingStrategies.Tunnel);
        FolderTree.AddHandler(PointerCaptureLostEvent, (_, _) => _dragCandidate = null, RoutingStrategies.Bubble);
        FolderTree.AddHandler(DragDrop.DragOverEvent, FolderDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        FolderTree.AddHandler(DragDrop.DropEvent, FolderDrop, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Stable identity for a node: survives renames never, moves never — it is the row's place,
    /// not its title. Unknown tags (anything unexpected) opt out of ordering entirely.</summary>
    internal static string? FolderKeyOf(TreeViewItem item) => item.Tag switch
    {
        string path when !string.IsNullOrWhiteSpace(path) => "pst:" + path,
        FolderSelection folder => "psf:" + folder.Path + "|" + folder.Folder.Nid.ToString("X8"),
        ConnectedAccount account => "acct:" + account.AccountId,
        MicrosoftFolderSelection mail => "gmf:" + mail.Account.AccountId + "/" + mail.Id,
        _ => null
    };

    private static string? FolderParentKeyOf(ItemsControl host) => host switch
    {
        TreeView => "",
        TreeViewItem item => FolderKeyOf(item),
        _ => null
    };

    private static ItemsControl? OwnerOf(TreeViewItem item)
    {
        var parent = (item as ILogical)?.LogicalParent;
        while (parent is not null and not ItemsControl) parent = (parent as ILogical)?.LogicalParent;
        return parent as ItemsControl;
    }

    /// <summary>Sorts a host's children into the saved order. Unknown or unranked siblings keep their
    /// natural relative order at the end, so newly discovered folders never jump around.</summary>
    private void ApplyFolderOrder(ItemsControl host)
    {
        var parentKey = FolderParentKeyOf(host);
        if (parentKey is null || !_folderOrder.TryGetValue(parentKey, out var saved) || saved.Count == 0) return;
        var items = host.Items.OfType<TreeViewItem>().ToList();
        if (items.Count < 2) return;
        var ordered = items.OrderBy(node => FolderPaneOrderStore.RankOf(saved, FolderKeyOf(node))).ToList();
        var selected = FolderTree.SelectedItem;
        var touchedSelection = false;
        for (var target = 0; target < ordered.Count; target++)
        {
            if (host.Items.IndexOf(ordered[target]) == target) continue;
            if (ReferenceEquals(ordered[target], selected) || ReferenceEquals(selected, host)) touchedSelection = true;
            host.Items.Remove(ordered[target]);
            host.Items.Insert(target, ordered[target]);
        }
        // Re-inserting the selected row drops its selection; put it back without a second load pass.
        if (touchedSelection && selected is not null) FolderTree.SelectedItem = selected;
    }

    private void SaveFolderOrderFor(ItemsControl host)
    {
        var parentKey = FolderParentKeyOf(host);
        if (parentKey is null) return;
        _folderOrder[parentKey] = [.. host.Items.OfType<TreeViewItem>()
            .Select(FolderKeyOf).Where(key => key is not null).Cast<string>()];
        try { _folderOrderStore.Save(_folderOrder); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { StatusText.Text = "Folder order changed, but it could not be saved. Check the settings folder."; }
    }

    private void MoveFolderNode(TreeViewItem item, int targetIndex)
    {
        if (OwnerOf(item) is not { } host) return;
        var current = host.Items.IndexOf(item);
        if (current < 0) return;
        targetIndex = Math.Clamp(targetIndex, 0, host.Items.Count - 1);
        if (targetIndex == current) return;
        var wasSelected = ReferenceEquals(FolderTree.SelectedItem, item);
        host.Items.Remove(item);
        host.Items.Insert(targetIndex, item);
        if (wasSelected) FolderTree.SelectedItem = item;
        SaveFolderOrderFor(host);
    }

    private void MoveFolderNodeBy(TreeViewItem item, int delta)
    {
        if (OwnerOf(item) is not { } host) return;
        var current = host.Items.IndexOf(item);
        if (current < 0) return;
        MoveFolderNode(item, current + delta);
    }

    /// <summary>Right-click menu for one tree row. Enabled state is recomputed every time the menu opens,
    /// because siblings come and go while the window lives.</summary>

    private void EnableFolderReordering(TreeViewItem item)
    {
        var menuOpeningExtras = new List<Action>();
        var up = new MenuItem { Header = "Move Up" };
        var down = new MenuItem { Header = "Move Down" };
        var top = new MenuItem { Header = "Move to Top" };
        up.Click += (_, _) => MoveFolderNodeBy(item, -1);
        down.Click += (_, _) => MoveFolderNodeBy(item, +1);
        top.Click += (_, _) => MoveFolderNode(item, 0);
        var items = new List<object> { up, down, top };
        if (item.Tag is FolderSelection anySel)
        {
            var newFolder = new MenuItem { Header = "New Folder\u2026" };
            newFolder.Click += (_, _) => _ = NewFolderAsync(anySel);
            items.Add(new Separator());
            items.Add(newFolder);
        }
        if (item.Tag is string rootArchivePath)
        {
            var newRootFolder = new MenuItem { Header = "New Folder\u2026" };
            newRootFolder.Click += (_, _) => _ = NewFolderAtRootAsync(rootArchivePath);
            items.Add(new Separator());
            items.Add(newRootFolder);
        }
        if (item.Tag is FolderSelection sel &&
            string.Equals(sel.Folder.Name, "Deleted Items", StringComparison.OrdinalIgnoreCase))
        {
            var purge = new MenuItem { Header = "Empty Deleted Items" };
            purge.Click += (_, _) => _ = EmptyDeletedItemsAsync(sel);
            items.Add(new Separator());
            items.Add(purge);
        }
        var menu = new MenuFlyout();
        foreach (var entry in items) menu.Items.Add(entry);
        menu.Opening += (_, _) =>
        {
            foreach (var update in menuOpeningExtras) update();
            var host = OwnerOf(item);
            var index = host?.Items.IndexOf(item) ?? -1;
            up.IsEnabled = index > 0;
            down.IsEnabled = host is not null && index >= 0 && index < host.Items.Count - 1;
            top.IsEnabled = index > 0;
        };
        item.ContextFlyout = menu;
    }

    // ---- drag and drop (sibling reorder only) ----

    private void FolderDragPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragCandidate = null;
        if (!e.GetCurrentPoint(FolderTree).Properties.IsLeftButtonPressed) return;
        var item = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (item is null || FolderKeyOf(item) is null) return;
        _dragCandidate = item;
        _dragCandidateStart = e.GetPosition(FolderTree);
    }

    private async void FolderDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate is not { } item) return;
        // Hovering must never start a drag: only a still-held left button counts. Without this check,
        // the candidate left behind by an earlier folder click launched DoDragDrop on a plain
        // mouse-over, putting the whole window in drag mode with a no-drop cursor over the list.
        if (!e.GetCurrentPoint(FolderTree).Properties.IsLeftButtonPressed)
        {
            _dragCandidate = null;
            return;
        }
        var position = e.GetPosition(FolderTree);
        if (Math.Abs(position.X - _dragCandidateStart.X) + Math.Abs(position.Y - _dragCandidateStart.Y) < 8) return;
        _dragCandidate = null;
        var key = FolderKeyOf(item);
        if (key is null) return;
        var data = new DataObject();
        data.Set(FolderDragFormat, key);
        _dragSourceItem = item;
        try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Move); }
        catch (Exception) { /* drag is an accelerator; the context menu always works. */ }
        finally { _dragSourceItem = null; _dropTargetItem = null; }
    }

    private void FolderDragOver(object? sender, DragEventArgs e)
    {
        _dropTargetItem = null;
        if (_dragSourceItem is null || !e.Data.Contains(FolderDragFormat))
        { e.DragEffects = DragDropEffects.None; return; }
        var target = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (target is null || ReferenceEquals(target, _dragSourceItem) || FolderKeyOf(target) is null ||
            !ReferenceEquals(OwnerOf(target), OwnerOf(_dragSourceItem)))
        { e.DragEffects = DragDropEffects.None; return; }
        _dropBefore = e.GetPosition(target).Y < target.Bounds.Height / 2;
        _dropTargetItem = target;
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void FolderDrop(object? sender, DragEventArgs e)
    {
        if (_dragSourceItem is not { } source || _dropTargetItem is not { } target) return;
        var host = OwnerOf(source);
        if (host is null || !ReferenceEquals(OwnerOf(target), host)) return;
        var targetIndex = host.Items.IndexOf(target);
        if (!_dropBefore) targetIndex++;
        var sourceIndex = host.Items.IndexOf(source);
        if (sourceIndex < 0 || targetIndex < 0) return;
        if (targetIndex > sourceIndex) targetIndex--; // the list shrinks by one when the row lifts out
        host.Items.Remove(source);
        host.Items.Insert(Math.Clamp(targetIndex, 0, host.Items.Count), source);
        SaveFolderOrderFor(host);
        e.Handled = true;
    }
}
