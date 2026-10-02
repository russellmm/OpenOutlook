using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>
/// "New Folder..." on archive folders: prompts for a name, runs the spec-shaped
/// CreateFolder engine through the always-editable path (automatic backup + full
/// verification), and inserts the new node into the tree in place.
/// </summary>
public partial class MainWindow
{
    private async Task NewFolderAsync(FolderSelection sel)
    {
        var store = EnsureWritableStore(sel.Path);
        if (store is null) return;
        var name = await PromptForFolderNameAsync(sel.Folder.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        MailFolder created;
        try { created = await Task.Run(() => store.CreateFolder(sel.Folder.Nid, name)); }
        catch (Exception ex) when (ex is PstException or IOException)
        {
            StatusText.Text = $"Could not create folder: {ex.Message}";
            return;
        }
        if (!await VerifyOperationAsync(sel.Path)) return; // rollback already explained itself
        InvalidateFolderCache(sel.Path);
        var parentNode = FindFolderNode(FolderTree.Items.OfType<object>(), sel.Folder.Nid);
        if (parentNode is not null)
        {
            AddChildren(parentNode, created, sel.Path, new HashSet<uint>());
            ApplyFolderOrder(parentNode);
            parentNode.IsExpanded = true;
        }
        StatusText.Text = $"Folder \"{created.Name}\" created in {Path.GetFileName(sel.Path)} · verified";
    }

    /// <summary>New Folder on the archive root node: parent is the store's root folder.</summary>
    private async Task NewFolderAtRootAsync(string archivePath)
    {
        var store = EnsureWritableStore(archivePath);
        if (store is null) return;
        var name = await PromptForFolderNameAsync(store.DisplayName);
        if (string.IsNullOrWhiteSpace(name)) return;
        // Real archives keep user folders inside the "Top of Outlook data file" wrapper; creating
        // beside it would hide them from wrapper-scoped views. Prefer the wrapper as the parent.
        var parentFolder = store.Root.Children.FirstOrDefault(c =>
            c.Name.Equals("Top of Outlook data file", StringComparison.OrdinalIgnoreCase)) ?? store.Root;
        MailFolder created;
        try { created = await Task.Run(() => store.CreateFolder(parentFolder.Nid, name)); }
        catch (Exception ex) when (ex is PstException or IOException)
        {
            StatusText.Text = $"Could not create folder: {ex.Message}";
            return;
        }
        if (!await VerifyOperationAsync(archivePath)) return;
        InvalidateFolderCache(archivePath);
        var archiveNode = FolderTree.Items.OfType<TreeViewItem>()
            .FirstOrDefault(n => n.Tag is string p && p == archivePath);
        if (archiveNode is not null)
        {
            AddChildren(archiveNode, created, archivePath, new HashSet<uint>());
            ApplyFolderOrder(archiveNode);
            archiveNode.IsExpanded = true;
        }
        StatusText.Text = $"Folder \"{created.Name}\" created in {Path.GetFileName(archivePath)} \u00b7 verified";
    }

    /// <summary>Delete Folder... with an Outlook-style confirmation; empty folders only (the engine
    /// refuses anything containing messages or subfolders), then verify-or-rollback as always.</summary>
    private async Task DeleteFolderWithConfirmAsync(FolderSelection sel)
    {
        var yes = await ConfirmAsync($"Delete the empty folder \"{sel.Folder.Name}\"?",
            "Messages inside must be moved out first. The archive backup keeps a safety copy.");
        if (!yes) return;
        var store = EnsureWritableStore(sel.Path);
        if (store is null) return;
        try { await Task.Run(() => store.DeleteFolder(sel.Folder.Nid)); }
        catch (Exception ex) when (ex is PstException or IOException)
        {
            StatusText.Text = $"Could not delete folder: {ex.Message}";
            return;
        }
        if (!await VerifyOperationAsync(sel.Path)) return;
        InvalidateFolderCache(sel.Path);
        var node = FindFolderNode(FolderTree.Items.OfType<object>(), sel.Folder.Nid);
        if (node?.Parent is ItemsControl host) host.Items.Remove(node);
        else if (node is not null) FolderTree.Items.Remove(node);
        StatusText.Text = $"Folder \"{sel.Folder.Name}\" deleted from {Path.GetFileName(sel.Path)} \u00b7 verified";
    }

    private async Task<bool> ConfirmAsync(string question, string detail)
    {
        var dialog = new Window
        {
            Title = "Confirm", Width = 440, Height = 190, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false
        };
        var yes = new Button { Content = "Delete", IsDefault = true };
        var no = new Button { Content = "Cancel", IsCancel = true };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 10, Children =
            {
                new TextBlock { Text = question, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = detail, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    private static TreeViewItem? FindFolderNode(System.Collections.IEnumerable items, uint nid)
    {
        foreach (var obj in items)
        {
            if (obj is not TreeViewItem node) continue;
            if (node.Tag is FolderSelection sel && sel.Folder.Nid == nid) return node;
            var deep = FindFolderNode(node.Items, nid);
            if (deep is not null) return deep;
        }
        return null;
    }

    private async Task<string?> PromptForFolderNameAsync(string parentName)
    {
        var dialog = new Window
        {
            Title = $"New folder in {parentName}",
            Width = 400, Height = 170, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false
        };
        var box = new TextBox { Watermark = "Folder name" };
        var ok = new Button { Content = "Create", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => dialog.Close(box.Text);
        cancel.Click += (_, _) => dialog.Close((string?)null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 12, Children =
            {
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
            }
        };
        ok.IsEnabled = false;
        box.TextChanged += (_, _) => ok.IsEnabled = !string.IsNullOrWhiteSpace(box.Text);
        dialog.Opened += (_, _) => box.Focus(); // no window manager: nothing else will focus it
        return await dialog.ShowDialog<string?>(this);
    }
}
