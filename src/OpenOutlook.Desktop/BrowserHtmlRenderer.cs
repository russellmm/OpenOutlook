using System.Buffers.Binary;
using PuppeteerSharp;

namespace OpenOutlook.Desktop;

/// <summary>Renders an inert email document in a short-lived, network-blocked Chrome process.</summary>
public static class BrowserHtmlRenderer
{
    public sealed record LinkArea(string Url, double X, double Y, double Width, double Height);
    public sealed record RenderedDocument(IReadOnlyList<byte[]> Pages, IReadOnlyList<LinkArea> Links);
    public const int MaximumScreenshotBytes = 32 * 1024 * 1024;
    private const int MaximumHeight = 60_000;
    private const int TileHeight = 3_000;
    private const int MaximumTiles = 20;

    public static string? FindBrowser() => new[]
    {
        "/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser"
    }.FirstOrDefault(File.Exists);

    public static async Task<byte[]> RenderAsync(string safeHtml, int viewportWidth,
        CancellationToken cancellationToken = default)
    {
        var pages = await RenderPagesAsync(safeHtml, viewportWidth, cancellationToken);
        if (pages.Count != 1) throw new InvalidDataException("HTML message requires multiple preview pages.");
        return pages[0];
    }

    public static async Task<byte[]> RenderPdfAsync(string html, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (html.Length > 40 * 1024 * 1024) throw new InvalidDataException("Message is too large to print.");
        var executable = FindBrowser() ?? throw new NotSupportedException("Chrome or Chromium is required for PDF output.");
        ct.ThrowIfCancellationRequested();
        await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            ExecutablePath = executable, Headless = true, Timeout = 10_000,
            Args = ["--disable-background-networking", "--disable-extensions"]
        }).ConfigureAwait(false);
        await using var page = await browser.NewPageAsync().ConfigureAwait(false);
        page.DefaultTimeout = 10_000;
        await page.SetJavaScriptEnabledAsync(false).ConfigureAwait(false);
        await page.SetRequestInterceptionAsync(true).ConfigureAwait(false);
        page.Request += async (_, request) =>
        {
            try { await request.Request.AbortAsync().ConfigureAwait(false); }
            catch { /* The page can close while a request is in flight. */ }
        };
        await page.SetContentAsync(html, new SetContentOptions
        { WaitUntil = [WaitUntilNavigation.DOMContentLoaded], Timeout = 10_000 }).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var pdf = await page.PdfDataAsync(new PdfOptions { PrintBackground = true }).ConfigureAwait(false);
        if (pdf.Length is < 8 or > 48 * 1024 * 1024 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            throw new InvalidDataException("Browser returned an invalid or oversized PDF.");
        ct.ThrowIfCancellationRequested();
        return pdf;
    }

    public static async Task<IReadOnlyList<byte[]>> RenderPagesAsync(string safeHtml, int viewportWidth,
        CancellationToken cancellationToken = default, bool trustedOriginal = false)
        => (await RenderDocumentAsync(safeHtml, viewportWidth, cancellationToken, trustedOriginal)).Pages;

    public static async Task<RenderedDocument> RenderDocumentAsync(string safeHtml, int viewportWidth,
        CancellationToken cancellationToken = default, bool trustedOriginal = false)
    {
        ArgumentNullException.ThrowIfNull(safeHtml);
        if (safeHtml.Length > 40 * 1024 * 1024 || viewportWidth is < 400 or > 1400)
            throw new InvalidDataException("HTML preview exceeds the renderer limit.");
        var executable = FindBrowser() ?? throw new NotSupportedException("Chrome or Chromium is required for HTML layout.");
        cancellationToken.ThrowIfCancellationRequested();
        await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            ExecutablePath = executable,
            Headless = true,
            Timeout = 10_000,
            Args = ["--disable-background-networking", "--disable-extensions"]
        }).ConfigureAwait(false);
        await using var page = await browser.NewPageAsync().ConfigureAwait(false);
        page.DefaultTimeout = 10_000;
        await page.SetViewportAsync(new ViewPortOptions
        { Width = viewportWidth, Height = 200, DeviceScaleFactor = 1 }).ConfigureAwait(false);
        await page.SetJavaScriptEnabledAsync(trustedOriginal).ConfigureAwait(false);
        if (!trustedOriginal)
        {
            await page.SetRequestInterceptionAsync(true).ConfigureAwait(false);
            page.Request += async (_, request) =>
            {
                try { await request.Request.AbortAsync().ConfigureAwait(false); }
                catch { /* The page can close while a blocked request is in flight. */ }
            };
        }
        await page.SetContentAsync(safeHtml, new SetContentOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded], Timeout = trustedOriginal ? 20_000 : 10_000
        }).ConfigureAwait(false);
        if (trustedOriginal)
        {
            for (var attempt = 0; attempt < 24; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pending = await page.EvaluateFunctionAsync<int>(
                    "() => Array.from(document.images).filter(image => !image.complete).length")
                    .ConfigureAwait(false);
                if (pending == 0) break;
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var contentWidth = await page.EvaluateFunctionAsync<int>(
            "() => Math.max(document.documentElement.scrollWidth, document.body.scrollWidth)")
            .ConfigureAwait(false);
        if (contentWidth > viewportWidth)
        {
            if (contentWidth > 1400) throw new InvalidDataException("HTML layout is too wide to preview.");
            await page.SetViewportAsync(new ViewPortOptions
            { Width = contentWidth, Height = 200, DeviceScaleFactor = 1 }).ConfigureAwait(false);
        }
        var height = await page.EvaluateFunctionAsync<int>("() => Math.max(document.documentElement.scrollHeight, document.body.scrollHeight)")
            .ConfigureAwait(false);
        var width = Math.Max(viewportWidth, contentWidth);
        if (height is <= 0 or > MaximumHeight ||
            (long)height * width > 72_000_000 || (height + TileHeight - 1) / TileHeight > MaximumTiles)
            throw new InvalidDataException("HTML layout exceeds the preview size limit.");
        var linkJson = await page.EvaluateFunctionAsync<string>(
            "() => JSON.stringify(Array.from(document.querySelectorAll('a')).slice(0,500).flatMap(a => " +
            "Array.from(a.getClientRects()).slice(0,8).map(r => ({Url:a.getAttribute('data-openoutlook-url') || a.getAttribute('href') || ''," +
            "X:r.left+window.scrollX,Y:r.top+window.scrollY,Width:r.width,Height:r.height}))))")
            .ConfigureAwait(false);
        var browserLinks = System.Text.Json.JsonSerializer.Deserialize<BrowserLink[]>(linkJson) ?? [];
        var links = browserLinks.Where(link => SafeHtmlDocument.TryLink(link.Url, out _) &&
            double.IsFinite(link.X) && double.IsFinite(link.Y) && double.IsFinite(link.Width) &&
            double.IsFinite(link.Height) && link.X >= 0 && link.Y >= 0 && link.Width > 0 && link.Height > 0 &&
            link.X < width && link.Y < height && link.Width <= width && link.Height <= height)
            .Take(1000).Select(link => new LinkArea(link.Url, link.X, link.Y, link.Width, link.Height)).ToArray();
        var pages = new List<byte[]>();
        var totalBytes = 0L;
        for (var y = 0; y < height; y += TileHeight)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tileHeight = Math.Min(TileHeight, height - y);
            var png = await page.ScreenshotDataAsync(new ScreenshotOptions
            {
                Clip = new PuppeteerSharp.Media.Clip { X = 0, Y = y, Width = width, Height = tileHeight },
                CaptureBeyondViewport = true
            }).ConfigureAwait(false);
            var dimensions = ValidateScreenshot(png);
            if (dimensions.Width != width || dimensions.Height != tileHeight)
                throw new InvalidDataException("Browser returned an incomplete preview page.");
            totalBytes += png.Length;
            if (totalBytes > 64L * 1024 * 1024)
                throw new InvalidDataException("HTML preview images exceed the memory limit.");
            pages.Add(png);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new RenderedDocument(pages, links);
    }

    private sealed class BrowserLink
    {
        public string Url { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public static (int Width, int Height) ValidateScreenshot(ReadOnlySpan<byte> png)
    {
        if (png.Length is < 24 or > MaximumScreenshotBytes ||
            !png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !png[12..16].SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Browser returned an invalid preview image.");
        var width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png[16..20]));
        var height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png[20..24]));
        if (width is < 400 or > 1400 || height is <= 0 or > TileHeight ||
            (long)width * height > 4_200_000)
            throw new InvalidDataException("Browser preview image exceeds the size limit.");
        return (width, height);
    }
}
