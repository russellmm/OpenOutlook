using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace OpenOutlook.Desktop;

/// <summary>
/// The divider between two star-sized panes of a grid (folder / message list / reading pane). Replaces Avalonia's GridSplitter, which measures each pointer move
/// against the splitter's own, not yet re-laid-out position and cancels the drag when the two panes' rounded widths drift by more than a device pixel (fractional
/// display scaling); its cancel put back the panes' original weights ("2*", "5*") next to a neighbour it had already set in pixels ("482*"), so the folder pane
/// shrank to a sliver on the first move and could not be grabbed again. This one takes every star column's width when the button goes down and sets the two
/// neighbours from the pointer's distance to that start point in grid coordinates, so neither layout lag nor rounding can push a pane away; each pane keeps at least
/// <see cref="MinPaneWidth"/>.
/// </summary>
public sealed class PaneSplitter : Border
{
    public const double MinPaneWidth = 80;

    private Grid? _grid;
    private double _startX;
    private double[]? _startWidths;

    /// <summary>Drags started (test seam: the column math is the same with or without a pointer).</summary>
    internal int DragsStarted { get; private set; }

    public PaneSplitter()
    {
        Background = Brushes.Transparent;
        Width = 5;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Parent is not Grid grid || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _grid = grid;
        _startX = e.GetPosition(grid).X;
        _startWidths = grid.ColumnDefinitions.Select(column => column.ActualWidth).ToArray();
        DragsStarted++;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_grid is null) return;
        MoveTo(e.GetPosition(_grid).X - _startX);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_grid is null) return;
        MoveTo(e.GetPosition(_grid).X - _startX);
        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        _grid = null;
        _startWidths = null;
    }

    /// <summary>Sets the panes on both sides to their start widths moved by <paramref name="delta"/> pixels; the other star columns keep their start widths.</summary>
    private void MoveTo(double delta)
    {
        if (_grid is null || _startWidths is null) return;
        var columns = _grid.ColumnDefinitions;
        var index = Grid.GetColumn(this);
        if (index < 1 || index + 1 >= columns.Count || columns.Count != _startWidths.Length) return;
        var left = _startWidths[index - 1];
        var right = _startWidths[index + 1];
        if (left + right < 2 * MinPaneWidth) return;
        delta = Math.Clamp(delta, MinPaneWidth - left, right - MinPaneWidth);
        for (var i = 0; i < columns.Count; i++)
        {
            if (!columns[i].Width.IsStar) continue;
            var width = i == index - 1 ? left + delta : i == index + 1 ? right - delta : _startWidths[i];
            columns[i].Width = new GridLength(Math.Max(width, 0.01), GridUnitType.Star);
        }
    }
}
