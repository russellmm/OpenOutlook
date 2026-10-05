using System.Buffers.Binary;
using PuppeteerSharp;

namespace OpenOutlook.Desktop;

/// <summary>Renders an inert email document in a short-lived, network-blocked Chrome process.</summary>
public static class BrowserHtmlRenderer
{
    public sealed record LinkArea(string Url, double X, double Y, double Width, double Height);

    /// <summary>
    /// One selectable block of text and the rectangle Chrome laid it out in. The snapshot reader is a
    /// bitmap, so nothing in it can be highlighted; these rectangles let the reading pane place an
    /// invisible selectable control over each block and recover selection without embedding a browser
    /// (which cannot composite into an Avalonia window under Wayland). Granularity is deliberately the
    /// element -- paragraph, list item, table cell -- because mapping substrings of one text node to
    /// the several line boxes it may span needs font metrics we do not share with Chrome.
    /// </summary>
    public sealed record TextArea(string Text, double X, double Y, double Width, double Height);

    public sealed record RenderedDocument(IReadOnlyList<byte[]> Pages, IReadOnlyList<LinkArea> Links,
        IReadOnlyList<TextArea> Text);
    public const int MaximumScreenshotBytes = 32 * 1024 * 1024;
    private const int MaximumHeight = 60_000;
    private const int TileHeight = 3_000;
    private const int MaximumTiles = 20;

    /// <summary>
    /// A Chromium-based browser to lay messages out with: OPENOUTLOOK_BROWSER, else Edge / Chrome on Windows (Edge ships with
    /// Windows 10 and 11), Chrome / Edge / Chromium on macOS, Chrome / Chromium / Edge on Linux. Null when none is installed (the
    /// reading pane then falls back to the plain text-run preview).
    /// </summary>
    /// <summary>Every installed Chromium-based browser, the forced one first. The layout tries them in turn: a browser that is installed can still refuse to start headless (an Edge update did).</summary>
    internal static IReadOnlyList<string> ExistingBrowsers()
    {
        var list = new List<string>();
        var forced = Environment.GetEnvironmentVariable("OPENOUTLOOK_BROWSER");
        if (!string.IsNullOrWhiteSpace(forced) && File.Exists(forced)) list.Add(forced);
        foreach (var c in Candidates()) if (File.Exists(c) && !list.Contains(c, StringComparer.OrdinalIgnoreCase)) list.Add(c);
        return list;
    }

    public static string? FindBrowser()
    {
        var forced = Environment.GetEnvironmentVariable("OPENOUTLOOK_BROWSER");
        if (!string.IsNullOrWhiteSpace(forced) && File.Exists(forced)) return forced;
        return Candidates().FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("LocalAppData")
            })
            {
                if (string.IsNullOrEmpty(root)) continue;
                yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
                yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
                yield return Path.Combine(root, "Chromium", "Application", "chrome.exe");
                yield return Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
            yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
        }
        else
        {
            foreach (var path in new[]
            {
                "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium", "/usr/bin/chromium-browser",
                "/snap/bin/chromium", "/usr/bin/microsoft-edge", "/usr/bin/microsoft-edge-stable"
            }) yield return path;
        }
    }

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
        await using var browser = await BrowserProcessTracker.LaunchAsync(executable).ConfigureAwait(false);
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
        await using var browser = await BrowserProcessTracker.LaunchAsync(executable).ConfigureAwait(false);
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
        // Text geometry for the selectable overlay. Runs over every element and keeps those holding
        // direct text, unioning the client rectangles of that element's own text nodes so nested
        // markup (a bold word inside a paragraph) becomes its own block rather than being counted
        // twice. Hidden elements return no rectangles and drop out. This reads layout only; it does
        // not enable scripting in the document, which stays off for untrusted mail.
        var textJson = await page.EvaluateFunctionAsync<string>(
            "() => JSON.stringify((()=>{const out=[];if(!document.body)return out;" +
            "const walker=document.createTreeWalker(document.body,NodeFilter.SHOW_ELEMENT);let e;" +
            "while((e=walker.nextNode())&&out.length<4000){let text='';let x=Infinity,y=Infinity," +
            "right=-Infinity,bottom=-Infinity,found=false;" +
            "for(const n of Array.from(e.childNodes)){if(n.nodeType!==3)continue;const t=n.nodeValue;" +
            "if(!t||!t.trim())continue;const range=document.createRange();range.selectNodeContents(n);" +
            "for(const rect of Array.from(range.getClientRects())){if(rect.width<1||rect.height<1)continue;" +
            "found=true;if(rect.left<x)x=rect.left;if(rect.top<y)y=rect.top;" +
            "if(rect.right>right)right=rect.right;if(rect.bottom>bottom)bottom=rect.bottom;}" +
            "text+=' '+t.replace(/\\s+/g,' ');}" +
            "if(!found)continue;out.push({Text:text.trim(),X:x+window.scrollX,Y:y+window.scrollY," +
            "Width:right-x,Height:bottom-y});}" +
            "return out;})())")
            .ConfigureAwait(false);
        var textAreas = ReadTextAreas(textJson, width, height);
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
        return new RenderedDocument(pages, links, textAreas);
    }

    private sealed class BrowserLink
    {
        public string Url { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    private sealed class BrowserTextBlock
    {
        public string Text { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    /// <summary>Maximum characters of message text the selectable overlay will carry.</summary>
    public const int MaximumOverlayTextCharacters = 400_000;

    /// <summary>
    /// Validates the geometry Chrome reported before anything is placed on screen. Rectangles come
    /// from a rendered untrusted document, so sizes are bounded and non-finite or off-page entries
    /// are dropped rather than trusted; text is capped so a pathological message cannot balloon the
    /// reading pane's memory.
    /// </summary>
    public static IReadOnlyList<TextArea> ReadTextAreas(string json, int pageWidth, int pageHeight)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return [];
        BrowserTextBlock[] blocks;
        try { blocks = System.Text.Json.JsonSerializer.Deserialize<BrowserTextBlock[]>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }

        var result = new List<TextArea>();
        var totalCharacters = 0;
        foreach (var block in blocks)
        {
            var text = block.Text?.Trim() ?? "";
            if (text.Length == 0 || text.Length > 20_000) continue;
            if (!double.IsFinite(block.X) || !double.IsFinite(block.Y) ||
                !double.IsFinite(block.Width) || !double.IsFinite(block.Height)) continue;
            if (block.X < 0 || block.Y < 0 || block.Width < 1 || block.Height < 2) continue;
            if (block.X >= pageWidth || block.Y >= pageHeight) continue;
            if (block.Width > pageWidth || block.Height > pageHeight) continue;
            totalCharacters += text.Length;
            if (totalCharacters > MaximumOverlayTextCharacters) break;
            result.Add(new TextArea(text, block.X, block.Y,
                Math.Min(block.Width, pageWidth - block.X), Math.Min(block.Height, pageHeight - block.Y)));
            if (result.Count >= 4000) break;
        }
        return result;
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
