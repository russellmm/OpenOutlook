using System.Globalization;
using System.Text;

namespace OpenOutlook.Providers.Google;

/// <summary>An outgoing message in neutral form.</summary>
public sealed class GmailOutgoing
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Bcc { get; set; } = "";
    public string Subject { get; set; } = "";
    /// <summary>Plain-text body (also sent as the alternative of an HTML message).</summary>
    public string Text { get; set; } = "";
    /// <summary>HTML body, or null for a plain-text message.</summary>
    public string? Html { get; set; }
    public string? InReplyTo { get; set; }
    public string? References { get; set; }
    public List<GmailOutgoingAttachment> Attachments { get; } = [];
}

public sealed record GmailOutgoingAttachment(string FileName, string MimeType, byte[] Data);

/// <summary>
/// Builds RFC 5322 / MIME messages for the Gmail API. Header values are checked for line breaks (header injection), non-ASCII text is encoded (RFC 2047),
/// bodies and attachments are base64, and the structure is multipart/mixed over multipart/alternative when needed.
/// </summary>
public static class GmailMimeBuilder
{
    public const int MaxAttachmentBytes = 25 * 1024 * 1024;           // Gmail's limit for a whole message is 25 MB
    public const int MaxMessageBytes = 25 * 1024 * 1024;

