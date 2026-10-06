using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace OpenOutlook.Desktop;

/// <summary>The right-click menu of the message list's column headers (Arrange By, Reverse Sort, Field Chooser, Remove This Column, Group By This Field), as in Outlook.</summary>
public partial class MainWindow
{
    // The columns of the message list in grid order; the indexes are also what view-layout.json stores.
    private static readonly string[] ColumnNames = ["Attachment", "From", "Subject", "Received", "Size", "Importance", "Flag"];
    private static readonly string?[] SortPaths = ["HasAttachment", "FromSort", "Subject", "ReceivedSort", "SizeBytes", "ImportanceSort", "IsFlagged"];
    private static readonly string?[] GroupPaths = ["AttachmentName", "FromDisplay", "Subject", "DateGroup", null, "ImportanceName", "FlagName"];
    private static readonly bool[] NewestFirst = [true, false, false, true, true, true, true];       // the direction "Arrange By" starts with

    private string? _groupPath;                                    // a group-by field other than the date sections (null = date sections when ticked, else none)

    /// <summary>The column whose header is under the pointer, or null when the event did not come from a header.</summary>
    private int? HeaderColumnIndexOf(object? source, out Control? header)
    {
        header = (source as Visual)?.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true);
        if (header is not DataGridColumnHeader h) return null;
        for (var i = 0; i < MessageList.Columns.Count; i++)
            if (ReferenceEquals(MessageList.Columns[i].Header, h.Content) || Equals(MessageList.Columns[i].Header, h.Content)) return i;
        return null;
    }

    private void ShowColumnHeaderMenu(int column, Control anchor)
    {
        MenuItem Item(string text, string icon, Action? run, bool enabled = true, string? tip = null)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled };
            if (icon.Length > 0) item.Icon = Themed.Icon(icon, 16);
            if (tip is not null) ToolTip.SetTip(item, tip);
            if (run is not null) item.Click += (_, _) => run();
            return item;
        }
        var arrange = Item("Arrange By", "OlIconSortUp", null);
        for (var i = 0; i < ColumnNames.Length; i++)
        {
            var index = i;
            arrange.Items.Add(Item(index == 3 ? "Date (Received)" : index == 0 ? "Attachments" : ColumnNames[index], "", () => SortMessageList(index, !NewestFirst[index])));
        }
        var visible = MessageList.Columns.Count(c => c.IsVisible);
        var groupable = GroupPaths[column] is not null;
        var flyout = new MenuFlyout
        {
            Items =
            {
                arrange,
                Item("Reverse Sort", "OlIconSortUp", ReverseMessageSort),
                new Separator(),
                Item("Field Chooser", "OlIconFilter", ShowFieldChooser),
                Item("Remove This Column", "", () => SetColumnVisible(column, false), enabled: visible > 1),
                new Separator(),
                Item("Group By This Field", "OlIconCleanUp", () => GroupMessageListBy(column), groupable, groupable ? null : "Messages cannot be grouped by this field"),
                Item("Group by Box", "OlIconReadingPane", null, false, "To be implemented"),
                new Separator(),
                Item("View Settings…", "OlIconGear", null, false, "To be implemented")
            }
        };
        flyout.ShowAt(anchor, showAtPointer: true);
    }

    /// <summary>Orders the list by a column without touching the folder; a date-sections view keeps its sections and orders inside them.</summary>
    internal void SortMessageList(int column, bool ascending)
    {
        if (MessageList.ItemsSource is not DataGridCollectionView view || SortPaths[column] is not { } path) return;
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(DataGridSortDescription.FromPath(path, ascending ? ListSortDirection.Ascending : ListSortDirection.Descending));
        StatusText.Text = $"Arranged by {ColumnNames[column]}, {(ascending ? "ascending" : "descending")}.";
    }

    private void ReverseMessageSort()
    {
        if (MessageList.ItemsSource is not DataGridCollectionView view) return;
        if (view.SortDescriptions.Count == 0) { SortMessageList(3, ascending: true); return; }       // the default order is newest first
        var current = view.SortDescriptions[0];
        var column = Array.IndexOf(SortPaths, current.PropertyPath);
        if (column >= 0) SortMessageList(column, current.Direction != ListSortDirection.Ascending);
    }

    internal void SetColumnVisible(int column, bool visible)
    {
        if (!visible && MessageList.Columns.Count(c => c.IsVisible) <= 1) return;
        var target = MessageList.Columns[column];
        target.IsVisible = visible;
        if (visible && column >= 5)                                  // Importance and Flag come in at the left, next to the paperclip, as in Outlook
            target.DisplayIndex = column == 5 ? 0 : Math.Min(1, MessageList.Columns.Count - 1);
        PersistViewLayout();
    }

    private void ShowFieldChooser()
    {
        var stack = new StackPanel { Margin = new Thickness(18), Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = "Show these fields in the message list:", FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });
        for (var i = 0; i < ColumnNames.Length; i++)
        {
            var index = i;
            var box = new CheckBox { Content = ColumnNames[i], IsChecked = MessageList.Columns[i].IsVisible };
            box.IsCheckedChanged += (_, _) =>
            {
                if ((box.IsChecked == true) == MessageList.Columns[index].IsVisible) return;
                SetColumnVisible(index, box.IsChecked == true);
                box.IsChecked = MessageList.Columns[index].IsVisible;          // the last visible column cannot be removed
            };
            stack.Children.Add(box);
        }
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(22, 5), Margin = new Thickness(0, 10, 0, 0), IsDefault = true };
        var window = new Window { Title = "Field Chooser", Width = 300, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = stack, Background = Themed.Brush("OlCard", Avalonia.Media.Brushes.White), CanResize = false };
        close.Click += (_, _) => window.Close();
        stack.Children.Add(close);
        window.Show(this);
    }

    /// <summary>"Group By This Field": sections by the field of the clicked column (date sections for Received, as the status-bar tick does).</summary>
    internal void GroupMessageListBy(int column)
    {
        if (GroupPaths[column] is not { } path) return;
        _groupPath = path == "DateGroup" ? null : path;
        _suppressGroupToggle = true;
        try { GroupByDateCheck.IsChecked = path == "DateGroup"; }
        finally { _suppressGroupToggle = false; }
        Interlocked.Increment(ref _messageVersion);
        ClearReader();
        if (_currentMessages is not null) ShowMessages(_currentMessages);
        else if (_currentGraphMessages is not null) ShowGraphMessages(_currentGraphMessages);
        StatusText.Text = $"Grouped by {ColumnNames[column]}.";
    }
}
