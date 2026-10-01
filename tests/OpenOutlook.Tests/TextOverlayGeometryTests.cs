using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class TextOverlayGeometryTests
{
    private static IReadOnlyList<BrowserHtmlRenderer.TextArea> Read(string json) =>
        BrowserHtmlRenderer.ReadTextAreas(json, 900, 3000);

    [Fact]
    public void KeepsWellFormedBlocksFromTheBrowser()
    {
        var areas = Read("""[{"Text":"Your balance is ready.","X":20,"Y":140,"Width":300,"Height":22}]""");
        var area = Assert.Single(areas);
        Assert.Equal("Your balance is ready.", area.Text);
        Assert.Equal(20, area.X);
        Assert.Equal(140, area.Y);
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
    [InlineData("""[{"Text":"x","X":NaN,"Y":1,"Width":10,"Height":10}]""")]           // non-finite from a broken layout
    [InlineData("""[{"Text":"x","X":-5,"Y":1,"Width":10,"Height":10}]""")]            // off the left edge
    [InlineData("""[{"Text":"x","X":1,"Y":-2,"Width":10,"Height":10}]""")]            // above the page
    [InlineData("""[{"Text":"x","X":950,"Y":1,"Width":10,"Height":10}]""")]           // past the page width
    [InlineData("""[{"Text":"x","X":1,"Y":4000,"Width":10,"Height":10}]""")]          // below the page
    [InlineData("""[{"Text":"x","X":1,"Y":1,"Width":5000,"Height":10}]""")]           // wider than the page
    [InlineData("""[{"Text":"x","X":1,"Y":1,"Width":0,"Height":10}]""")]              // zero width
    [InlineData("""[{"Text":"   ","X":1,"Y":1,"Width":10,"Height":10}]""")]           // whitespace only
    [InlineData("""[{"Text":"","X":1,"Y":1,"Width":10,"Height":10}]""")]              // empty
    public void RejectsGeometryThatCannotBeTrusted(string json) => Assert.Empty(Read(json));

    [Fact]
    public void CapsTextSoAPathologicalMessageCannotExhaustMemory()
    {
        var blocks = string.Join(",", Enumerable.Range(0, 500)
            .Select(_ => $$"""{"Text":"{{new string('a', 2000)}}","X":1,"Y":1,"Width":10,"Height":10}"""));
        var areas = Read("[" + blocks + "]");
        Assert.True(areas.Sum(a => a.Text.Length) <= BrowserHtmlRenderer.MaximumOverlayTextCharacters);
        Assert.True(areas.Count < 500, "the character budget should have stopped collection early");
    }

    [Fact]
    public void ClampsWidthAndHeightToThePageRatherThanDroppingTheBlock()
    {
        var area = Assert.Single(Read("""[{"Text":"tail","X":880,"Y":100,"Width":60,"Height":20}]"""));
        Assert.Equal(20, area.Width);   // 900 - 880
        Assert.Equal(20, area.Height);
    }

    [Fact]
    public void KeepsBlocksInDocumentOrder()
    {
        var areas = Read("""[{"Text":"first","X":1,"Y":10,"Width":100,"Height":10},{"Text":"second","X":1,"Y":40,"Width":100,"Height":10}]""");
        Assert.Equal(new[] { "first", "second" }, areas.Select(a => a.Text));
    }
}
