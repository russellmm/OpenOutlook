using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using OpenOutlook.Desktop;
using Xunit;

namespace OpenOutlook.HeadlessTests;

/// <summary>The dividers between the folder pane, the message list and the reading pane, and View > Change View > Default View.</summary>
public sealed class PaneSplitterHeadlessTests
{
    private static (MainWindow Window, Grid Grid) Open()
    {
        var window = new MainWindow { Width = 1400, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, window.FindControl<Grid>("PaneGrid")!);
    }

    private static Point Grip(MainWindow window, Grid grid, int column)
    {
        var splitter = grid.Children.OfType<PaneSplitter>().Single(s => Grid.GetColumn(s) == column);
        return splitter.TranslatePoint(new Point(2, splitter.Bounds.Height / 2), window)!.Value;
    }

    private static void Drag(MainWindow window, Point start, params double[] offsets)
    {
        window.MouseDown(start, MouseButton.Left);
        foreach (var offset in offsets) window.MouseMove(new Point(start.X + offset, start.Y), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(start.X + offsets[^1], start.Y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Dragging_the_folder_divider_narrows_the_folder_pane_by_the_pointer_distance_and_never_collapses_it()
    {
        var (window, grid) = Open();
        try
        {
            Assert.Empty(grid.Children.OfType<GridSplitter>());   // Avalonia's GridSplitter collapsed the folder pane on the owner's 2.7x display
            var folder = grid.ColumnDefinitions[0].ActualWidth;
            var messages = grid.ColumnDefinitions[2].ActualWidth;
            var reader = grid.ColumnDefinitions[4].ActualWidth;

            // 1. step by step to the left: the folder pane follows the pointer, the message list takes the room, the reading pane stays
            Drag(window, Grip(window, grid, 1), -5, -10, -20, -35, -50);
            Assert.Equal(folder - 50, grid.ColumnDefinitions[0].ActualWidth, 1.0);
            Assert.Equal(messages + 50, grid.ColumnDefinitions[2].ActualWidth, 1.0);
            Assert.Equal(reader, grid.ColumnDefinitions[4].ActualWidth, 1.0);

            // 2. far past the window's edge in one jump: the folder pane stops at its minimum and the divider can be grabbed again
            Drag(window, Grip(window, grid, 1), -2000);
            Assert.Equal(PaneSplitter.MinPaneWidth, grid.ColumnDefinitions[0].ActualWidth, 1.0);
            Drag(window, Grip(window, grid, 1), 100);
            Assert.Equal(PaneSplitter.MinPaneWidth + 100, grid.ColumnDefinitions[0].ActualWidth, 1.0);

            // 3. the reading pane divider: to the right until the reading pane is at its minimum; the folder pane does not move
            var folderNow = grid.ColumnDefinitions[0].ActualWidth;
            Drag(window, Grip(window, grid, 3), 300, 3000);
            Assert.Equal(PaneSplitter.MinPaneWidth, grid.ColumnDefinitions[4].ActualWidth, 1.0);
            Assert.Equal(folderNow, grid.ColumnDefinitions[0].ActualWidth, 1.0);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Default_view_puts_the_pane_widths_and_message_columns_back()
    {
        var (window, grid) = Open();
        try
        {
            double Panes() => grid.ColumnDefinitions[0].ActualWidth + grid.ColumnDefinitions[2].ActualWidth + grid.ColumnDefinitions[4].ActualWidth;
            var list = window.FindControl<DataGrid>("MessageList")!;
            var visible = list.Columns.Select(c => c.IsVisible).ToArray();
            var order = list.Columns.Select(c => c.DisplayIndex).ToArray();
            Assert.NotNull(window.FindControl<MenuItem>("DefaultViewMenuItem"));

            Drag(window, Grip(window, grid, 1), -120);
            var shown = list.Columns.First(c => c.IsVisible);
            shown.IsVisible = false;
            list.Columns.Last().DisplayIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(Panes() * 2 / 11, grid.ColumnDefinitions[0].ActualWidth, 1.0);

            window.ApplyDefaultView();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Panes() * 2 / 11, grid.ColumnDefinitions[0].ActualWidth, 1.0);   // a new installation's 2 : 5 : 4
            Assert.Equal(Panes() * 5 / 11, grid.ColumnDefinitions[2].ActualWidth, 1.0);
            Assert.Equal(visible, list.Columns.Select(c => c.IsVisible).ToArray());
            Assert.Equal(order, list.Columns.Select(c => c.DisplayIndex).ToArray());
        }
        finally { window.Close(); }
    }
}
