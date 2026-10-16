using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace OpenOutlook.Desktop;

public sealed record HtmlImageSource(string Key, string Value, bool ContactsExternalSite)
{
    /// <summary>Where the message referenced it (an img element, a style sheet, a style attribute ...), for the log.</summary>
    public string Origin { get; init; } = "";
}

/// <summary>
/// Turns email HTML into a bounded, inert browser document. All image URLs are removed before
/// the browser sees them; validated image bytes can later be inserted as data URIs.
/// </summary>
public static partial class SafeHtmlDocument
{
    public const int MaximumHtmlLength = 512 * 1024;
    public const int MaximumImageSources = 64;
    private const string Csp = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; " +
        "font-src 'none'; connect-src 'none'; media-src 'none'; object-src 'none'; " +
        "frame-src 'none'; form-action 'none'; base-uri 'none'; script-src 'none'";

    public static IReadOnlyList<HtmlImageSource> FindImages(string html)
    {
        var document = Parse(html);
        var sources = new Dictionary<string, HtmlImageSource>(StringComparer.Ordinal);
        foreach (var image in document.QuerySelectorAll("img[src]"))
            if (!IsHiddenImage(image)) Add(image.GetAttribute("src"), "img" + Describe(image));
        foreach (var element in document.QuerySelectorAll("[background]")) Add(element.GetAttribute("background"), "background attribute on " + element.LocalName + Describe(element));
        foreach (var element in document.QuerySelectorAll("[style], style"))
        {
            // A style sheet's pictures count only where they can be drawn: rules for print, and rules whose selector matches nothing
            // that can show a background (American Express puts "printed" and "forwarded" tracking beacons in such rules), are not
            // fetched. Web fonts are not pictures: an @font-face src (a .woff from the sender's site) was counted as a picture that
            // "could not be loaded" (a Delta mailing, 2026-10-15); fonts stay blocked by the page's CSP, they are just not reported.
            var rules = element.LocalName == "style"
                ? ScreenRulesWithPictures(document, element.TextContent)
                : [(Selector: "", Body: element.GetAttribute("style") ?? "")];
            foreach (var (selector, body) in rules)
                foreach (Match match in CssUrl().Matches(FontFaceRule().Replace(body, "")))
                    if (!IsFontFile(match.Groups[2].Value))
                        Add(match.Groups[2].Value, element.LocalName == "style"
                            ? "style sheet rule " + (selector.Length > 80 ? selector[..80] + "..." : selector)
                            : "style attribute on " + element.LocalName + Describe(element));
        }
        return sources.Values.Take(MaximumImageSources).ToArray();

        void Add(string? value, string origin)
        {
            if (TrySource(value, out var source)) sources.TryAdd(source.Key, source with { Origin = origin });
        }

        static string Describe(IElement element)
        {
            var id = element.Id is { Length: > 0 and <= 40 } i ? " #" + i : "";
            var size = element.LocalName == "img" ? $" {element.GetAttribute("width") ?? "?"}x{element.GetAttribute("height") ?? "?"}" : "";
            return id + size;
        }
    }

    /// <summary>
    /// The declarations of a style sheet that can put a picture on screen: rules inside <c>@media print</c> (only) and other
    /// at-rules are skipped, and a rule is kept only when one of its selectors matches an element of the message (a selector
    /// AngleSharp cannot evaluate counts as matching).
    /// </summary>
    internal static IReadOnlyList<(string Selector, string Body)> ScreenRulesWithPictures(IParentNode document, string css)
    {
        var kept = new List<(string Selector, string Body)>();
        // "<!--" and "-->" around a style sheet are legal CSS and ignored by browsers (mail bodies from Outlook.com have them); left in,
        // "<!-- @media print" read as a selector, and a selector that cannot be evaluated counts as matching.
        css = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline).Replace("<!--", " ").Replace("-->", " ");
        Scan(css, 0, css.Length);
        return kept;

