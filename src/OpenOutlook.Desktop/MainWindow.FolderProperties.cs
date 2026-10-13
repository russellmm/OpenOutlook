using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenOutlook.Auth;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>Right-click > Properties on a folder-list row: a read-only sheet with the details of that folder (name, kind, where it lives, item and unread counts, size).</summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<string, int> _msTotal = new(StringComparer.Ordinal);            // accountId|folderId -> item count at the last folder-list load
    private readonly Dictionary<string, int> _gmailTotal = new(StringComparer.Ordinal);         // accountId|labelId -> message count at the last label load

    private MenuItem FolderPropertiesMenuItem(TreeViewItem item)
    {
        var properties = new MenuItem { Header = "Properties" };
        properties.Click += (_, _) => _ = ShowFolderPropertiesAsync(item);
        return properties;
    }

    /// <summary>The names of the rows above this one, root first: "rmarrash_hotmail \ Banks \ Statements".</summary>
    private string FolderPathOf(TreeViewItem item)
    {
        var names = new List<string>();
        for (Control? node = item; node is not null; node = node.Parent as Control)
        {
            if (node is not TreeViewItem row) continue;
            var name = row.Tag switch
            {
                FolderSelection s => s.Folder.Name,
                MicrosoftFolderSelection m => m.Name,
                GmailFolderSelection g => g.Name,
                ConnectedAccount a => a.DisplayAddress,
                string path => _stores.TryGetValue(path, out var store) ? store.DisplayName : Path.GetFileNameWithoutExtension(path),
                _ => null
            };
            if (name is not null) names.Add(name);
        }
        names.Reverse();
        return string.Join(" \\ ", names);
    }

    private static string FormatFolderBytes(long bytes) => bytes switch
    {
        < 1024 => bytes + " bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB ({bytes:N0} bytes)",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB ({bytes:N0} bytes)",
        _ => $"{bytes / 1073741824.0:0.##} GB ({bytes:N0} bytes)"
    };

    private async Task ShowFolderPropertiesAsync(TreeViewItem item)
    {
        if (CreateFolderPropertiesSheet(item) is not { } dialog) return;
        await dialog.ShowDialog(this);
    }

    /// <summary>Builds the sheet (not shown); null when the row has no properties. The counts that need a read of the folder are filled in once it opens.</summary>
    internal Window? CreateFolderPropertiesSheet(TreeViewItem item)
    {
        var rows = new List<(string Label, string Value)>();
        var valueBlocks = new Dictionary<string, SelectableTextBlock>();
        string title;
        Func<Task>? fillLater = null;
        var children = item.Items.OfType<TreeViewItem>().Count();

        switch (item.Tag)
        {
            case FolderSelection sel:
            {
                title = sel.Folder.Name;
                var isMirror = IsMirrorCopy(sel.Path);
                rows.Add(("Name", sel.Folder.Name));
                rows.Add(("Kind", isMirror ? "Folder in a mailbox copy (the local PST behind a connected account)" : "Folder in an Outlook data file (PST)"));
                rows.Add(("Folder path", FolderPathOf(item)));
                rows.Add(("Data file", sel.Path));
                rows.Add(("Items", sel.Folder.ContentCount.ToString("N0")));
                rows.Add(("Unread", sel.Folder.UnreadCount.ToString("N0")));
                rows.Add(("Subfolders", children.ToString("N0")));
                rows.Add(("Size of the messages", "calculating…"));
                rows.Add(("Folder ID", "0x" + sel.Folder.Nid.ToString("X")));
                if (_stores.TryGetValue(sel.Path, out var store))
                    fillLater = async () =>
                    {
                        try
                        {
                            var messages = await Task.Run(() => store.GetMessages(sel.Folder));
                            long bytes = messages.Sum(m => (long)m.Size);
                            valueBlocks["Items"].Text = messages.Count.ToString("N0");
                            valueBlocks["Unread"].Text = messages.Count(m => !m.IsRead).ToString("N0");
                            valueBlocks["Size of the messages"].Text = FormatFolderBytes(bytes) + "  (this folder only, not its subfolders)";
                        }
                        catch (Exception ex) when (ex is PstException or IOException)
                        { valueBlocks["Size of the messages"].Text = "not available: " + ex.Message; }
                    };
                else rows[^2] = ("Size of the messages", "not available (the file is not open)");
                break;
            }
            case MicrosoftFolderSelection ms:
            {
                title = ms.Name;
                rows.Add(("Name", ms.Name));
                rows.Add(("Kind", "Folder in a Microsoft (Hotmail / Outlook.com) mailbox"));
                rows.Add(("Account", ms.Account.DisplayAddress));
                rows.Add(("Folder path", FolderPathOf(item)));
                var key = ms.Account.AccountId + "|" + ms.Id;
                rows.Add(("Items", _msTotal.TryGetValue(key, out var total) ? total.ToString("N0") + "  (as of the last folder-list refresh)" : "not loaded yet"));
                rows.Add(("Unread", _msUnread.TryGetValue(key, out var unread) ? unread.ToString("N0") : "not loaded yet"));
                rows.Add(("Subfolders", children.ToString("N0")));
                rows.Add(("Stored", "on the Microsoft server; a copy is kept in the account's mailbox copy"));
                break;
            }
            case GmailFolderSelection g:
            {
                title = g.Name;
                rows.Add(("Name", g.Name));
                rows.Add(("Kind", "Gmail label (shown as a folder)"));
                rows.Add(("Account", g.Account.DisplayAddress));
                var key = g.Account.AccountId + "|" + g.LabelId;
                rows.Add(("Items", _gmailTotal.TryGetValue(key, out var total) ? total.ToString("N0") + "  (messages with this label)" : "not loaded yet"));
                rows.Add(("Unread", _gmailUnread.TryGetValue(key, out var unread) ? unread.ToString("N0") : "not loaded yet"));
                rows.Add(("Label ID", g.LabelId));
                rows.Add(("Stored", "on the Gmail server; a copy is kept in the account's mailbox copy"));
                break;
            }
            case ConnectedAccount account:
                title = account.DisplayAddress;
                rows.Add(("Name", account.DisplayAddress));
                rows.Add(("Kind", account.Provider == OAuthProvider.Google ? "Gmail account" : "Microsoft (Hotmail / Outlook.com) account"));
                rows.Add(("Folders", children.ToString("N0") + " at the top level"));
                break;
            case string path:
            {
                var name = _stores.TryGetValue(path, out var store) ? store.DisplayName : Path.GetFileNameWithoutExtension(path);
                title = name;
                rows.Add(("Name", name));
                rows.Add(("Kind", IsMirrorCopy(path) ? "Mailbox copy (the local PST behind a connected account)" : "Outlook data file (PST)"));
                rows.Add(("File", path));
                try { rows.Add(("File size", FormatFolderBytes(new FileInfo(path).Length))); } catch (IOException) { rows.Add(("File size", "not available")); }
                rows.Add(("Folders", children.ToString("N0") + " at the top level"));
                rows.Add(("Editable", store is not null && store.CanWrite ? "yes" : "no (opened read-only)"));
                break;
            }
            default:
                StatusText.Text = "There are no properties to show for this item.";
                return null;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 4, 0, 0) };
        var labelBrush = Themed.Brush("OlTextSecondary", Brushes.Gray);
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = rows[i].Label, Foreground = labelBrush, Margin = new Thickness(0, 4, 16, 4), VerticalAlignment = VerticalAlignment.Top };
            var value = new SelectableTextBlock { Text = rows[i].Value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(label, i); Grid.SetRow(value, i); Grid.SetColumn(value, 1);
            grid.Children.Add(label); grid.Children.Add(value);
            valueBlocks[rows[i].Label] = value;
        }
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window
        {
            Title = "Properties",
            Width = 560, SizeToContent = SizeToContent.Height, MaxHeight = 560, MinHeight = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 10,
            Children =
            {
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new ScrollViewer { Content = grid, MaxHeight = 400, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
                close
            }
        };
        dialog.Opened += (_, _) => { close.Focus(); if (fillLater is not null) _ = fillLater(); };
        return dialog;
    }
}
