using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PstCore;

/// <summary>
/// Reads an RFC 5322 / MIME message (an .eml file) into a <see cref="MailImport"/>: unfolded and RFC 2047-decoded headers, address lists,
/// multipart bodies (alternative / mixed / related), base64 and quoted-printable parts in any charset, attachments and inline pictures.
/// Tolerant by design: mail in the wild is sloppy, so a bad header or charset degrades that field instead of failing the message.
/// </summary>
public static class EmlParser
{
    private const int MaxDepth = 24;
    private const long MaxMessageBytes = 256L * 1024 * 1024;

    static EmlParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static MailImport Parse(Stream stream, bool markRead = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        if (buffer.Length > MaxMessageBytes) throw new InvalidDataException("The message is too large to import.");
        return Parse(buffer.ToArray(), markRead);
    }

    public static MailImport Parse(byte[] raw, bool markRead = false)
    {
        var root = ParsePart(raw, 0, raw.Length, 0);
        var mail = new MailImport { Read = markRead };
        mail.TransportHeaders = root.RawHeaders;
        mail.Subject = Decode(root.Header("subject"));
        var from = ParseAddresses(root.Header("from")).FirstOrDefault();
        if (from is not null) { mail.SenderName = from.Name; mail.SenderEmail = from.Email; }
        else
        {
            var sender = ParseAddresses(root.Header("sender")).FirstOrDefault();
            if (sender is not null) { mail.SenderName = sender.Name; mail.SenderEmail = sender.Email; }
        }
        foreach (var (header, kind) in new[] { ("to", RecipientKind.To), ("cc", RecipientKind.Cc), ("bcc", RecipientKind.Bcc) })
            foreach (var a in ParseAddresses(root.Header(header)))
                mail.Recipients.Add(new ImportRecipient(a.Name, a.Email, kind));
        mail.MessageId = root.Header("message-id").Trim();
        mail.Sent = ParseDate(root.Header("date"));
        mail.Received = ReceivedDate(root) ?? mail.Sent;
        mail.Importance = ImportanceOf(root);
        Walk(root, mail, 0);
        if (string.IsNullOrEmpty(mail.Subject) && string.IsNullOrEmpty(mail.BodyText) && string.IsNullOrEmpty(mail.BodyHtml) &&
            mail.Attachments.Count == 0 && mail.Recipients.Count == 0 && string.IsNullOrEmpty(mail.SenderEmail))
            throw new InvalidDataException("This does not look like an e-mail message.");
        return mail;
    }

    // ------------------------------------------------------------------------------------------------------------------ MIME structure
    private sealed class Part
    {
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<KeyValuePair<string, string>> Ordered { get; } = [];
        public string RawHeaders { get; set; } = "";
        public byte[] Raw { get; set; } = [];
        public int BodyStart { get; set; }
        public int BodyEnd { get; set; }
        public List<Part> Children { get; } = [];
        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : "";
        public ReadOnlySpan<byte> Body => Raw.AsSpan(BodyStart, BodyEnd - BodyStart);
    }

