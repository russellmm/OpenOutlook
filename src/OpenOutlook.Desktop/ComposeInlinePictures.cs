using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace OpenOutlook.Desktop;

/// <summary>A picture inside an HTML message, sent as an inline (Content-ID) part.</summary>
public sealed record InlinePicture(string ContentId, string FileName, string MimeType, byte[] Data);

/// <summary>
/// Pictures pasted or inserted into the compose body are data: URIs in the editor. Gmail and several other mail programs do not show data: pictures,
/// so before a message is saved or sent each one becomes an inline part (<c>cid:</c>), as Outlook sends them; reopening a draft turns them back into
/// data: URIs for the editor. Content-IDs and file names come from the picture's bytes, so a picture keeps its name when a draft is saved again
/// (Microsoft drafts: only new pictures are uploaded) and the same picture used twice is sent once.
/// </summary>
public static partial class ComposeInlinePictures
{
    /// <summary>The largest picture accepted in a message (Graph and Gmail take far more; this keeps the editor and the draft responsive).</summary>
    public const int MaxPictureBytes = 7 * 1024 * 1024;
    private const string IdDomain = "openoutlook";

    public static (string Html, IReadOnlyList<InlinePicture> Pictures) Extract(string html)
    {
        if (string.IsNullOrEmpty(html) || !html.Contains("data:image/", StringComparison.OrdinalIgnoreCase)) return (html, []);
        var document = new HtmlParser().ParseDocument("<body>" + html + "</body>");
        var pictures = new List<InlinePicture>();
        foreach (var img in document.Body!.QuerySelectorAll("img[src]").ToList())
        {
            var src = img.GetAttribute("src") ?? "";
            if (!src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (FromDataUri(src) is not { } picture) { img.RemoveAttribute("src"); continue; }    // not a picture we can send: never send the data: text
            if (pictures.FirstOrDefault(p => p.ContentId == picture.ContentId) is { } same) picture = same;
            else pictures.Add(picture);
            img.SetAttribute("src", "cid:" + picture.ContentId);
        }
        return (document.Body.InnerHtml, pictures);
    }

    /// <summary>cid: pictures back to data: URIs (a reopened draft); references to unknown parts are left as they are.</summary>
    public static string Restore(string html, IEnumerable<InlinePicture> pictures)
    {
        var byId = pictures.GroupBy(p => p.ContentId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (byId.Count == 0 || !html.Contains("cid:", StringComparison.OrdinalIgnoreCase)) return html;
        var document = new HtmlParser().ParseDocument("<body>" + html + "</body>");
        foreach (var img in document.Body!.QuerySelectorAll("img[src]"))
        {
            var src = img.GetAttribute("src") ?? "";
            if (!src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) continue;
            if (byId.TryGetValue(Uri.UnescapeDataString(src[4..]).Trim('<', '>'), out var p)) img.SetAttribute("src", ToDataUri(p.MimeType, p.Data));
        }
        return document.Body.InnerHtml;
    }

    /// <summary>A png / jpeg / gif / webp data: URI as a picture (the bytes must really be that kind of picture), or null.</summary>
    public static InlinePicture? FromDataUri(string uri)
    {
        var match = DataUriRegex().Match(uri);
        if (!match.Success) return null;
        byte[] data;
        try { data = Convert.FromBase64String(Regex.Replace(match.Groups[2].Value, "\\s", "")); }
        catch (FormatException) { return null; }
        var mime = match.Groups[1].Value.ToLowerInvariant();
        return data.Length is 0 or > MaxPictureBytes || SniffMime(data) != mime ? null : Create(mime, data);
    }

    /// <summary>A picture file's bytes as a picture (Insert > Pictures), or null when it is not png / jpeg / gif / webp or too large.</summary>
    public static InlinePicture? FromFile(byte[] data) =>
        data.Length is 0 or > MaxPictureBytes || SniffMime(data) is not { } mime ? null : Create(mime, data);

    public static string ToDataUri(string mime, byte[] data) => "data:" + mime + ";base64," + Convert.ToBase64String(data);

    /// <summary>The Content-ID of a picture this program named (image-&lt;hash&gt;.ext), so a saved draft's attachment can be matched by name.</summary>
    public static string ContentIdFor(string fileName) => fileName + "@" + IdDomain;

    public static bool IsOwnName(string name) => OwnNameRegex().IsMatch(name);

    private static InlinePicture Create(string mime, byte[] data)
    {
        var hash = Convert.ToHexString(SHA256.HashData(data))[..16].ToLowerInvariant();
        var name = "image-" + hash + mime switch { "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".jpg" };
        return new InlinePicture(ContentIdFor(name), name, mime, data);
    }

    /// <summary>The picture type from the file's first bytes (the declared type is not trusted).</summary>
    internal static string? SniffMime(ReadOnlySpan<byte> d) =>
        d.Length >= 8 && d[0] == 0x89 && d[1] == (byte)'P' && d[2] == (byte)'N' && d[3] == (byte)'G' ? "image/png"
        : d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF ? "image/jpeg"
        : d.Length >= 6 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'8' ? "image/gif"
        : d.Length >= 12 && d[0] == (byte)'R' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'F' && d[8] == (byte)'W' && d[9] == (byte)'E' && d[10] == (byte)'B' && d[11] == (byte)'P' ? "image/webp"
        : null;

    [GeneratedRegex(@"^data:(image/(?:png|jpeg|gif|webp));base64,([A-Za-z0-9+/=\s]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DataUriRegex();
    [GeneratedRegex(@"^image-[0-9a-f]{16}\.(png|jpg|gif|webp)$")]
    private static partial Regex OwnNameRegex();
}
