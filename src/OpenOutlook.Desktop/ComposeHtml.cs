using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace OpenOutlook.Desktop;

/// <summary>
/// Sanitizer for message bodies the user is composing. It has different requirements from the
/// reader's <see cref="SafeHtmlDocument"/>: untrusted mail arrives here too (reply and forward load
/// the original body into the editor), but everything that survives must round-trip intact, because
/// it becomes the message other people receive.
///
/// A stock <see cref="HtmlSanitizer"/> profile is unsuitable in both directions. It silently drops
/// formatting the editor itself produces -- the font picker emits &lt;font face&gt;, the link dialog
/// accepts mailto:, and pasted images arrive as data: URIs, all of which were discarded on Apply --
/// while still permitting &lt;form&gt; and &lt;input&gt;, which allow a forwarded message to plant a
/// fake credential prompt in the draft.
/// </summary>
public static partial class ComposeHtml
{
    /// <summary>Data URIs are accepted only for images, and only within a sane size.</summary>
    private const int MaximumDataUriLength = 4 * 1024 * 1024;

    private static readonly string[] AllowedTags =
    [
        "a", "abbr", "b", "big", "blockquote", "br", "caption", "center", "cite", "code", "col",
        "colgroup", "dd", "del", "div", "dl", "dt", "em", "figcaption", "figure", "font", "h1",
        "h2", "h3", "h4", "h5", "h6", "hr", "i", "img", "ins", "li", "mark", "ol", "p", "pre", "s",
        "small", "span", "strike", "strong", "sub", "sup", "table", "tbody", "td", "tfoot", "th",
        "thead", "tr", "u", "ul", "wbr",
    ];

    private static readonly string[] AllowedAttributes =
    [
        "align", "alt", "bgcolor", "border", "cellpadding", "cellspacing", "class", "color", "colspan",
        "face", "height", "href", "id", "rowspan", "size", "span", "src", "start", "style", "title",
        "type", "valign", "width",
    ];

    // Border longhands are listed explicitly because AngleSharp expands the `border` shorthand into
    // them; omitting them silently discards every bordered table and cell in a signature.
    private static readonly string[] AllowedCssProperties =
    [
        "background", "background-color", "background-image", "border", "border-bottom", "border-bottom-color",
        "border-bottom-left-radius", "border-bottom-right-radius", "border-bottom-style", "border-bottom-width",
        "border-collapse", "border-color", "border-left", "border-left-color", "border-left-style",
        "border-left-width", "border-radius", "border-right", "border-right-color", "border-right-style",
        "border-right-width", "border-spacing", "border-style", "border-top", "border-top-color",
        "border-top-left-radius", "border-top-right-radius", "border-top-style", "border-top-width",
        "border-width", "caption-side", "clear", "color", "display", "float", "font",
        "font-family", "font-size", "font-style", "font-variant", "font-weight", "height", "letter-spacing",
        "line-height", "list-style", "list-style-type", "margin", "margin-bottom", "margin-left",
        "margin-right", "margin-top", "max-height", "max-width", "min-height", "min-width", "opacity",
        "padding", "padding-bottom", "padding-left", "padding-right", "padding-top", "table-layout",
        "text-align", "text-decoration", "text-indent", "text-transform", "vertical-align", "visibility",
        "white-space", "width", "word-spacing",
    ];

    [GeneratedRegex(@"rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*([0-9.]+)\s*)?\)")]
    private static partial Regex ColorFunctionRegex();

    /// <summary>
    /// Returns HTML that is safe to place in the editor and safe to send, preserving the formatting
    /// the editor can create. Sanitizing already-sanitized output returns the same string.
    /// </summary>
    public static string Sanitize(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var sanitizer = CreateSanitizer();
        var sanitized = sanitizer.Sanitize(html);
        return NormalizeColorFunctions(RestrictDataUris(sanitized));
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        foreach (var tag in AllowedTags) sanitizer.AllowedTags.Add(tag);
        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in AllowedAttributes) sanitizer.AllowedAttributes.Add(attribute);
        // mailto: is kept because the editor's own link dialog offers it; data: because pasted and
        // inline images use it. Both are narrowed further after sanitization.
        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto", "data" }) sanitizer.AllowedSchemes.Add(scheme);
        sanitizer.AllowedCssProperties.Clear();
        foreach (var property in AllowedCssProperties) sanitizer.AllowedCssProperties.Add(property);
        return sanitizer;
    }

    /// <summary>
    /// Drops data: URIs that are not a reasonably sized image. The scheme allowlist alone would let
    /// data:text/html or data:application/javascript through on an href or src, which the editor
    /// would then render as part of the user's own message.
    /// </summary>
    private static string RestrictDataUris(string html)
    {
        var document = new HtmlParser().ParseDocument("<body>" + html + "</body>");
        foreach (var element in document.Body!.QuerySelectorAll("[src^='data:'],[href^='data:']").ToList())
        {
            var attribute = element.GetAttribute("href") is not null ? "href" : "src";
            var value = element.GetAttribute(attribute) ?? "";
            if (value.Length > MaximumDataUriLength || !IsSupportedImageDataUri(value))
                element.RemoveAttribute(attribute);
        }
        return document.Body.InnerHtml;
    }

    private static bool IsSupportedImageDataUri(string value) =>
        value.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("data:image/gif;base64,", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("data:image/webp;base64,", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rewrites opaque rgb()/rgba() colours as hex. AngleSharp re-serializes every colour the
    /// sanitizer keeps into rgba(), and Outlook's Word-based rendering engine ignores that syntax,
    /// so text coloured in the editor would arrive uncoloured. Values with real transparency are
    /// left alone rather than guessed at.
    /// </summary>
    private static string NormalizeColorFunctions(string html)
    {
        if (!html.Contains("rgb", StringComparison.OrdinalIgnoreCase)) return html;
        var document = new HtmlParser().ParseDocument("<body>" + html + "</body>");
        foreach (var element in document.Body!.QuerySelectorAll("[style]"))
        {
            var style = element.GetAttribute("style");
            if (string.IsNullOrEmpty(style) || !style.Contains("rgb", StringComparison.OrdinalIgnoreCase)) continue;
            var rewritten = ColorFunctionRegex().Replace(style, match =>
            {
                if (match.Groups[4].Success && decimal.TryParse(match.Groups[4].Value,
                        System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var alpha)
                    && alpha < 1m)
                    return match.Value;
                var red = ToComponent(match.Groups[1].Value);
                var green = ToComponent(match.Groups[2].Value);
                var blue = ToComponent(match.Groups[3].Value);
                return red < 0 || green < 0 || blue < 0 ? match.Value : $"#{red:x2}{green:x2}{blue:x2}";
            });
            if (rewritten != style) element.SetAttribute("style", rewritten);
        }
        return document.Body.InnerHtml;
    }

    private static int ToComponent(string value) =>
        int.TryParse(value, out var number) && number is >= 0 and <= 255 ? number : -1;
}