        void Scan(string text, int start, int end)
        {
            var i = start;
            while (i < end)
            {
                var open = text.IndexOf('{', i, end - i);
                if (open < 0) return;
                var prelude = text[i..open].Trim();
                var semicolon = prelude.LastIndexOf(';');                        // "@import ...;" or "@charset ...;" before the rule
                if (semicolon >= 0) prelude = prelude[(semicolon + 1)..].Trim();
                var close = MatchingBrace(text, open, end);
                if (close < 0) return;
                if (prelude.StartsWith('@'))
                {
                    if (prelude.StartsWith("@media", StringComparison.OrdinalIgnoreCase) && !IsPrintOnly(prelude))
                        Scan(text, open + 1, close);
                    // @font-face, @keyframes, @page and the rest put no picture on the page.
                }
                else
                {
                    var body = text[(open + 1)..close];
                    if (body.Contains("url(", StringComparison.OrdinalIgnoreCase) && AnyMatch(prelude))
                        kept.Add((prelude, body));
                }
                i = close + 1;
            }
        }

        bool AnyMatch(string selectors)
        {
            foreach (var selector in selectors.Split(','))
            {
                var plain = Regex.Replace(selector, @"::?[a-zA-Z-]+(\([^)]*\))?", "").Trim();       // :hover, ::before -> the element itself
                if (plain.Length == 0) return true;
                try { if (document.QuerySelectorAll(plain).Any(CanShowBackground)) return true; }
                catch (Exception) { return true; }
            }
            return false;
        }

