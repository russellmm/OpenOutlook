using System.Buffers.Binary;
using PuppeteerSharp;

namespace OpenOutlook.Desktop;

/// <summary>Renders an inert email document in a short-lived, network-blocked Chrome process.</summary>
public static class BrowserHtmlRenderer
{
    public sealed record LinkArea(string Url, double X, double Y, double Width, double Height);

    /// <summary>
    /// One line's worth of one text node, as Chrome laid it out: the text (collapsed whitespace already removed), the line box
    /// (<see cref="X"/>, <see cref="Y"/>, <see cref="Height"/>) and the x position of every character boundary
    /// (<see cref="Edges"/>, relative to <see cref="X"/>, one more than the text's length). The snapshot reader is a bitmap; these
    /// runs let it select by character, word and line the way a browser does, while everything is still drawn by Avalonia (the
    /// embedded web view cannot composite into an Avalonia window under Wayland). <see cref="Block"/> numbers the nearest
    /// block-level element (paragraph, list item, table cell; <see cref="Cell"/> when it is a cell) so copied text gets line breaks
    /// and tabs; <see cref="HardBreak"/> means a &lt;br&gt; or a preformatted newline came first, <see cref="SpaceBefore"/> that
    /// whitespace collapsed away just before it.
    /// </summary>
    public sealed record TextRun(string Text, double X, double Y, double Height, IReadOnlyList<double> Edges,
        int Block, bool Cell, bool HardBreak, bool SpaceBefore);

    /// <summary>The tiles are PNGs at <paramref name="Scale"/> device pixels per CSS pixel; links and text runs are in CSS pixels.</summary>
    public sealed record RenderedDocument(IReadOnlyList<byte[]> Pages, IReadOnlyList<LinkArea> Links,
        IReadOnlyList<TextRun> Text, double Scale = 1);

    /// <summary>Device pixels of one message rendering at most (about 240 MB decoded): very long mail is drawn at a lower scale, never refused for it.</summary>
    private const double MaximumDevicePixels = 60_000_000;

    /// <summary>The scale a document of this CSS size is drawn at: the screen's (capped at 3), lowered to stay within <see cref="MaximumDevicePixels"/>.</summary>
    public static double EffectiveScale(double screenScale, int width, int height)
    {
        var scale = double.IsFinite(screenScale) ? Math.Clamp(screenScale, 1, 3) : 1;
        var budget = Math.Sqrt(MaximumDevicePixels / Math.Max(1d, (double)width * height));
        return Math.Max(1, Math.Min(scale, budget));
    }
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

