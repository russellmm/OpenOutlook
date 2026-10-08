using System.Globalization;
using System.Text;

namespace PstCore;

/// <summary>
/// Builds a complete internet message (RFC 5322 / MIME) from a message read out of a PST, so it can be filed in a mailbox that only accepts MIME
/// (Gmail import, Microsoft Graph). Unlike the bounded EML export it keeps inline pictures (multipart/related with Content-ID), hidden parts,
/// Unicode in any header, the original Message-ID / threading headers and the importance, and it fails closed only on data it cannot read.
/// </summary>
public static class MimeMessageBuilder
{
    public const long MaximumBytes = 35L * 1024 * 1024;
    private static readonly string[] CarriedHeaders = ["Message-ID", "In-Reply-To", "References", "Reply-To", "Sender"];

    /// <param name="readAttachment">Returns the bytes of an attachment stored by value; throwing marks the message as not transferable.</param>
    public static byte[] Build(MailMessage message, Func<MailAttachment, byte[]> readAttachment)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(readAttachment);
        var inline = new List<(MailAttachment A, byte[] Data)>();
        var files = new List<(MailAttachment A, byte[] Data)>();
        long total = 0;
        foreach (var a in message.Attachments)
        {
            if (a.Method != 1)
                throw new NotSupportedException($"The attachment \"{a.FileName}\" is an embedded item or a link and cannot be transferred.");
            byte[] data;
            try { data = readAttachment(a); }
            catch (Exception e) when (e is not OperationCanceledException) { throw new IOException($"The attachment \"{a.FileName}\" could not be read.", e); }
            total += data.LongLength;
            if (total > MaximumBytes) throw new InvalidDataException("The message is too large to transfer.");
            (string.IsNullOrWhiteSpace(a.ContentId) ? files : inline).Add((a, data));
        }

