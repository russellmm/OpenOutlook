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