        // An element with no content and no size of its own (AmEx's <div id="_two50"></div>, the target of its "printed" and
        // "forwarded" beacons) has nothing to paint a background on.
        static bool CanShowBackground(IElement element)
        {
            if (element.Children.Length > 0 || !string.IsNullOrWhiteSpace(element.TextContent)) return true;
            if (element.HasAttribute("width") || element.HasAttribute("height")) return true;
            var style = element.GetAttribute("style") ?? "";
            return Regex.IsMatch(style, @"(?:^|[;\s])(?:min-)?(?:width|height)\s*:\s*[1-9]|(?:^|[;\s])padding[a-z-]*\s*:\s*[1-9]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        static bool IsPrintOnly(string media) =>
            Regex.IsMatch(media, @"\bprint\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(media, @"\b(screen|all)\b", RegexOptions.IgnoreCase);

        static int MatchingBrace(string text, int open, int end)
        {
            var depth = 0;
            for (var j = open; j < end; j++)
            {
                if (text[j] == '{') depth++;
                else if (text[j] == '}' && --depth == 0) return j;
            }
            return -1;
        }
    }

    private static bool IsHiddenImage(IElement image)
    {
        if (image.HasAttribute("hidden") || image.GetAttribute("width")?.Trim() == "0" ||
            image.GetAttribute("height")?.Trim() == "0") return true;
        var style = image.GetAttribute("style");
        if (style is not null && Regex.IsMatch(style,
            @"(?:^|;)\s*(?:display\s*:\s*none|visibility\s*:\s*hidden)(?:\s*!important)?\s*(?:;|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
        return IsTrackingPixel(image, style);
    }

    /// <summary>
    /// A sliver -- at most 2 px one way and 8 px the other (by attributes or inline style; 1 x 1, 2 x 6 ...) -- is a tracking pixel, not content:
    /// there is nothing to see, and fetching it only tells the sender the message was opened -- and can be slow (an American Express tracking
    /// server took 8 s per pixel) or fail (Slickdeals' 2 x 6 "imp" beacon answers with a redirect, which is refused). It is not fetched and not
    /// counted as a missing picture. Small icons (4 x 4, 8 x 8) and full-width 1 px spacer lines are still loaded.
    /// </summary>
    private static bool IsTrackingPixel(IElement image, string? style)
    {
        static double? Size(string? value) => value is not null &&
            double.TryParse(value.Trim().TrimEnd('x', 'X', 'p', 'P').Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var size) ? size : null;
        string? StyleValue(string name)
        {
            if (style is null) return null;
            var match = Regex.Match(style, @"(?:^|[;\s])" + name + @"\s*:\s*([0-9.]+)\s*px", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : null;
        }
        if (Size(StyleValue("width") ?? image.GetAttribute("width")) is not { } width ||
            Size(StyleValue("height") ?? image.GetAttribute("height")) is not { } height) return false;
        return Math.Min(width, height) <= 2 && Math.Max(width, height) <= 8;
    }

    public static string Build(string html, IReadOnlyDictionary<string, byte[]> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        var document = Parse(html);
        var approvedImageData = new Dictionary<string, (string Source, string? OriginalTitle)>(StringComparer.Ordinal);
        var approvedLinks = new Dictionary<string, string>(StringComparer.Ordinal);
        var imageMarkerPrefix = "openoutlook-image-" + Guid.NewGuid().ToString("N") + "-";
        var linkMarkerPrefix = "openoutlook-link-" + Guid.NewGuid().ToString("N") + "-";
        var nextImage = 0;
        var nextLink = 0;
        foreach (var element in document.All.ToArray())
        {
            if (element.LocalName == "a" && TryLink(element.GetAttribute("href"), out var link))
            {
                var marker = linkMarkerPrefix + (++nextLink).ToString(System.Globalization.CultureInfo.InvariantCulture);
                approvedLinks.Add(marker, link);
                element.SetAttribute("data-openoutlook-marker", marker);
            }
            foreach (var attribute in element.Attributes.ToArray())
            {
                if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                    attribute.Name is "srcset" or "href" or "action" or "formaction" or "poster" or "data" or
                        "xlink:href")
                    element.RemoveAttribute(attribute.Name);
            }
            if (element.LocalName == "img")
            {
                var source = DataUri(element.GetAttribute("src"), images);
                element.RemoveAttribute("src");
                if (source is not null)
                {
                    var marker = imageMarkerPrefix + (++nextImage).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    approvedImageData.Add(marker, (source, element.GetAttribute("title")));
                    element.SetAttribute("title", marker);
                }
                else if (!element.HasAttribute("alt"))
                    element.SetAttribute("alt", "Image unavailable");
                element.RemoveAttribute("srcset");
            }
            else element.RemoveAttribute("src");

            // Rewrite the author's own CSS first. The approved data URI is appended afterwards: if it
            // passed through ReplaceCssUrls as well, its "data:" prefix would be keyed a second time,
            // never match the loaded-image table, and every background= attribute would render as none.
            if (element.HasAttribute("style"))
                element.SetAttribute("style", ReplaceCssUrls(element.GetAttribute("style") ?? "", images));
            if (element.HasAttribute("background"))
            {
                var source = DataUri(element.GetAttribute("background"), images);
                element.RemoveAttribute("background");
                if (source is not null)
                    element.SetAttribute("style", (element.GetAttribute("style") ?? "") +
                        ";background-image:url('" + source + "')");
            }
            if (element.LocalName == "style")
                element.TextContent = ReplaceCssUrls(element.TextContent, images);
        }

        var sanitizer = CreateSanitizer();
        sanitizer.AllowedAttributes.Add("data-openoutlook-marker");
        var sanitized = sanitizer.SanitizeDocument(document.DocumentElement.OuterHtml);
        var safe = new HtmlParser().ParseDocument(sanitized);
        foreach (var anchor in safe.QuerySelectorAll("a[data-openoutlook-marker]"))
        {
            var marker = anchor.GetAttribute("data-openoutlook-marker");
            anchor.RemoveAttribute("data-openoutlook-marker");
            if (marker is not null && approvedLinks.TryGetValue(marker, out var url))
                anchor.SetAttribute("data-openoutlook-url", url);
        }
        foreach (var image in safe.QuerySelectorAll("img[title]"))
        {
            var marker = image.GetAttribute("title");
            if (marker is null || !approvedImageData.TryGetValue(marker, out var approved)) continue;
            image.SetAttribute("src", approved.Source);
            if (approved.OriginalTitle is null) image.RemoveAttribute("title");
            else image.SetAttribute("title", approved.OriginalTitle);
        }
        foreach (var link in safe.QuerySelectorAll("link,base,meta[http-equiv]")) link.Remove();
        var meta = safe.CreateElement("meta");
        meta.SetAttribute("http-equiv", "Content-Security-Policy");
        meta.SetAttribute("content", Csp);
        safe.Head!.Prepend(meta);
        // Mail is designed for a white page (Outlook always paints one): without a base colour a transparent snapshot shows the app
        // theme through it, which makes dark text unreadable in dark mode. Prepended so the message's own styles still win.
        var baseStyle = safe.CreateElement("style");
        baseStyle.TextContent = "html{background:#fff;color:#000;color-scheme:light}";
        safe.Head.Prepend(baseStyle);
        var viewport = safe.CreateElement("meta");
        viewport.SetAttribute("name", "viewport");
        viewport.SetAttribute("content", "width=device-width, initial-scale=1");
        safe.Head.Append(viewport);
        // Browser requests are also intercepted and aborted. CSP is a second independent barrier.
        return "<!doctype html>" + safe.DocumentElement.OuterHtml;
    }

    public static bool TryLink(string? value, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)) return false;
        value = value.Trim();
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http" or "mailto") ||
            uri.Scheme != "mailto" && uri.UserInfo.Length != 0) return false;
        url = uri.AbsoluteUri;
        return true;
    }

    /// <summary>For a native dialog that cancels navigation and opens approved links externally.</summary>
    public static string BuildInteractive(string html, IReadOnlyDictionary<string, byte[]> images)
    {
        var safe = new HtmlParser().ParseDocument(Build(html, images));
        foreach (var anchor in safe.QuerySelectorAll("a[data-openoutlook-url]"))
            if (TryLink(anchor.GetAttribute("data-openoutlook-url"), out var url))
                anchor.SetAttribute("href", url);
        return "<!doctype html>" + safe.DocumentElement.OuterHtml;
    }

    public static bool TrySource(string? value, out HtmlImageSource source)
    {
        source = null!;
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return false;
        if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
            value.Contains(";base64,", StringComparison.OrdinalIgnoreCase) &&
            value.Length <= MaximumHtmlLength)
        {
            source = new HtmlImageSource("data:" + value, value, false);
            return true;
        }
        if (value.Length > 2048) return false;
        if (value.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
        {
            var cid = value[4..].Trim().Trim('<', '>');
            if (cid.Length is 0 or > 255 || cid.Any(char.IsControl)) return false;
            source = new HtmlImageSource("cid:" + cid.ToLowerInvariant(), cid, false);
            return true;
        }
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (SafeRemoteImageLoader.TryAcceptUrl(value, out var uri))
        {
            source = new HtmlImageSource("remote:" + uri.AbsoluteUri, uri.AbsoluteUri, true);
            return true;
        }
        return false;
    }

    public static byte[]? DecodeDataImage(HtmlImageSource source)
    {
        if (!source.Key.StartsWith("data:", StringComparison.Ordinal)) return null;
        var comma = source.Value.IndexOf(',');
        if (comma < 0) return null;
        try
        {
            var bytes = Convert.FromBase64String(source.Value[(comma + 1)..]);
            SafeInlineImage.Validate(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or OverflowException)
        { return null; }
    }

    private static string? DataUri(string? source, IReadOnlyDictionary<string, byte[]> images)
    {
        if (!TrySource(source, out var parsed) || !images.TryGetValue(parsed.Key, out var bytes)) return null;
        try { SafeInlineImage.Validate(bytes); }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException) { return null; }
        var mime = SafeInlineImage.MimeType(bytes);
        return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
    }

    private static string ReplaceCssUrls(string css, IReadOnlyDictionary<string, byte[]> images) =>
        CssUrl().Replace(css, match => DataUri(match.Groups[2].Value, images) is { } data
            ? "url('" + data + "')" : "none");

    private static AngleSharp.Html.Dom.IHtmlDocument Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (html.Length > MaximumHtmlLength)
            throw new InvalidDataException("HTML message is too large to display.");
        return new HtmlParser().ParseDocument(html);
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[] { "html", "head", "body", "a", "div", "span", "p", "br", "hr", "strong", "b",
            "em", "i", "u", "s", "small", "big", "font", "center", "blockquote", "pre", "code",
            "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "table", "thead", "tbody",
            "tfoot", "tr", "td", "th", "col", "colgroup", "img", "style",
            // more of what newsletters and receipts use (all plain layout, nothing that loads or runs anything)
            "sup", "sub", "strike", "del", "ins", "mark", "abbr", "acronym", "address", "cite", "q", "tt", "kbd", "samp", "var", "wbr",
            "dl", "dt", "dd", "caption", "section", "article", "header", "footer", "main", "nav", "aside", "figure", "figcaption",
            "picture", "label", "time", "nobr" })
            sanitizer.AllowedTags.Add(tag);
        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in new[] { "style", "class", "id", "align", "valign", "width", "height", "bgcolor", "color",
            "face", "size", "cellpadding", "cellspacing", "colspan", "rowspan", "border", "alt", "title", "src",
            "dir", "lang", "nowrap", "rules", "frame", "summary", "start", "type", "reversed", "scope", "headers", "abbr",
            "bordercolor", "hspace", "vspace", "span", "role", "clear", "noshade" })
            sanitizer.AllowedAttributes.Add(attribute);
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("data");
        // Responsive mail keeps its desktop/phone rules in @media blocks of a <style> element; without them the layout falls back to whatever
        // the inline styles alone give.
        sanitizer.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Media);
        sanitizer.AllowedCssProperties.Clear();
        // The CSS parser expands shorthands before checking them (border -> border-top-width / -style / -color ..., border-radius ->
        // the four corners, background -> background-position / -size / -repeat ..., text-decoration -> text-decoration-line ...).
        // Listing only the shorthands silently dropped every border, rounded corner and link underline (found 2026-10-15 by rendering
        // a newsletter both ways); so the longhands are listed too. Nothing here can load content (url() values are rewritten to
        // approved data URIs before sanitizing, and the page's CSP blocks everything else) or position content over the page.
        var sides = new[] { "top", "right", "bottom", "left" };
        var properties = new List<string> { "background", "background-color", "background-image", "background-position",
            "background-position-x", "background-position-y", "background-size", "background-repeat", "background-repeat-x",
            "background-repeat-y", "background-origin", "background-clip", "background-attachment", "color", "display",
            "font", "font-family", "font-size", "font-weight", "font-style", "font-variant", "font-stretch", "line-height", "letter-spacing",
            "word-spacing", "text-align", "text-decoration", "text-decoration-line", "text-decoration-color", "text-decoration-style",
            "text-transform", "text-indent", "text-shadow", "text-overflow", "vertical-align", "white-space", "word-break", "word-wrap",
            "overflow-wrap", "direction", "width", "height", "max-width", "min-width", "max-height", "min-height", "margin", "padding",
            "border", "border-color", "border-width", "border-style", "border-radius", "box-shadow", "box-sizing", "float", "clear",
            "table-layout", "border-collapse", "border-spacing", "caption-side", "empty-cells", "opacity", "visibility",
            "overflow", "overflow-x", "overflow-y", "object-fit", "object-position", "list-style", "list-style-type",
            "list-style-position", "outline", "outline-color", "outline-style", "outline-width" };
        foreach (var side in sides)
        {
            properties.AddRange(new[] { "margin-" + side, "padding-" + side, "border-" + side, "border-" + side + "-width",
                "border-" + side + "-style", "border-" + side + "-color" });
        }
        foreach (var corner in new[] { "top-left", "top-right", "bottom-right", "bottom-left" })
            properties.Add("border-" + corner + "-radius");
        foreach (var property in properties) sanitizer.AllowedCssProperties.Add(property);
        return sanitizer;
    }

    private static bool IsFontFile(string url)
    {
        var path = url.Split('?', '#')[0];
        return path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".eot", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"@font-face\s*\{[^}]*\}", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex FontFaceRule();

    [GeneratedRegex("url\\(\\s*(['\"]?)(.*?)\\1\\s*\\)", RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex CssUrl();
}