    public static byte[] Build(GmailOutgoing m, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(m);
        var total = m.Attachments.Sum(a => (long)a.Data.Length) + Encoding.UTF8.GetByteCount(m.Text) + (m.Html is null ? 0 : Encoding.UTF8.GetByteCount(m.Html));
        if (total > MaxMessageBytes) throw new ArgumentException("The message and its attachments are larger than Gmail's 25 MB limit.");
        var sb = new StringBuilder();
        Header(sb, "From", Addresses(m.From, required: true));
        if (m.To.Trim().Length > 0) Header(sb, "To", Addresses(m.To, required: false));
        if (m.Cc.Trim().Length > 0) Header(sb, "Cc", Addresses(m.Cc, required: false));
        if (m.Bcc.Trim().Length > 0) Header(sb, "Bcc", Addresses(m.Bcc, required: false));
        Header(sb, "Subject", Encode(Single(m.Subject, "subject")));
        Header(sb, "Date", (now ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " +0000");
        Header(sb, "Message-ID", $"<{Guid.NewGuid():N}@openoutlook.local>");
        if (!string.IsNullOrWhiteSpace(m.InReplyTo)) Header(sb, "In-Reply-To", Single(m.InReplyTo!.Trim(), "In-Reply-To"));
        if (!string.IsNullOrWhiteSpace(m.References)) Header(sb, "References", Single(m.References!.Trim(), "References"));
        Header(sb, "MIME-Version", "1.0");

        string mixed = "", alt = "";
        var hasAttachments = m.Attachments.Count > 0;
        var isHtml = m.Html is not null;
        if (hasAttachments) { mixed = "=_oo_mixed_" + Guid.NewGuid().ToString("N"); }
        if (isHtml) { alt = "=_oo_alt_" + Guid.NewGuid().ToString("N"); }

        void TextPart(string type, string content)
        {
            sb.Append("Content-Type: ").Append(type).Append("; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
            Base64Lines(sb, Encoding.UTF8.GetBytes(Normalize(content)));
        }

        void Body()
        {
            if (!isHtml) { TextPart("text/plain", m.Text); return; }
            sb.Append("Content-Type: multipart/alternative; boundary=\"").Append(alt).Append("\"\r\n\r\n");
            sb.Append("--").Append(alt).Append("\r\n"); TextPart("text/plain", m.Text);
            sb.Append("--").Append(alt).Append("\r\n"); TextPart("text/html", m.Html!);
            sb.Append("--").Append(alt).Append("--\r\n");
        }

        if (!hasAttachments) { Body(); return Encoding.ASCII.GetBytes(sb.ToString()); }
        sb.Append("Content-Type: multipart/mixed; boundary=\"").Append(mixed).Append("\"\r\n\r\n");
        sb.Append("--").Append(mixed).Append("\r\n");
        Body();
        foreach (var a in m.Attachments)
        {
            if (a.Data.Length > MaxAttachmentBytes) throw new ArgumentException($"{a.FileName} is larger than Gmail's 25 MB limit.");
            var name = SafeFileName(a.FileName);
            var type = SafeMime(a.MimeType);
            sb.Append("\r\n--").Append(mixed).Append("\r\n");
            sb.Append("Content-Type: ").Append(type).Append("; name=\"").Append(Encode(name)).Append("\"\r\n");
            sb.Append("Content-Disposition: attachment; filename=\"").Append(Encode(name)).Append("\"\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            Base64Lines(sb, a.Data);
        }
        sb.Append("\r\n--").Append(mixed).Append("--\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>base64url without padding, as the Gmail API wants it.</summary>
    public static string ToBase64Url(byte[] data) => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
    }

    // ---- helpers ----

    private static void Header(StringBuilder sb, string name, string value) => sb.Append(name).Append(": ").Append(value).Append("\r\n");

    private static string Single(string value, string what)
    {
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException($"The {what} contains a line break.");
        return value;
    }

    /// <summary>Encodes non-ASCII text as RFC 2047 encoded words (base64, UTF-8, split so that no word exceeds 75 characters).</summary>
    internal static string Encode(string value)
    {
        if (value.All(c => c is >= ' ' and < (char)127)) return value;
        var words = new List<string>();
        var bytes = Encoding.UTF8.GetBytes(value);
        var i = 0;
        while (i < bytes.Length)
        {
            var take = Math.Min(30, bytes.Length - i);
            while (take > 0 && i + take < bytes.Length && (bytes[i + take] & 0xC0) == 0x80) take--;   // never split a UTF-8 character
            if (take == 0) take = Math.Min(bytes.Length - i, 30);
            words.Add("=?utf-8?B?" + Convert.ToBase64String(bytes, i, take) + "?=");
            i += take;
        }
        return string.Join("\r\n ", words);
    }

    /// <summary>"Name &lt;a@b&gt;, c@d" (also ';' separated): validates every address and encodes display names.</summary>
    internal static string Addresses(string list, bool required)
    {
        Single(list, "address list");
        var result = new List<string>();
        foreach (var part in SplitAddresses(list))
        {
            var text = part.Trim();
            if (text.Length == 0) continue;
            string display = "", address = text;
            var lt = text.LastIndexOf('<');
            if (lt >= 0 && text.EndsWith('>')) { display = text[..lt].Trim().Trim('"'); address = text[(lt + 1)..^1].Trim(); }
            if (!ValidAddress(address)) throw new ArgumentException($"\"{address}\" is not a valid email address.");
            result.Add(display.Length == 0 ? address : (display.All(c => c is >= ' ' and < (char)127) ? "\"" + display.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : Encode(display)) + " <" + address + ">");
        }
        if (required && result.Count == 0) throw new ArgumentException("A sender address is required.");
        return string.Join(", ", result);
    }

    private static IEnumerable<string> SplitAddresses(string list)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in list)
        {
            if (c == '"') inQuotes = !inQuotes;
            if ((c == ',' || c == ';') && !inQuotes) { yield return current.ToString(); current.Clear(); }
            else current.Append(c);
        }
        yield return current.ToString();
    }

    internal static bool ValidAddress(string a) =>
        a.Length is >= 3 and <= 254 && a.Count(c => c == '@') == 1 && !a.StartsWith('@') && !a.EndsWith('@') &&
        a.All(c => c is > ' ' and < (char)127 && c is not ('<' or '>' or '"' or ',' or ';' or '(' or ')'));

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    private static void Base64Lines(StringBuilder sb, byte[] data)
    {
        var b64 = Convert.ToBase64String(data);
        for (var i = 0; i < b64.Length; i += 76) sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n");
        if (b64.Length == 0) sb.Append("\r\n");
    }

    private static string SafeFileName(string name)
    {
        var clean = new string((name ?? "").Where(c => c >= ' ' && c != '"' && c != '\\' && c != '/' && c != ':').ToArray()).Trim();
        return clean.Length == 0 ? "attachment" : clean.Length > 200 ? clean[..200] : clean;
    }

    private static string SafeMime(string? type) =>
        type is { Length: > 2 and <= 100 } && type.Count(c => c == '/') == 1 && type.All(c => c is > ' ' and < (char)127 && c is not (';' or '"' or ',')) ? type : "application/octet-stream";
}
