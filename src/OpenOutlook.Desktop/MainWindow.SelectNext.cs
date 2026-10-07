using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Threading;

namespace OpenOutlook.Desktop;

/// <summary>After the highlighted message is deleted, archived or moved away, the message that takes its place in the list is highlighted (and shown in the reading pane), as in Outlook.</summary>
public partial class MainWindow
{
    private int _nextSelectIndex = -1;                  // display index of the first highlighted row removed in this burst; -1 = nothing pending

    /// <summary>The message rows in the order the list shows them (sorted and grouped).</summary>
    private List<object> DisplayedRows() =>
        MessageList.ItemsSource is DataGridCollectionView view ? view.Cast<object>().Where(o => o is MessageListRow or GraphMessageListRow).ToList() : [];

    private bool IsHighlighted(object row) =>
        ReferenceEquals(MessageList.SelectedItem, row) || MessageList.SelectedItems.Cast<object>().Contains(row);

    /// <summary>Call just before a row leaves the list. When it was highlighted, the row now at its place is highlighted once the current batch of removals is done.</summary>
    private void NoteRowLeaving(object row)
    {
        if (!IsHighlighted(row)) return;
        var index = DisplayedRows().IndexOf(row);
        if (index < 0) return;
        var first = _nextSelectIndex < 0;
        _nextSelectIndex = first ? index : Math.Min(_nextSelectIndex, index);
        if (first) Dispatcher.UIThread.Post(SelectNextAfterRemoval);
    }

    private void SelectNextAfterRemoval()
    {
        var index = _nextSelectIndex;
        _nextSelectIndex = -1;
        SelectDisplayedRow(index);
    }

    /// <summary>Highlights the row at this place in the list (the last one when the list is now shorter); does nothing when something else is already highlighted.</summary>
    private void SelectDisplayedRow(int index)
    {
        if (index < 0 || MessageList.SelectedItem is not null) return;
        var rows = DisplayedRows();
        if (rows.Count == 0) return;
        var next = rows[Math.Min(index, rows.Count - 1)];
        MessageList.SelectedItem = next;
        MessageList.ScrollIntoView(next, null);
    }

    /// <summary>The display index of the first highlighted row among these (for a PST list that is rebuilt after the change).</summary>
    private int FirstDisplayedIndexOf(IEnumerable<object> rows)
    {
        var shown = DisplayedRows();
        var indexes = rows.Select(r => shown.IndexOf(r)).Where(i => i >= 0).ToList();
        return indexes.Count == 0 ? -1 : indexes.Min();
    }
}