        var html = message.BodyHtml;
        var text = message.BodyText;
        var sb = new StringBuilder();
        sb.Append("MIME-Version: 1.0\r\n");
        Header(sb, "From", Mailbox(message.Summary.From));
        AddRecipients(sb, message);
        Header(sb, "Subject", Words(message.Summary.Subject));
        var date = message.Summary.Sent != DateTime.MinValue ? message.Summary.Sent : message.Summary.Received;
        if (date != DateTime.MinValue)
        {
            var utc = date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : DateTime.SpecifyKind(date, DateTimeKind.Utc);
            Header(sb, "Date", utc.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture));
        }
        foreach (var (name, value) in OriginalHeaders(message.Headers)) Header(sb, name, value);
        switch (message.Summary.Importance)
        {
            case 2: Header(sb, "Importance", "high"); Header(sb, "X-Priority", "1"); break;
            case 0: Header(sb, "Importance", "low"); Header(sb, "X-Priority", "5"); break;
        }

        // body tree: [mixed [related [alternative | single leaf] inline...] files...]
        var hasText = !string.IsNullOrEmpty(text);
        var hasHtml = !string.IsNullOrEmpty(html);
        Node root;
        if (hasText && hasHtml) root = Multipart("alternative", null, [Leaf("text/plain", text), Leaf("text/html", html)]);
        else root = Leaf(hasHtml ? "text/html" : "text/plain", hasHtml ? html : text);
        if (inline.Count > 0)
        {
            var type = hasText && hasHtml ? "multipart/alternative" : hasHtml ? "text/html" : "text/plain";
            root = Multipart("related", $"type=\"{type}\"", [root, .. inline.Select(i => AttachmentNode(i.A, i.Data, inlinePart: true))]);
        }
        if (files.Count > 0) root = Multipart("mixed", null, [root, .. files.Select(f => AttachmentNode(f.A, f.Data, inlinePart: false))]);
        sb.Append(root.Headers).Append("\r\n").Append(root.Body);
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        if (bytes.LongLength > MaximumBytes) throw new InvalidDataException("The message is too large to transfer.");
        return bytes;
    }

    /// <summary>One MIME entity: its header lines (each ending in CRLF) and its body.</summary>
    private sealed record Node(string Headers, string Body);

    private static Node Leaf(string mediaType, string content)
    {
        var body = new StringBuilder();
        Base64(body, Encoding.UTF8.GetBytes(content));
        return new Node($"Content-Type: {mediaType}; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n", body.ToString());
    }

    private static Node Multipart(string subtype, string? parameters, IReadOnlyList<Node> children)
    {
        var boundary = "=_OpenOutlook_" + Guid.NewGuid().ToString("N");
        var body = new StringBuilder();
        foreach (var child in children) body.Append("--").Append(boundary).Append("\r\n").Append(child.Headers).Append("\r\n").Append(child.Body);
        body.Append("--").Append(boundary).Append("--\r\n");
        return new Node($"Content-Type: multipart/{subtype};{(parameters is null ? "" : " " + parameters + ";")} boundary=\"{boundary}\"\r\n", body.ToString());
    }

    private static Node AttachmentNode(MailAttachment a, byte[] data, bool inlinePart)
    {
        var name = string.IsNullOrWhiteSpace(a.FileName) ? "attachment" : a.FileName.Trim();
        var headers = new StringBuilder();
        headers.Append("Content-Type: ").Append(SafeType(a.MimeTag)).Append("; ").Append(Param("name", name)).Append("\r\n");
        headers.Append("Content-Transfer-Encoding: base64\r\n");
        if (inlinePart) headers.Append("Content-ID: <").Append(a.ContentId.Trim('<', '>', ' ')).Append(">\r\n");
        headers.Append("Content-Disposition: ").Append(inlinePart || a.IsHidden ? "inline" : "attachment").Append("; ").Append(Param("filename", name)).Append("\r\n");
        var body = new StringBuilder();
        Base64(body, data);
        return new Node(headers.ToString(), body.ToString());
    }

    // ---- headers ---------------------------------------------------------------------------------------------------------------------------

    private static void AddRecipients(StringBuilder sb, MailMessage message)
    {
        foreach (var (kind, name) in new[] { (RecipientKind.To, "To"), (RecipientKind.Cc, "Cc"), (RecipientKind.Bcc, "Bcc") })
        {
            var list = message.Recipients.Where(r => r.Kind == kind && !string.IsNullOrWhiteSpace(r.Email)).ToList();
            if (list.Count > 0) Header(sb, name, string.Join(", ", list.Select(r => Format(r.Name, r.Email)).Where(x => x.Length > 0)));
        }
        if (!message.Recipients.Any(r => r.Kind == RecipientKind.To && !string.IsNullOrWhiteSpace(r.Email)) && !string.IsNullOrWhiteSpace(message.Summary.To))
        {
            var to = message.Summary.To;
            if (to.Contains('@')) Header(sb, "To", Mailbox(to));
            else Header(sb, "X-OpenOutlook-To-Display", Words(to));
        }
    }

    /// <summary>Message-ID and threading headers of the original (unfolded), so a copy keeps its place in a conversation.</summary>
    private static IEnumerable<(string Name, string Value)> OriginalHeaders(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(lines[i][0])) continue;
            var name = lines[i][..colon].Trim();
            var wanted = CarriedHeaders.FirstOrDefault(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (wanted is null || !seen.Add(wanted)) continue;
            var value = lines[i][(colon + 1)..].Trim();
            while (i + 1 < lines.Length && lines[i + 1].Length > 0 && char.IsWhiteSpace(lines[i + 1][0])) value += " " + lines[++i].Trim();
            if (value.Length is 0 or > 2000 || value.Any(c => c < ' ' || c > '~')) continue;           // keep only plain ASCII ones: never risk a malformed header
            yield return (wanted, value);
        }
    }

    private static void Header(StringBuilder sb, string name, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append(name).Append(": ").Append(value).Append("\r\n");
    }

    /// <summary>"Name &lt;a@b&gt;" with the display name RFC 2047 encoded when needed; several addresses separated by , or ; are handled one by one.</summary>
    private static string Mailbox(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var parts = SplitAddresses(text).Select(One).Where(s => s.Length > 0);
        return string.Join(", ", parts);

        static string One(string part)
        {
            part = part.Trim();
            var lt = part.LastIndexOf('<');
            var gt = part.LastIndexOf('>');
            if (lt >= 0 && gt > lt) return Format(part[..lt].Trim().Trim('"').Trim(), part[(lt + 1)..gt].Trim());
            return Format("", part);
        }
    }

    /// <summary>One mailbox from a display name and an address; "" when the address is not a plain ASCII address.</summary>
    private static string Format(string? name, string? address)
    {
        address = address?.Trim() ?? "";
        if (!(address.Length > 2 && address.Contains('@') && address.All(c => c > ' ' && c <= '~' && c is not ('<' or '>' or ',' or ';' or '"')))) return "";
        name = name?.Trim() ?? "";
        return name.Length == 0 || name == address ? address : $"{Display(name)} <{address}>";
    }

    private static IEnumerable<string> SplitAddresses(string text)
    {
        var sb = new StringBuilder();
        var quoted = false; var angle = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '<') angle = true;
            else if (!quoted && c == '>') angle = false;
            if (!quoted && !angle && c is ',' or ';') { if (sb.Length > 0) yield return sb.ToString(); sb.Clear(); }
            else sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string Display(string name) =>
        name.All(c => c is >= ' ' and <= '~') ? (name.Any(c => c is '"' or '\\' or ',' or '<' or '>' or '(' or ')' or '@' or ':' or ';' or '.' or '[' or ']') ? "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : name) : Words(name);

    /// <summary>RFC 2047 encoded words (UTF-8, base64), each at most 45 bytes of text, never splitting a character; control characters become spaces.</summary>
    internal static string Words(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var clean = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (clean.All(c => c is >= ' ' and <= '~')) return clean;
        var words = new List<string>();
        var chunk = new List<byte>();
        Span<byte> buf = stackalloc byte[4];
        foreach (var rune in clean.EnumerateRunes())
        {
            var n = rune.EncodeToUtf8(buf);
            if (chunk.Count + n > 45) { words.Add("=?utf-8?B?" + Convert.ToBase64String(chunk.ToArray()) + "?="); chunk.Clear(); }
            for (var i = 0; i < n; i++) chunk.Add(buf[i]);
        }
        if (chunk.Count > 0) words.Add("=?utf-8?B?" + Convert.ToBase64String(chunk.ToArray()) + "?=");
        return string.Join("\r\n ", words);
    }

    private static string Param(string name, string value)
    {
        if (value.All(c => c is >= ' ' and <= '~' && c is not ('"' or '\\')) && value.Length <= 60) return $"{name}=\"{value}\"";
        var bytes = Encoding.UTF8.GetBytes(value);
        var sb = new StringBuilder();
        var pieces = 0;
        for (var offset = 0; offset < bytes.Length;)
        {
            var take = Math.Min(15, bytes.Length - offset);
            while (take > 0 && offset + take < bytes.Length && (bytes[offset + take] & 0xC0) == 0x80) take--;        // do not cut a UTF-8 sequence
            if (take == 0) take = Math.Min(15, bytes.Length - offset);
            if (pieces > 0) sb.Append(";\r\n ");
            sb.Append(name).Append('*').Append(pieces).Append("*=");
            if (pieces == 0) sb.Append("utf-8''");
            for (var i = offset; i < offset + take; i++) sb.Append('%').Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            offset += take; pieces++;
        }
        return sb.ToString();
    }

    private static string SafeType(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 127 || value.Count(c => c == '/') != 1 ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '!' or '#' or '$' or '&' or '^' or '_' or '.' or '+' or '-')) ||
            value.Split('/').Any(p => p.Length == 0))
            return "application/octet-stream";
        return value;
    }

    private static void Base64(StringBuilder to, byte[] data)
    {
        var encoded = Convert.ToBase64String(data);
        for (var i = 0; i < encoded.Length; i += 76) to.Append(encoded, i, Math.Min(76, encoded.Length - i)).Append("\r\n");
        if (encoded.Length == 0) to.Append("\r\n");
    }
}