    /// <summary>The headless Chromium shipped with the application (a "chromium" folder beside it), if this build has one.</summary>
    internal static IEnumerable<string> BundledBrowsers()
    {
        var name = OperatingSystem.IsWindows() ? "chrome-headless-shell.exe" : "chrome-headless-shell";
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath ?? "") })
            if (!string.IsNullOrEmpty(dir)) yield return Path.Combine(dir, "chromium", name);
    }

    private static IEnumerable<string> Candidates()
    {
        foreach (var bundled in BundledBrowsers()) yield return bundled;               // the pinned copy first: it does not depend on what is installed
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

    /// <summary>
    /// Lays the message out at <paramref name="viewportWidth"/> CSS pixels and takes the pictures at <paramref name="screenScale"/> (the window's render
    /// scaling): a 1x picture stretched to a HiDPI screen made every message soft (owner 2026-10-30, 2.74x on the bare-metal box).
    /// </summary>
    public static async Task<RenderedDocument> RenderDocumentAsync(string safeHtml, int viewportWidth,
        CancellationToken cancellationToken = default, bool trustedOriginal = false, double screenScale = 1)
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
        // Character geometry for the selectable overlay (TextRunScript). This reads layout only; it does not enable scripting in
        // the document, which stays off for untrusted mail.
        var textJson = await page.EvaluateFunctionAsync<string>(TextRunScript).ConfigureAwait(false);
        var textRuns = ReadTextRuns(textJson, width, height);
        var scale = EffectiveScale(screenScale, width, height);
        if (scale > 1)
            await page.SetViewportAsync(new ViewPortOptions
            { Width = width, Height = 200, DeviceScaleFactor = scale }).ConfigureAwait(false);       // the layout stays in CSS pixels
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
            var dimensions = ValidateScreenshot(png, scale);
            if (Math.Abs(dimensions.Width - width * scale) > 1.5 || Math.Abs(dimensions.Height - tileHeight * scale) > 1.5)
                throw new InvalidDataException("Browser returned an incomplete preview page.");
            totalBytes += png.Length;
            if (totalBytes > 192L * 1024 * 1024)
                throw new InvalidDataException("HTML preview images exceed the memory limit.");
            pages.Add(png);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new RenderedDocument(pages, links, textRuns, scale);
    }

    private sealed class BrowserLink
    {
        public string Url { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    private sealed class BrowserTextRun
    {
        public string T { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double H { get; set; }
        public double[] E { get; set; } = [];
        public int B { get; set; }
        public int C { get; set; }
        public int P { get; set; }
        public int S { get; set; }
    }

    /// <summary>Maximum characters of message text the selectable overlay will carry.</summary>
    public const int MaximumOverlayTextCharacters = 400_000;

    /// <summary>Maximum number of text runs (about one per line per text node) the overlay will carry.</summary>
    public const int MaximumOverlayRuns = 60_000;

    /// <summary>
    /// Measures every visible character of the laid-out document and returns JSON runs (see <see cref="TextRun"/>): one run per
    /// text node per line, with each character's boundaries. Whitespace that CSS collapses is dropped (and remembered as "S"),
    /// &lt;br&gt; and preformatted newlines become "P", hidden text (no boxes, visibility hidden, opacity 0) is skipped. A
    /// per-character Range is exact but costs time on very long mail, so after 2.5 s the rest is measured per word and the
    /// word's width shared out between its characters.
    /// </summary>
    internal const string TextRunScript = """
        () => {
          const out = [];
          if (!document.body) return '[]';
          const started = performance.now();
          let total = 0;
          const styles = new Map();
          const style = e => { let v = styles.get(e); if (!v) { v = getComputedStyle(e); styles.set(e, v); } return v; };
          const blocks = new Map();
          let nextBlock = 0;
          const blockOf = e => {
            let b = e;
            while (b && b !== document.body && style(b).display.startsWith('inline')) b = b.parentElement;
            if (!b) b = document.body;
            let v = blocks.get(b);
            if (v === undefined) { v = [nextBlock++, style(b).display === 'table-cell' ? 1 : 0]; blocks.set(b, v); }
            return v;
          };
          const range = document.createRange();
          const boxOf = (n, i, k) => {
            range.setStart(n, i); range.setEnd(n, i + k);
            const rects = range.getClientRects();
            for (let j = 0; j < rects.length; j++) if (rects[j].height > 0) return rects[j];
            return null;
          };
          const r1 = v => Math.round(v * 10) / 10;
          const skipTags = new Set(['STYLE', 'SCRIPT', 'NOSCRIPT', 'TEMPLATE', 'TITLE', 'TEXTAREA', 'SELECT', 'OPTION']);
          let run = null, hardBreak = false, pendingSpace = false, lastEndsSpace = true;
          const flush = () => {
            if (run && run.T.length) {
              out.push(run); total += run.T.length;
              lastEndsSpace = /\s$/.test(run.T);
            }
            run = null;
          };
          const emit = (text, left, right, top, height, block, cell) => {
            const mid = top + height / 2;
            if (run && (run.B !== block || mid < run.Y || mid > run.Y + run.H)) flush();
            if (!run) {
              run = { T: '', X: left, Y: top, H: height, E: [0], B: block, C: cell,
                      P: hardBreak ? 1 : 0, S: pendingSpace && !lastEndsSpace ? 1 : 0, L: left };
              hardBreak = false;
            }
            pendingSpace = false;
            if (top < run.Y) { run.H += run.Y - top; run.Y = top; }
            if (top + height > run.Y + run.H) run.H = top + height - run.Y;
            run.T += text;
            if (text.length === 2) run.E.push(r1(left - run.L));
            run.E.push(r1(right - run.L));
          };
          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT);
          let node;
          while ((node = walker.nextNode()) && total < 400000 && out.length < 60000) {
            if (node.nodeType === 1) { if (node.tagName === 'BR') { flush(); hardBreak = true; } continue; }
            const p = node.parentElement, s = node.nodeValue;
            if (!p || !s || skipTags.has(p.tagName)) continue;
            range.selectNodeContents(node);
            if (range.getClientRects().length === 0) continue;
            const cs = style(p);
            if (cs.visibility !== 'visible' || cs.opacity === '0') continue;
            const ws = cs.whiteSpace;
            const pre = ws.startsWith('pre') || ws === 'break-spaces';
            const [block, cell] = blockOf(p);
            const fast = performance.now() - started > 2500;
            const sx = window.scrollX, sy = window.scrollY;
            let prevSpace = false;
            for (let i = 0; i < s.length;) {
              const c = s.charCodeAt(i);
              const k = c >= 0xD800 && c < 0xDC00 && i + 1 < s.length ? 2 : 1;
              const ch = s.substr(i, k);
              if (pre && (ch === '\n' || ch === '\r')) { if (ch === '\n') { flush(); hardBreak = true; } i += 1; continue; }
              const space = ch === ' ' || ch === '\t' || ch === '\n' || ch === '\r' || ch === '\f';
              if (space && !pre && prevSpace) { i += k; continue; }
              if (fast && !space) {
                let j = i;
                while (j < s.length && !/\s/.test(s[j])) j++;
                range.setStart(node, i); range.setEnd(node, j);
                const rects = Array.from(range.getClientRects()).filter(q => q.height > 0);
                if (rects.length === 1 && j - i > 1) {
                  const q = rects[0], w = q.width / (j - i);
                  for (let u = i; u < j;) {
                    const cu = s.charCodeAt(u);
                    const ku = cu >= 0xD800 && cu < 0xDC00 && u + 1 < j ? 2 : 1;
                    emit(s.substr(u, ku), q.left + sx + (u - i) * w, q.left + sx + (u - i + ku) * w, q.top + sy, q.height, block, cell);
                    u += ku;
                  }
                  prevSpace = false;
                  i = j;
                  continue;
                }
              }
              const box = boxOf(node, i, k);
              if (!box || (space && !pre && box.width < 0.01)) {
                if (space) { pendingSpace = true; prevSpace = true; }
                i += k;
                continue;
              }
              emit(space || ch === '\u00a0' ? (pre && ch === '\t' ? '\t' : ' ') : ch,
                   box.left + sx, box.right + sx, box.top + sy, box.height, block, cell);
              prevSpace = space;
              i += k;
            }
          }
          flush();
          return JSON.stringify(out.map(q => ({ T: q.T, X: r1(q.L), Y: r1(q.Y), H: r1(q.H), E: q.E, B: q.B, C: q.C, P: q.P, S: q.S })));
        }
        """;

    /// <summary>
    /// Validates the character geometry Chrome reported before anything is placed on screen. Runs come from a rendered untrusted
    /// document, so sizes are bounded, non-finite or off-page entries are dropped, edges are clamped to the page, and the text is
    /// capped so a pathological message cannot balloon the reading pane's memory.
    /// </summary>
    public static IReadOnlyList<TextRun> ReadTextRuns(string json, int pageWidth, int pageHeight)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return [];
        BrowserTextRun[] runs;
        try { runs = System.Text.Json.JsonSerializer.Deserialize<BrowserTextRun[]>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }

        var result = new List<TextRun>();
        var totalCharacters = 0;
        foreach (var run in runs)
        {
            var text = run.T ?? "";
            if (text.Length is 0 or > 20_000 || run.E is null || run.E.Length != text.Length + 1) continue;
            if (!double.IsFinite(run.X) || !double.IsFinite(run.Y) || !double.IsFinite(run.H)) continue;
            if (run.X < -1 || run.X >= pageWidth || run.Y < 0 || run.Y >= pageHeight || run.H < 1 || run.H > pageHeight) continue;
            if (run.B < 0 || string.IsNullOrWhiteSpace(text) && run.E[^1] - run.E[0] < 0.5) continue;
            var edges = new double[run.E.Length];
            var valid = true;
            for (var i = 0; i < edges.Length; i++)
            {
                var e = run.E[i];
                if (!double.IsFinite(e)) { valid = false; break; }
                edges[i] = Math.Clamp(run.X + e, 0, pageWidth) - Math.Max(0, run.X);
            }
            if (!valid) continue;
            totalCharacters += text.Length;
            if (totalCharacters > MaximumOverlayTextCharacters) break;
            result.Add(new TextRun(text, Math.Max(0, run.X), run.Y, Math.Min(run.H, pageHeight - run.Y), edges,
                run.B, run.C != 0, run.P != 0, run.S != 0));
            if (result.Count >= MaximumOverlayRuns) break;
        }
        return result;
    }

    public static (int Width, int Height) ValidateScreenshot(ReadOnlySpan<byte> png, double scale = 1)
    {
        if (png.Length is < 24 or > MaximumScreenshotBytes ||
            !png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !png[12..16].SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Browser returned an invalid preview image.");
        var width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png[16..20]));
        var height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png[20..24]));
        if (width < 400 || width > 1400 * scale + 2 || height <= 0 || height > TileHeight * scale + 2 ||
            (long)width * height > 4_200_000 * scale * scale + 10_000)
            throw new InvalidDataException("Browser preview image exceeds the size limit.");
        return (width, height);
    }
}
