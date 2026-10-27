using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenOutlook.Desktop;

/// <summary>
/// View > Change View > Default View: puts the mail view back as it was first installed, like Outlook's Reset View - the folder / message list / reading pane
/// widths of a new installation (2 : 5 : 4) and the message list's columns as the window defines them (which columns show, their order and widths). The window
/// size and position stay; the reset view is saved like any change made by hand.
/// </summary>
public partial class MainWindow
{
    private MessageColumnLayout[]? _defaultColumns;

    /// <summary>Takes the message list's columns as the window defines them, before a saved layout changes them.</summary>
    private void CaptureDefaultColumns() =>
        _defaultColumns = MessageList.Columns.Select((column, index) => new MessageColumnLayout(index, column.DisplayIndex,
            column.Width.UnitType is DataGridLengthUnitType.Pixel or DataGridLengthUnitType.Star ? column.Width.Value : column.ActualWidth,
            column.Width.UnitType == DataGridLengthUnitType.Star, column.IsVisible)).ToArray();

    private void DefaultViewClicked(object? sender, RoutedEventArgs e) => ApplyDefaultView();

    internal void ApplyDefaultView()
    {
        ExitPortraitReading();
        var defaults = new ViewLayoutSettings();
        PaneGrid.ColumnDefinitions[0].Width = new GridLength(defaults.FolderPaneWeight, GridUnitType.Star);
        PaneGrid.ColumnDefinitions[2].Width = new GridLength(defaults.MessagePaneWeight, GridUnitType.Star);
        PaneGrid.ColumnDefinitions[4].Width = new GridLength(defaults.ReaderPaneWeight, GridUnitType.Star);
        if (_defaultColumns is { } columns)
            foreach (var column in columns.OrderBy(column => column.DisplayIndex))
            {
                var target = MessageList.Columns[column.ColumnIndex];
                target.IsVisible = column.Visible;
                target.DisplayIndex = column.DisplayIndex;
                target.Width = new DataGridLength(column.Width,
                    column.IsStar ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel);
            }
        ScheduleLayoutSave();
        StatusText.Text = "The view is back to the default layout.";
        AppLog.Note("view", "Change View > Default View: pane widths and message list columns reset");
    }
}
