using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenOutlook.Desktop;

/// <summary>One row of the folder picker. Unread &gt; 0 shows the row bold with the count in blue, like Outlook's folder list.</summary>
public sealed class FolderPickItem(string name, object? tag, int unread = 0, bool selectable = true)
{
    public string Name { get; } = name;
    public object? Tag { get; } = tag;
    public int Unread { get; } = unread;
    public bool Selectable { get; } = selectable;
    public List<FolderPickItem> Children { get; } = [];
}

/// <summary>
/// The "Move Items" window, laid out like Outlook's: a prompt, the folder tree of the mailbox or archive, and OK / Cancel / New... buttons on the right.
/// New... asks the caller to create a folder under the selected one and returns it, so the new folder appears in the tree and is selected.
/// </summary>
internal sealed class FolderPickerWindow : Window
{
    private readonly TreeView _tree = new() { MinWidth = 300 };
    private readonly Button _ok;
    private readonly Button _new;
    private readonly Func<FolderPickItem?, Task<FolderPickItem?>>? _createNew;

    public FolderPickItem? Result { get; private set; }

    public FolderPickerWindow(string title, string prompt, IReadOnlyList<FolderPickItem> roots, Func<FolderPickItem?, Task<FolderPickItem?>>? createNew)
    {
        _createNew = createNew;
        Title = title;
        Width = 540; Height = 500; MinWidth = 420; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _ok = new Button { Content = "OK", MinWidth = 96, IsEnabled = false, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", MinWidth = 96, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        _new = new Button { Content = "New...", MinWidth = 96, IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Center, IsVisible = createNew is not null };
        _ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();
        _new.Click += async (_, _) => await NewClickedAsync();
        _tree.SelectionChanged += (_, _) => UpdateButtons();
        _tree.DoubleTapped += (_, _) => { if (_ok.IsEnabled) Accept(); };
        foreach (var root in roots) _tree.Items.Add(Build(root, depth: 0));

        var buttons = new StackPanel { Spacing = 10, Width = 110, Children = { _ok, cancel, _new } };
        var grid = new Grid
        {
            Margin = new Thickness(12),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        grid.Children.Add(new TextBlock { Text = prompt, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) });
        buttons.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(buttons, 1); Grid.SetRow(buttons, 1);
        var frame = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _tree };
        Grid.SetRow(frame, 1);
        grid.Children.Add(frame);
        grid.Children.Add(buttons);
        Content = grid;
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
        Opened += (_, _) =>
        {
            if (_tree.Items.OfType<TreeViewItem>().FirstOrDefault() is { } first) first.IsExpanded = true;
            _tree.Focus();
        };
    }

    private TreeViewItem Build(FolderPickItem item, int depth)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var label = new TextBlock { Text = item.Name, FontWeight = item.Unread > 0 ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };
        if (!item.Selectable) label.Foreground = Brushes.Gray;          // (assigning null would make the text invisible)
        header.Children.Add(label);
        if (item.Unread > 0)
            header.Children.Add(new TextBlock { Text = $"({item.Unread:N0})", Foreground = new SolidColorBrush(Color.Parse("#0F6CBD")), VerticalAlignment = VerticalAlignment.Center });
        var node = new TreeViewItem { Header = header, Tag = item, IsExpanded = depth == 0 };
        foreach (var child in item.Children) node.Items.Add(Build(child, depth + 1));
        return node;
    }

    private FolderPickItem? Selected => (_tree.SelectedItem as TreeViewItem)?.Tag as FolderPickItem;

    private void UpdateButtons()
    {
        _ok.IsEnabled = Selected is { Selectable: true };
        _new.IsEnabled = _tree.SelectedItem is not null;
    }

    private void Accept()
    {
        if (Selected is not { Selectable: true } picked) return;
        Result = picked;
        Close();
    }

    private async Task NewClickedAsync()
    {
        if (_createNew is null || _tree.SelectedItem is not TreeViewItem parentNode) return;
        _new.IsEnabled = false;
        try
        {
            var created = await _createNew(Selected);
            if (created is null) return;
            var node = Build(created, depth: 1);
            parentNode.Items.Add(node);
            parentNode.IsExpanded = true;
            _tree.SelectedItem = node;
        }
        finally { UpdateButtons(); }
    }
}