    private static Part ParsePart(byte[] raw, int start, int end, int depth)
    {
        var part = new Part { Raw = raw, BodyStart = end, BodyEnd = end };
        // headers end at the first empty line (CRLF CRLF, LF LF or a mix)
        var pos = start;
        var headerEnd = -1;
        while (pos < end)
        {
            var lineEnd = Array.IndexOf(raw, (byte)'\n', pos, end - pos);
            var next = lineEnd < 0 ? end : lineEnd + 1;
            var lineLength = (lineEnd < 0 ? end : lineEnd) - pos;
            if (lineLength > 0 && raw[pos + lineLength - 1] == '\r') lineLength--;
            if (lineLength == 0) { headerEnd = pos; part.BodyStart = next; break; }
            pos = next;
        }
        if (headerEnd < 0) { headerEnd = end; part.BodyStart = end; }
        part.RawHeaders = DecodeHeaderBytes(raw, start, headerEnd - start).TrimEnd('\r', '\n');
        string? name = null;
        var value = new StringBuilder();
        void Flush()
        {
            if (name is null) return;
            var v = value.ToString().Trim();
            part.Ordered.Add(new KeyValuePair<string, string>(name, v));
            if (!part.Headers.ContainsKey(name)) part.Headers[name] = v;
            name = null; value.Clear();
        }
        foreach (var line in part.RawHeaders.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length > 0 && (l[0] == ' ' || l[0] == '\t')) { if (name is not null) value.Append(' ').Append(l.Trim()); continue; }
            Flush();
            var colon = l.IndexOf(':');
            if (colon <= 0) continue;
            name = l[..colon].Trim().ToLowerInvariant();
            value.Append(l[(colon + 1)..].Trim());
        }
        Flush();
        part.BodyEnd = end;
        var type = ContentType(part).Type;
        if (depth < MaxDepth && type.StartsWith("multipart/", StringComparison.Ordinal))
        {
            var boundary = Param(part.Header("content-type"), "boundary");
            if (!string.IsNullOrEmpty(boundary)) SplitMultipart(part, raw, boundary, depth);
        }
        return part;
    }

    /// <summary>Header bytes are 7-bit ASCII in theory; in practice they are often raw UTF-8 (RFC 6532) or Windows-1252.</summary>
    private static string DecodeHeaderBytes(byte[] raw, int start, int length)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw, start, length); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1252).GetString(raw, start, length); }
    }

    private static void SplitMultipart(Part part, byte[] raw, string boundary, int depth)
    {
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var segments = new List<(int Start, int End)>();
        var pos = part.BodyStart;
        var partStart = -1;
        while (pos <= part.BodyEnd)
        {
            var lineEnd = pos < part.BodyEnd ? Array.IndexOf(raw, (byte)'\n', pos, part.BodyEnd - pos) : -1;
            var next = lineEnd < 0 ? part.BodyEnd + 1 : lineEnd + 1;
            var contentEnd = lineEnd < 0 ? part.BodyEnd : lineEnd;
            if (pos < part.BodyEnd && StartsWith(raw, pos, contentEnd, delimiter))
            {
                if (partStart >= 0)
                {
                    var e = pos;                                            // the line break before the delimiter belongs to the delimiter
                    if (e > partStart && raw[e - 1] == '\n') e--;
                    if (e > partStart && raw[e - 1] == '\r') e--;
                    segments.Add((partStart, e));
                }
                var rest = pos + delimiter.Length;
                if (rest + 1 < contentEnd + 1 && rest + 1 < raw.Length && raw[rest] == '-' && raw[rest + 1] == '-') { partStart = -1; break; }
                partStart = Math.Min(next, part.BodyEnd);
            }
            if (lineEnd < 0) break;
            pos = next;
        }
        if (partStart >= 0 && partStart <= part.BodyEnd) segments.Add((partStart, part.BodyEnd));      // no closing delimiter
        foreach (var (s, e) in segments)
            if (e >= s) part.Children.Add(ParsePart(raw, s, e, depth + 1));
    }

    private static bool StartsWith(byte[] raw, int pos, int end, byte[] prefix)
    {
        if (end - pos < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++) if (raw[pos + i] != prefix[i]) return false;
        return true;
    }

    private static (string Type, string Charset) ContentType(Part part)
    {
        var header = part.Header("content-type");
        if (string.IsNullOrWhiteSpace(header)) return ("text/plain", "");
        var semicolon = header.IndexOf(';');
        var type = (semicolon < 0 ? header : header[..semicolon]).Trim().ToLowerInvariant();
        return (type.Length == 0 ? "text/plain" : type, Param(header, "charset"));
    }

    // ------------------------------------------------------------------------------------------------------------------ bodies and attachments
    private static void Walk(Part part, MailImport mail, int depth)
    {
        var (type, charset) = ContentType(part);
        if (part.Children.Count > 0)
        {
            // multipart/alternative: the parts are renditions of one body, so each text kind is taken once; other multiparts are walked in order
            foreach (var child in part.Children) Walk(child, mail, depth + 1);
            return;
        }
        var disposition = part.Header("content-disposition");
        var isAttachmentDisposition = disposition.StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
        var fileName = FileNameOf(part);
        var contentId = part.Header("content-id").Trim().Trim('<', '>');
        var isText = type is "text/plain" or "text/html";
        if (isText && !isAttachmentDisposition && fileName.Length == 0)
        {
            var text = DecodeText(part, charset);
            if (type == "text/html") mail.BodyHtml = mail.BodyHtml.Length == 0 ? text : mail.BodyHtml + text;
            else mail.BodyText = mail.BodyText.Length == 0 ? text : mail.BodyText + "\r\n" + text;
            return;
        }
        if (type.StartsWith("multipart/", StringComparison.Ordinal)) return;           // a multipart without a usable boundary
        var data = DecodeBody(part);
        if (fileName.Length == 0)
        {
            var extension = type switch
            {
                "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "message/rfc822" => ".eml",
                "text/calendar" => ".ics", "text/plain" => ".txt", "text/html" => ".html", "application/pdf" => ".pdf", _ => ".dat"
            };
            fileName = (contentId.Length > 0 ? SafeName(contentId.Split('@')[0]) : "attachment" + (mail.Attachments.Count + 1)) + extension;
        }
        mail.Attachments.Add(new ImportAttachment(fileName, type, contentId, data));
    }

    private static string FileNameOf(Part part)
    {
        var name = Param(part.Header("content-disposition"), "filename");
        if (name.Length == 0) name = Param(part.Header("content-type"), "name");
        return SafeName(Decode(name));
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    private static byte[] DecodeBody(Part part)
    {
        var encoding = part.Header("content-transfer-encoding").Trim().ToLowerInvariant();
        var body = part.Body;
        switch (encoding)
        {
            case "base64":
            {
                var clean = new StringBuilder(body.Length);
                foreach (var b in body) if (b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'+' or (byte)'/') clean.Append((char)b);
                while (clean.Length % 4 != 0) clean.Append('=');
                try { return Convert.FromBase64String(clean.ToString()); }
                catch (FormatException) { return body.ToArray(); }
            }
            case "quoted-printable": return DecodeQuotedPrintable(body);
            default: return body.ToArray();
        }
    }

    private static byte[] DecodeQuotedPrintable(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var b = input[i];
            if (b != '=') { output.Add(b); continue; }
            if (i + 1 < input.Length && (input[i + 1] == '\r' || input[i + 1] == '\n'))        // soft line break
            {
                i++;
                if (input[i] == '\r' && i + 1 < input.Length && input[i + 1] == '\n') i++;
                continue;
            }
            if (i + 2 < input.Length && Uri.IsHexDigit((char)input[i + 1]) && Uri.IsHexDigit((char)input[i + 2]))
            {
                output.Add(Convert.ToByte(((char)input[i + 1]).ToString() + (char)input[i + 2], 16));
                i += 2;
            }
            else output.Add(b);
        }
        return output.ToArray();
    }

    private static string DecodeText(Part part, string charset)
    {
        var bytes = DecodeBody(part);
        return GetEncoding(charset, bytes).GetString(bytes);
    }

    private static Encoding GetEncoding(string charset, byte[] sample)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset.Trim().Trim('"')); }
            catch (ArgumentException) { /* unknown charset: guess below */ }
        }
        // no (usable) charset: UTF-8 when the bytes are valid UTF-8, else Windows-1252
        try { new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(sample); return Encoding.UTF8; }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1252); }
    }

    // ------------------------------------------------------------------------------------------------------------------ header values
    /// <summary>The value of a header parameter (name="value", name=value, and RFC 2231 name*=charset''encoded / name*0= continuations).</summary>
    private static string Param(string header, string name)
    {
        if (string.IsNullOrEmpty(header)) return "";
        var segments = new SortedDictionary<int, string>();
        string charset = "";
        foreach (Match m in Regex.Matches(header, @"(?:^|;)\s*([A-Za-z0-9_\-]+)(\*\d+)?(\*)?\s*=\s*(""(?:[^""\\]|\\.)*""|[^;]*)", RegexOptions.CultureInvariant))
        {
            if (!m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var value = m.Groups[4].Value.Trim();
            if (value.Length >= 2 && value[0] == '"') value = Regex.Replace(value[1..^1], @"\\(.)", "$1");
            var index = m.Groups[2].Success ? int.Parse(m.Groups[2].Value[1..], CultureInfo.InvariantCulture) : 0;
            if (m.Groups[3].Success)                                                      // extended: charset'lang'percent-encoded
            {
                var parts = value.Split('\'', 3);
                if (parts.Length == 3) { if (index == 0) charset = parts[0]; value = PercentDecode(parts[2], charset); }
                else value = PercentDecode(value, charset);
            }
            segments[index] = value;
        }
        return string.Concat(segments.Values);
    }

    private static string PercentDecode(string value, string charset)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length + 0 && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            { bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16)); i += 2; }
            else bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
        }
        var array = bytes.ToArray();
        return GetEncoding(charset, array).GetString(array);
    }

    /// <summary>Decodes RFC 2047 encoded-words (=?charset?B|Q?text?=) inside a header value.</summary>
    public static string Decode(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("=?", StringComparison.Ordinal)) return value;
        // whitespace between two adjacent encoded-words is not part of the text
        var collapsed = Regex.Replace(value, @"(\?=)\s+(=\?)", "$1$2");
        return Regex.Replace(collapsed, @"=\?([^?\s]+)\?([BbQq])\?([^?]*)\?=", match =>
        {
            try
            {
                var charset = match.Groups[1].Value;
                var star = charset.IndexOf('*');
                if (star >= 0) charset = charset[..star];
                var text = match.Groups[3].Value;
                byte[] bytes;
                if (match.Groups[2].Value is "B" or "b")
                {
                    var padded = text.PadRight((text.Length + 3) / 4 * 4, '=');
                    bytes = Convert.FromBase64String(padded);
                }
                else bytes = DecodeQuotedPrintable(Encoding.ASCII.GetBytes(text.Replace('_', ' ')));
                return GetEncoding(charset, bytes).GetString(bytes);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { return match.Value; }
        });
    }

    private sealed record Address(string Name, string Email);

    /// <summary>Splits an address list ("Name" &lt;a@b&gt;, c@d, group: x@y;) into display name and address pairs.</summary>
    private static IEnumerable<Address> ParseAddresses(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) yield break;
        var text = Decode(header);
        var items = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '(') depth++;
            else if (!quoted && c == ')' && depth > 0) depth--;
            if (!quoted && depth == 0 && (c == ',' || c == ';')) { items.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        items.Add(current.ToString());
        foreach (var raw in items)
        {
            var item = raw.Trim();
            var colon = item.IndexOf(':');
            if (colon >= 0)                                                                    // "group: a@b" - drop the group label
            {
                var label = item[..colon];
                if (!label.Contains('@', StringComparison.Ordinal) && !label.Contains('<')) item = item[(colon + 1)..].Trim();
            }
            if (item.Length == 0) continue;
            var lt = item.LastIndexOf('<');
            var gt = item.LastIndexOf('>');
            string name, email;
            if (lt >= 0 && gt > lt)
            {
                email = item[(lt + 1)..gt].Trim();
                name = item[..lt].Trim();
            }
            else
            {
                email = Regex.Replace(item, @"\([^)]*\)", "").Trim();
                var comment = Regex.Match(item, @"\(([^)]*)\)");
                name = comment.Success ? comment.Groups[1].Value.Trim() : "";
            }
            name = name.Trim().Trim('"').Replace("\\\"", "\"").Trim();
            if (email.Length == 0 && name.Length == 0) continue;
            if (!email.Contains('@', StringComparison.Ordinal) && name.Contains('@', StringComparison.Ordinal)) { email = name; name = ""; }
            yield return new Address(name, email);
        }
    }

    private static DateTime? ParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = Regex.Replace(value, @"\([^)]*\)", "").Trim();
        cleaned = Regex.Replace(cleaned, @"\s+(UT|GMT|Z)$", " +0000", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+(EST)$", " -0500", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+(EDT)$", " -0400", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+(CST)$", " -0600", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+(CDT)$", " -0500", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+(MST)$", " -0700", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+(MDT)$", " -0600", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+(PST)$", " -0800", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+(PDT)$", " -0700", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"([+-]\d{2}):(\d{2})$", "$1$2");
        if (DateTimeOffset.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.UtcDateTime;
        return null;
    }

    /// <summary>When the message arrived: the date after the semicolon of the newest (first) Received header.</summary>
    private static DateTime? ReceivedDate(Part root)
    {
        foreach (var (name, value) in root.Ordered)
        {
            if (name != "received") continue;
            var semicolon = value.LastIndexOf(';');
            if (semicolon < 0) continue;
            var date = ParseDate(value[(semicolon + 1)..]);
            if (date is not null) return date;
        }
        return null;
    }

    private static int ImportanceOf(Part root)
    {
        var importance = root.Header("importance").Trim().ToLowerInvariant();
        if (importance == "high") return 2;
        if (importance == "low") return 0;
        var priority = root.Header("x-priority").Trim();
        if (priority.Length > 0 && char.IsDigit(priority[0]))
        {
            var p = priority[0] - '0';
            return p <= 2 ? 2 : p >= 4 ? 0 : 1;
        }
        return 1;
    }
}
