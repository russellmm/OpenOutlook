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
}
