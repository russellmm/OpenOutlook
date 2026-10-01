using Avalonia;
using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class TextSelectionGeometryTests
{
    private static readonly Rect[] ParagraphThenCells =
    [
        new(20, 100, 400, 20),   // 0: paragraph line one
        new(20, 130, 380, 20),   // 1: paragraph line two
        new(500, 130, 100, 20),  // 2: cell to the right of line two
    ];

    private static readonly string[] Texts = ["first line", "second line", "right cell"];

    [Fact]
    public void PressInsideABlockSelectsIt()
    {
        var selection = TextSelectionGeometry.Select(ParagraphThenCells, Texts,
            new TextSelectionGeometry.Drag(ParagraphThenCells[1], ParagraphThenCells[1]));
        Assert.Equal(new[] { 1 }, selection.BlockIndexes);
        Assert.Equal("second line", selection.Text);
    }

    [Fact]
    public void DragAcrossLinesSelectsThemInReadingOrder()
    {
        var selection = TextSelectionGeometry.Select(ParagraphThenCells, Texts,
            new TextSelectionGeometry.Drag(ParagraphThenCells[0], ParagraphThenCells[2]));
        Assert.Equal(new[] { 0, 1, 2 }, selection.BlockIndexes);
        Assert.Equal("first line second line right cell", selection.Text);
    }

    [Fact]
    public void ReadingOrderComesFromCoordinatesNotDocumentIndex()
    {
        // A table can put a later cell above an earlier one; the copied text has to read top-down.
        Rect[] blocks = [new(20, 300, 100, 20), new(20, 100, 100, 20)];
        var selection = TextSelectionGeometry.Select(blocks, new[] { "lower", "upper" },
            new TextSelectionGeometry.Drag(new Rect(20, 100, 100, 220), new Rect(20, 300, 100, 20)));
        Assert.Equal("upper lower", selection.Text);
    }

    [Fact]
    public void ClickInsideOverlappingBlocksPicksTheSmallest()
    {
        // A bold word block sits inside its paragraph's rectangle.
        Rect[] blocks = [new(20, 100, 400, 20), new(120, 100, 60, 20)];
        Assert.Equal(1, TextSelectionGeometry.BlockAt(blocks, 140, 110));
        Assert.Equal(0, TextSelectionGeometry.BlockAt(blocks, 30, 110));
    }

    [Fact]
    public void PressOutsideEveryBlockSelectsNothing()
    {
        Assert.Equal(-1, TextSelectionGeometry.BlockAt(ParagraphThenCells, 5, 5));
        var selection = TextSelectionGeometry.Select(ParagraphThenCells, Texts,
            new TextSelectionGeometry.Drag(new Rect(5, 5, 1, 1), new Rect(5, 5, 1, 1)));
        Assert.True(selection.BlockIndexes.Count == 0);
        Assert.Equal("", selection.Text);
    }

    [Fact]
    public void SelectAllReturnsEveryBlockInReadingOrder()
    {
        var selection = TextSelectionGeometry.SelectAll(ParagraphThenCells, Texts);
        Assert.Equal("first line second line right cell", selection.Text);
        Assert.Equal(3, selection.BlockIndexes.Count);
    }

    [Fact]
    public void MismatchedBlockAndTextCountsYieldNothingRatherThanThrowing()
    {
        Assert.True(TextSelectionGeometry.SelectAll([new Rect(0, 0, 10, 10)], ["a", "b"]).BlockIndexes.Count == 0);
        Assert.True(TextSelectionGeometry.Select([], [],
            new TextSelectionGeometry.Drag(new Rect(0, 0, 5, 5), new Rect(9, 9, 5, 5)))
            .BlockIndexes.Count == 0);
    }

    [Fact]
    public void SweepCoversBothEndsRegardlessOfDragDirection()
    {
        var downRight = TextSelectionGeometry.Sweep(new Rect(100, 300, 50, 20), new Rect(20, 100, 50, 20));
        Assert.Equal(20, downRight.X);
        Assert.Equal(100, downRight.Y);
        Assert.Equal(150, downRight.Right);   // far edge of the first rectangle
        Assert.Equal(320, downRight.Bottom);  // far edge of the first rectangle
        Assert.True(downRight.Contains(new Point(140, 310)));
        Assert.False(downRight.Contains(new Point(160, 310)));
    }

    [Fact]
    public void TextIsTrimmedWhenJoinedSoBlocksDoNotRunTogether()
    {
        Rect[] blocks = [new(0, 0, 50, 20), new(0, 30, 50, 20)];
        var selection = TextSelectionGeometry.Select(blocks, new[] { "  hello  ", "world"},
            new TextSelectionGeometry.Drag(new Rect(0, 0, 50, 50), new Rect(0, 30, 50, 20)));
        Assert.Equal("hello world", selection.Text);
    }
}
