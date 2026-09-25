using System.Net;
using System.Text;

namespace OpenOutlook.Desktop;

public sealed record HtmlPreviewRun(string Text, bool Bold = false, bool Italic = false,
    bool Underline = false, double Scale = 1);

/// <summary>
/// Converts a bounded subset of email HTML to inert text runs. No markup, URI, CSS,
/// image or script is handed to a browser or an Avalonia HTML control.
/// </summary>
public static class SafeHtmlPreview
{
    private const int MaxInput = 512 * 1024;
    private const int MaxOutput = 512 * 1024;
    private const int MaxRuns = 5000;
    private const int MaxDepth = 64;
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    { "script", "style", "head", "iframe", "object", "embed", "svg", "math", "template", "form" };

    private readonly record struct Style(bool Bold, bool Italic, bool Underline, double Scale, bool Pre);
    private readonly record struct Frame(string Tag, Style Previous);

    public static IReadOnlyList<HtmlPreviewRun> Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (html.Length > MaxInput) throw new InvalidDataException("HTML message is too large to preview.");
        var runs = new List<HtmlPreviewRun>();
        var stack = new Stack<Frame>();
        var style = new Style(false, false, false, 1, false);
        var outputLength = 0;
        var index = 0;
        while (index < html.Length && runs.Count < MaxRuns)
        {
            if (html[index] != '<')
            {
                var next = html.IndexOf('<', index);
                if (next < 0) next = html.Length;
                AddText(html[index..next], style);
                index = next;
                continue;
            }
            if (html.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
            {
                var endComment = html.IndexOf("-->", index + 4, StringComparison.Ordinal);
                index = endComment < 0 ? html.Length : endComment + 3;
                continue;
            }
            var end = FindTagEnd(html, index + 1);
            if (end < 0) { AddText("<", style); index++; continue; }
            var raw = html[(index + 1)..end].Trim();
            index = end + 1;
            if (raw.Length == 0 || raw[0] is '!' or '?') continue;
            var closing = raw[0] == '/';
            var start = closing ? 1 : 0;
            var stop = start;
            while (stop < raw.Length && char.IsLetterOrDigit(raw[stop])) stop++;
            if (stop == start) continue;
            var tag = raw[start..stop].ToLowerInvariant();
            if (Hidden.Contains(tag))
            {
                if (!closing)
                {
                    var close = html.IndexOf("</" + tag, index, StringComparison.OrdinalIgnoreCase);
                    if (close < 0) { index = html.Length; break; }
                    var closeEnd = FindTagEnd(html, close + 2 + tag.Length);
                    index = closeEnd < 0 ? html.Length : closeEnd + 1;
                }
                continue;
            }
            if (closing)
            {
                if (tag is "p" or "div" or "section" or "article" or "blockquote" or "li" or "tr" || tag.StartsWith('h') && tag.Length == 2)
                    AddBreak(tag is "p" or "blockquote" || tag.StartsWith('h') ? 2 : 1);
                if (!stack.Any(frame => frame.Tag == tag)) continue;
                while (stack.Count > 0)
                {
                    var frame = stack.Pop();
                    style = frame.Previous;
                    if (frame.Tag == tag) break;
                }
                continue;
            }
            switch (tag)
            {
                case "br": AddBreak(1); break;
                case "hr": AddBreak(2); break;
                case "img": AddText("[Image blocked]", style); break;
                case "p": case "section": case "article": case "blockquote": AddBreak(2); break;
                case "div": case "tr": AddBreak(1); break;
                case "li": AddBreak(1); AddText("• ", style); break;
                case "td": case "th": AddText("    ", style); break;
                default:
                    if (tag.Length == 2 && tag[0] == 'h' && tag[1] is >= '1' and <= '6') AddBreak(2);
                    break;
            }
            if (raw.EndsWith('/') || tag is "br" or "hr" or "img" or "meta" or "link" or "input" or "wbr") continue;
            if (stack.Count >= MaxDepth) continue;
            stack.Push(new Frame(tag, style));
            style = tag switch
            {
                "b" or "strong" or "th" => style with { Bold = true },
                "i" or "em" => style with { Italic = true },
                "u" or "a" => style with { Underline = true },
                "pre" => style with { Pre = true },
                "h1" => style with { Bold = true, Scale = 1.55 },
                "h2" => style with { Bold = true, Scale = 1.4 },
                "h3" => style with { Bold = true, Scale = 1.25 },
                "h4" or "h5" or "h6" => style with { Bold = true, Scale = 1.1 },
                _ => style
            };
        }
        if (runs.Count >= MaxRuns)
            runs[^1] = runs[^1] with { Text = runs[^1].Text + "\n[Preview shortened]" };
        return runs;

        void AddBreak(int count)
        {
            if (runs.Count == 0) return;
            var last = runs[^1].Text;
            var existing = last.Length - last.TrimEnd('\n').Length;
            if (existing < count) AddText(new string('\n', count - existing), style, preserveWhitespace: true);
        }

        void AddText(string value, Style current, bool preserveWhitespace = false)
        {
            if (value.Length == 0 || runs.Count >= MaxRuns) return;
            if (!preserveWhitespace) value = WebUtility.HtmlDecode(value);
            if (!current.Pre && !preserveWhitespace)
            {
                var collapsed = new StringBuilder(value.Length);
                var white = false;
                foreach (var character in value)
                {
                    if (char.IsWhiteSpace(character)) { if (!white) collapsed.Append(' '); white = true; }
                    else { collapsed.Append(character); white = false; }
                }
                value = collapsed.ToString();
            }
            if (value.Length == 0) return;
            if (outputLength + value.Length > MaxOutput)
                value = value[..Math.Max(0, MaxOutput - outputLength)];
            if (value.Length == 0) return;
            outputLength += value.Length;
            var run = new HtmlPreviewRun(value, current.Bold, current.Italic, current.Underline, current.Scale);
            if (runs.Count > 0 && runs[^1] is { } previous &&
                previous.Bold == run.Bold && previous.Italic == run.Italic &&
                previous.Underline == run.Underline && previous.Scale == run.Scale)
                runs[^1] = previous with { Text = previous.Text + run.Text };
            else runs.Add(run);
        }
    }

    private static int FindTagEnd(string html, int start)
    {
        var quote = '\0';
        for (var i = start; i < html.Length; i++)
        {
            var character = html[i];
            if (quote != '\0') { if (character == quote) quote = '\0'; }
            else if (character is '\'' or '"') quote = character;
            else if (character == '>') return i;
        }
        return -1;
    }
}
