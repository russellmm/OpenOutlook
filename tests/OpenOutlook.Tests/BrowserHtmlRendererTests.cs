using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class BrowserHtmlRendererTests
{
    [Fact]
    public async Task MeasuresClickableLinksWithoutNavigating()
    {
        if (BrowserHtmlRenderer.FindBrowser() is null) return;
        var safe = SafeHtmlDocument.Build("<body><a href='https://example.test/read'>Read this</a>" +
            "<a href='javascript:alert(1)'>Unsafe</a></body>", new Dictionary<string, byte[]>());
        var rendered = await BrowserHtmlRenderer.RenderDocumentAsync(safe, 640);
        Assert.NotEmpty(rendered.Pages);
        var link = Assert.Single(rendered.Links);
        Assert.Equal("https://example.test/read", link.Url);
        Assert.True(link.Width > 0 && link.Height > 0);
        var pdf = await BrowserHtmlRenderer.RenderPdfAsync(safe);
        Assert.True(pdf.Length > 100);
        Assert.True(pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8));
    }

    /// <summary>Owner 2026-10-30: every message looked soft on the 2.74x screen because the picture was taken at 1x and stretched. It is now
    /// taken at the screen's scale; the layout, links and text positions stay in CSS pixels; very long mail gets a lower scale, not a refusal.</summary>
    [Fact]
    public async Task PicturesAreTakenAtTheScreensScaleWithTheSameLayout()
    {
        if (BrowserHtmlRenderer.FindBrowser() is null) return;
        var safe = SafeHtmlDocument.Build("<body><p>Sharp <a href='https://example.test/x'>text</a> please</p></body>", new Dictionary<string, byte[]>());
        var flat = await BrowserHtmlRenderer.RenderDocumentAsync(safe, 640);
        var sharp = await BrowserHtmlRenderer.RenderDocumentAsync(safe, 640, screenScale: 2.74);
        Assert.Equal(1, flat.Scale);
        Assert.Equal(2.74, sharp.Scale, 3);
        var (w1, h1) = BrowserHtmlRenderer.ValidateScreenshot(flat.Pages[0]);
        var (w2, h2) = BrowserHtmlRenderer.ValidateScreenshot(sharp.Pages[0], sharp.Scale);
        Assert.Equal(640, w1);
        Assert.InRange(w2, 1752, 1755);                                        // 640 x 2.74 device pixels
        Assert.InRange(h2, h1 * 2.74 - 2, h1 * 2.74 + 2);
        Assert.Equal(flat.Links.Single(), sharp.Links.Single());               // links and text stay in CSS pixels
        Assert.Equal(flat.Text.Select(r => (r.Text, r.X, r.Y)), sharp.Text.Select(r => (r.Text, r.X, r.Y)));

        Assert.Equal(2.74, BrowserHtmlRenderer.EffectiveScale(2.74, 800, 5_000), 3);
        Assert.Equal(1.94, BrowserHtmlRenderer.EffectiveScale(2.74, 800, 20_000), 2);   // a long newsletter stays within the pixel budget
        Assert.Equal(1, BrowserHtmlRenderer.EffectiveScale(2.74, 1400, 60_000));
        Assert.Equal(3, BrowserHtmlRenderer.EffectiveScale(4, 600, 1_000));
        Assert.Equal(1, BrowserHtmlRenderer.EffectiveScale(0.5, 600, 1_000));
    }

    [Fact]
    public async Task MeasuresEveryCharacterForSelection()
    {
        if (BrowserHtmlRenderer.FindBrowser() is null) return;
        var safe = SafeHtmlDocument.Build(
            "<body><p>Hello   <b>bold</b> world, this line is long enough to wrap in a narrow column of text ok.</p>" +
            "<p style='display:none'>hidden preheader</p><p>Line one<br>Line two</p>" +
            "<table><tr><td>Cell A</td><td>Cell B</td></tr></table><pre>a  b\nc</pre></body>",
            new Dictionary<string, byte[]>());
        var rendered = await BrowserHtmlRenderer.RenderDocumentAsync(safe, 400);
        var text = new SnapshotText(rendered.Text);
        Assert.Contains("Hello bold world, this line", text.Text);           // collapsed spaces become one
        Assert.DoesNotContain("hidden preheader", text.Text);
        Assert.Contains("Line one\nLine two", text.Text);
        Assert.Contains("Cell A\tCell B", text.Text);
        Assert.Contains("a  b\nc", text.Text);                               // preformatted spacing is kept
        Assert.Contains("column of text ok.", text.Text.Replace("\n", " "));
        Assert.True(rendered.Text.Count(r => r.Text.Contains("wrap") || r.Text.Contains("long")) >= 1);
        Assert.True(rendered.Text.Select(r => r.Y).Distinct().Count() >= 5, "the long paragraph should wrap over several lines");
        foreach (var run in rendered.Text)
        {
            Assert.Equal(run.Text.Length + 1, run.Edges.Count);
            Assert.True(run.Edges[^1] > run.Edges[0]);
        }
        // A caret placed on the middle of "bold" lands inside that word.
        var bold = rendered.Text.First(r => r.Text.Contains("bold"));
        var i = bold.Text.IndexOf("bold", StringComparison.Ordinal);
        var x = bold.X + (bold.Edges[i + 1] + bold.Edges[i + 2]) / 2;
        var caret = text.CaretAt(x, bold.Y + bold.Height / 2);
        var (s, e) = text.WordAt(caret);
        Assert.Equal("bold", text.Text[s..e]);
    }

    private static byte[] RedPng(int size)
    {
        using var bitmap = new SkiaSharp.SKBitmap(size, size);
        bitmap.Erase(SkiaSharp.SKColors.Red);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        return image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>Owner 2026-11-02 (Ulta Lab Tests): pictures were written into the document as base64, once per img element, and a picture-heavy mailing
    /// passed the renderer's 40 MB limit ("Full HTML layout is unavailable"). The snapshot document now links each distinct picture once and the renderer
    /// answers those links from memory; every other request is still refused.</summary>
    [Fact]
    public void SnapshotDocumentLinksEachPictureOnceInsteadOfItsBase64()
    {
        var png = RedPng(40);
        var html = string.Concat(Enumerable.Repeat("<img src='cid:logo' width='40' height='40'>", 30)) +
            "<div style=\"background-image:url('cid:logo')\">x</div><table background='cid:logo'><tr><td>y</td></tr></table>" +
            "<img src='https://tracker.example.test/open.gif'>";
        var snapshot = SafeHtmlDocument.BuildSnapshot(html, new Dictionary<string, byte[]> { ["cid:logo"] = png });
        var picture = Assert.Single(snapshot.Pictures);
        Assert.Equal("image/png", picture.MimeType);
        Assert.DoesNotContain("base64", snapshot.Html);
        Assert.DoesNotContain("tracker.example.test", snapshot.Html);
        Assert.Equal(32, snapshot.Html.Split(SafeHtmlDocument.SnapshotPictureUrl + "0").Length - 1);   // 30 img + style + table background
        Assert.Contains("img-src data: " + SafeHtmlDocument.SnapshotPictureUrl + ";", snapshot.Html);
        Assert.Same(picture, BrowserHtmlRenderer.SnapshotPictureFor(SafeHtmlDocument.SnapshotPictureUrl + "0", snapshot.Pictures));
        Assert.Null(BrowserHtmlRenderer.SnapshotPictureFor(SafeHtmlDocument.SnapshotPictureUrl + "1", snapshot.Pictures));
        Assert.Null(BrowserHtmlRenderer.SnapshotPictureFor(SafeHtmlDocument.SnapshotPictureUrl + "-1", snapshot.Pictures));
        Assert.Null(BrowserHtmlRenderer.SnapshotPictureFor("https://pictures.openoutlook.invalid.example.test/0", snapshot.Pictures));
        Assert.Null(BrowserHtmlRenderer.SnapshotPictureFor(SafeHtmlDocument.SnapshotPictureUrl + "0", null));
    }

    [Fact]
    public async Task SnapshotPicturesAreDrawnAndTheFirstScreenComesFirst()
    {
        if (BrowserHtmlRenderer.FindBrowser() is null) return;
        var html = "<body style='margin:0'><img src='cid:logo' width='100' height='100' style='display:block'>" +
            "<div style='height:5000px'>long</div><img src='https://tracker.example.test/open.gif'></body>";
        var snapshot = SafeHtmlDocument.BuildSnapshot(html, new Dictionary<string, byte[]> { ["cid:logo"] = RedPng(100) });
        BrowserHtmlRenderer.RenderedDocument? first = null;
        var rendered = await BrowserHtmlRenderer.RenderDocumentAsync(snapshot.Html, 640, screenScale: 2, pictures: snapshot.Pictures,
            firstScreenHeight: 700, firstScreen: document => first = document);
        Assert.NotNull(first);
        Assert.Single(first!.Pages);
        Assert.Equal(first.Pages[0], rendered.Pages[0]);
        Assert.Equal(1400, BrowserHtmlRenderer.ValidateScreenshot(rendered.Pages[0], 2).Height);       // 700 CSS pixels at 2x
        Assert.Equal(3, rendered.Pages.Count);                                                        // 700 + 3000 + the rest of ~5120
        Assert.InRange(rendered.Pages.Sum(p => BrowserHtmlRenderer.ValidateScreenshot(p, 2).Height), 10_200, 10_300);   // 5100 px + the alt text line
        using var tile = SkiaSharp.SKBitmap.Decode(rendered.Pages[0]);
        Assert.Equal(SkiaSharp.SKColors.Red, tile.GetPixel(100, 100));                               // the picture came from memory

        var again = await BrowserHtmlRenderer.RenderDocumentAsync(snapshot.Html, 640, pictures: snapshot.Pictures);   // the shared browser is reused
        Assert.Equal(2, again.Pages.Count);
        Assert.Contains("start", again.Timings);
    }
}
