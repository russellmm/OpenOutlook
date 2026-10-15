using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class TextOverlayGeometryTests
{
    private static IReadOnlyList<BrowserHtmlRenderer.TextRun> Read(string json) =>
        BrowserHtmlRenderer.ReadTextRuns(json, 900, 3000);

    [Fact]
    public void KeepsWellFormedRunsFromTheBrowser()
    {
        var run = Assert.Single(Read("""[{"T":"Hi!","X":20,"Y":140,"H":22,"E":[0,8,12,16],"B":3,"C":1,"P":1,"S":0}]"""));
        Assert.Equal("Hi!", run.Text);
        Assert.Equal(20, run.X);
        Assert.Equal(140, run.Y);
        Assert.Equal(22, run.Height);
        Assert.Equal(new double[] { 0, 8, 12, 16 }, run.Edges);
        Assert.Equal(3, run.Block);
        Assert.True(run.Cell);
        Assert.True(run.HardBreak);
        Assert.False(run.SpaceBefore);
    }

    [Fact]
    public void HandlesAnEmptyDocumentWithoutThrowing()
    {
        Assert.Empty(Read("[]"));
        Assert.Empty(Read("null"));
        Assert.Empty(Read(""));
        Assert.Empty(Read("not json at all"));
    }

    [Theory]
    [InlineData("""[{"T":"x","X":NaN,"Y":1,"H":10,"E":[0,5]}]""")]            // non-finite from a broken layout
    [InlineData("""[{"T":"x","X":-5,"Y":1,"H":10,"E":[0,5]}]""")]             // off the left edge
    [InlineData("""[{"T":"x","X":1,"Y":-2,"H":10,"E":[0,5]}]""")]             // above the page
    [InlineData("""[{"T":"x","X":950,"Y":1,"H":10,"E":[0,5]}]""")]            // past the page width
    [InlineData("""[{"T":"x","X":1,"Y":4000,"H":10,"E":[0,5]}]""")]           // below the page
    [InlineData("""[{"T":"x","X":1,"Y":1,"H":5000,"E":[0,5]}]""")]            // taller than the page
    [InlineData("""[{"T":"x","X":1,"Y":1,"H":0,"E":[0,5]}]""")]               // zero height
    [InlineData("""[{"T":"xy","X":1,"Y":1,"H":10,"E":[0,5]}]""")]             // one edge short of the text
    [InlineData("""[{"T":"x","X":1,"Y":1,"H":10,"E":[0,5],"B":-1}]""")]       // impossible block number
    [InlineData("""[{"T":"","X":1,"Y":1,"H":10,"E":[0]}]""")]                 // empty
    [InlineData("""[{"T":" ","X":1,"Y":1,"H":10,"E":[0,0]}]""")]              // a collapsed space with no width
    public void RejectsGeometryThatCannotBeTrusted(string json) => Assert.Empty(Read(json));

    [Fact]
    public void ClampsEdgesToThePageRatherThanDroppingTheRun()
    {
        var run = Assert.Single(Read("""[{"T":"tail","X":880,"Y":100,"H":20,"E":[0,15,30,45,60]}]"""));
        Assert.Equal(20, run.Edges[^1]);   // 900 - 880
    }

    [Fact]
    public void CapsTextSoAPathologicalMessageCannotExhaustMemory()
    {
        var edges = string.Join(",", Enumerable.Range(0, 2001));
        var runs = string.Join(",", Enumerable.Range(0, 500)
            .Select(_ => $$"""{"T":"{{new string('a', 2000)}}","X":1,"Y":1,"H":10,"E":[{{edges}}]}"""));
        var read = BrowserHtmlRenderer.ReadTextRuns("[" + runs + "]", 1400, 3000);
        Assert.True(read.Sum(a => a.Text.Length) <= BrowserHtmlRenderer.MaximumOverlayTextCharacters);
        Assert.True(read.Count < 500, "the character budget should have stopped collection early");
    }

    [Fact]
    public void KeepsRunsInDocumentOrder()
    {
        var runs = Read("""[{"T":"first","X":1,"Y":10,"H":10,"E":[0,1,2,3,4,5]},{"T":"second","X":1,"Y":40,"H":10,"E":[0,1,2,3,4,5,6]}]""");
        Assert.Equal(new[] { "first", "second" }, runs.Select(a => a.Text));
    }
}
