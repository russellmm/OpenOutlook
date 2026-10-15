using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class SnapshotTextTests
{
    /// <summary>A run whose characters are each 10 px wide.</summary>
    private static BrowserHtmlRenderer.TextRun Run(string text, double x, double y, int block = 0, bool cell = false,
        bool hardBreak = false, bool spaceBefore = false) =>
        new(text, x, y, 20, Enumerable.Range(0, text.Length + 1).Select(i => i * 10.0).ToArray(), block, cell, hardBreak, spaceBefore);

    // A paragraph that wraps at a collapsed space, a second paragraph, and a table row of two cells.
    private static readonly SnapshotText Sample = new([
        Run("Hello world", 20, 100, block: 0),
        Run("again", 20, 120, block: 0, spaceBefore: true),
        Run("Next para", 20, 160, block: 1),
        Run("Cell A", 20, 200, block: 2, cell: true),
        Run("Cell B", 200, 200, block: 3, cell: true),
    ]);

    [Fact]
    public void JoinsRunsLikeACopyFromABrowser()
    {
        Assert.Equal("Hello world again\nNext para\nCell A\tCell B", Sample.Text);
    }

    [Fact]
    public void CaretLandsOnTheNearestCharacterBoundary()
    {
        Assert.Equal(0, Sample.CaretAt(21, 110));    // left edge of "H"
        Assert.Equal(1, Sample.CaretAt(32, 110));    // between "H" and "e"
        Assert.Equal(5, Sample.CaretAt(68, 110));    // after "Hello"
        Assert.Equal(11, Sample.CaretAt(800, 110));  // far right of the line: its end
        Assert.Equal(0, Sample.CaretAt(0, 110));     // left margin: the line's start
    }

    [Fact]
    public void CaretBetweenLinesGoesToTheNearestLine()
    {
        var offset = Sample.CaretAt(25, 155);        // in the gap, nearer "Next para"
        Assert.Equal(Sample.Text.IndexOf("Next", StringComparison.Ordinal), offset);
        Assert.Equal(-1, SnapshotText.Empty.CaretAt(10, 10));
    }

    [Fact]
    public void CopyReturnsTheExactCharacterRange()
    {
        var start = Sample.CaretAt(32, 110);         // after "H"
        var end = Sample.CaretAt(53, 125);           // after "aga"
        Assert.Equal("ello world aga", Sample.Copy(start, end));
        Assert.Equal("ello world aga", Sample.Copy(end, start));  // a backwards drag copies the same text
    }

    [Fact]
    public void CopyUsesPlatformLineEndingsAndTabsBetweenCells()
    {
        var text = Sample.Copy(Sample.Text.IndexOf("Next", StringComparison.Ordinal), Sample.Text.Length);
        Assert.Equal("Next para" + Environment.NewLine + "Cell A\tCell B", text);
    }

    [Fact]
    public void DoubleClickWordAndTripleClickParagraph()
    {
        var w = Sample.CaretAt(85, 110);             // inside "world"
        var (ws, we) = Sample.WordAt(w);
        Assert.Equal("world", Sample.Text[ws..we]);
        var (ps, pe) = Sample.BlockAt(w);
        Assert.Equal("Hello world again", Sample.Text[ps..pe]);
        var (cs, ce) = Sample.BlockAt(Sample.Text.IndexOf("Cell B", StringComparison.Ordinal) + 2);
        Assert.Equal("Cell B", Sample.Text[cs..ce]);
    }

    [Fact]
    public void WordAtTheEndOfAWordSelectsThatWord()
    {
        var (s, e) = Sample.WordAt(5);               // the boundary right after "Hello"
        Assert.Equal("Hello", Sample.Text[s..e]);
    }

    [Fact]
    public void HighlightsCoverOnlyTheSelectedCharactersOfEachLine()
    {
        var rects = Sample.Highlights(1, Sample.Text.IndexOf("again", StringComparison.Ordinal) + 2).ToList();
        Assert.Equal(2, rects.Count);
        Assert.Equal(new Avalonia.Rect(30, 100, 100, 20), rects[0]);   // "ello world"
        Assert.Equal(new Avalonia.Rect(20, 120, 20, 20), rects[1]);    // "ag"
        // Limited to one tile's band, only the second line remains.
        Assert.Single(Sample.Highlights(1, 15, top: 121, bottom: 3000));
    }

    [Fact]
    public void HardBreaksBecomeNewLinesAndWrappedWordsStayWhole()
    {
        var text = new SnapshotText([
            Run("Line one", 0, 0),
            Run("Line two", 0, 20, hardBreak: true),
            Run("https://example.test/very/long/", 0, 40, hardBreak: true),
            Run("path", 0, 60),                          // a link that wrapped without a space
        ]);
        Assert.Equal("Line one\nLine two\nhttps://example.test/very/long/path", text.Text);
    }

    [Fact]
    public void EmojiAreNeverSplit()
    {
        var text = new SnapshotText([
            new BrowserHtmlRenderer.TextRun("a\U0001F600b", 0, 0, 20, [0, 10, 20, 30, 40], 0, false, false, false)
        ]);
        Assert.Equal(1, text.CaretAt(21, 10));       // the boundary inside the pair snaps to before the emoji
        Assert.Equal("\U0001F600", text.Text[1..3]);
    }

    [Fact]
    public void SharedSelectionReportsChanges()
    {
        var selection = new SnapshotSelection(Sample);
        var changes = 0;
        selection.Changed += () => changes++;
        Assert.True(selection.IsEmpty);
        selection.Set(0, 5);
        Assert.Equal("Hello", selection.SelectedText);
        selection.Set(0, 5);                         // no change, no repaint
        selection.SelectAll();
        Assert.Equal(Sample.Copy(0, Sample.Text.Length), selection.SelectedText);
        selection.Clear();
        Assert.True(selection.IsEmpty);
        Assert.Equal(3, changes);
    }
}
