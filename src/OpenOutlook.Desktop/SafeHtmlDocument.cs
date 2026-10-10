using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace OpenOutlook.Desktop;

public sealed record HtmlImageSource(string Key, string Value, bool ContactsExternalSite);

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
            if (!IsHiddenImage(image)) Add(image.GetAttribute("src"));
        foreach (var element in document.QuerySelectorAll("[background]")) Add(element.GetAttribute("background"));
        foreach (var element in document.QuerySelectorAll("[style], style"))
        {
            var css = element.LocalName == "style" ? element.TextContent : element.GetAttribute("style") ?? "";
            foreach (Match match in CssUrl().Matches(css)) Add(match.Groups[2].Value);
        }
        return sources.Values.Take(MaximumImageSources).ToArray();

        void Add(string? value)
        {
            if (TrySource(value, out var source)) sources.TryAdd(source.Key, source);
        }
    }

    private static bool IsHiddenImage(IElement image)
    {
        if (image.HasAttribute("hidden") || image.GetAttribute("width")?.Trim() == "0" ||
            image.GetAttribute("height")?.Trim() == "0") return true;
        var style = image.GetAttribute("style");
        return style is not null && Regex.IsMatch(style,
            @"(?:^|;)\s*(?:display\s*:\s*none|visibility\s*:\s*hidden)(?:\s*!important)?\s*(?:;|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
        var mime = bytes[0] == 137 ? "image/png" : bytes[0] == 0xff ? "image/jpeg" : "image/gif";
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
            "tfoot", "tr", "td", "th", "col", "colgroup", "img", "style" })
            sanitizer.AllowedTags.Add(tag);
        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in new[] { "style", "class", "id", "align", "valign", "width", "height", "bgcolor", "color",
            "face", "size", "cellpadding", "cellspacing", "colspan", "rowspan", "border", "alt", "title", "src" })
            sanitizer.AllowedAttributes.Add(attribute);
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("data");
        sanitizer.AllowedCssProperties.Clear();
        foreach (var property in new[] { "background", "background-color", "background-image", "color", "display",
            "font", "font-family", "font-size", "font-weight", "font-style", "line-height", "letter-spacing",
            "text-align", "text-decoration", "text-transform", "vertical-align", "white-space", "width", "height",
            "max-width", "min-width", "max-height", "min-height", "margin", "margin-top", "margin-right",
            "margin-bottom", "margin-left", "padding", "padding-top", "padding-right", "padding-bottom",
            "padding-left", "border", "border-top", "border-right", "border-bottom", "border-left",
            "border-color", "border-width", "border-style", "border-radius", "box-shadow", "float",
            "table-layout", "border-collapse", "border-spacing", "opacity" })
            sanitizer.AllowedCssProperties.Add(property);
        return sanitizer;
    }

    [GeneratedRegex("url\\(\\s*(['\"]?)(.*?)\\1\\s*\\)", RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex CssUrl();
}
