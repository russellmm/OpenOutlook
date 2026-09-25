using System.Net;
using System.Text;

namespace PstCore;

public static class EmlExport
{
    public static string ToEml(PstStore store, MailMessage message)
    {
        var sb = new StringBuilder();
        var headers = message.Headers?.Trim();
        if (!string.IsNullOrWhiteSpace(headers))
        {
            sb.AppendLine(headers.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            if (!headers.EndsWith("\n")) sb.AppendLine();
        }
        else
        {
            sb.AppendLine($"From: {message.Summary.From}");
            sb.AppendLine($"To: {message.Summary.To}");
            sb.AppendLine($"Subject: {EncodeHeader(message.Summary.Subject)}");
            if (message.Summary.Received != DateTime.MinValue)
                sb.AppendLine($"Date: {message.Summary.Received:R}");
            sb.AppendLine("MIME-Version: 1.0");
        }

        var boundary = "----=_PstMail_" + Guid.NewGuid().ToString("N");
        var hasAttach = message.Attachments.Count > 0;
        if (hasAttach)
            sb.AppendLine($"Content-Type: multipart/mixed; boundary=\"{boundary}\"");
        sb.AppendLine();

        if (hasAttach)
        {
            sb.AppendLine($"--{boundary}");
            AppendBody(sb, message);
            foreach (var att in message.Attachments)
            {
                sb.AppendLine($"--{boundary}");
                sb.AppendLine($"Content-Type: {SafeMime(att.MimeTag)}; name=\"{att.FileName}\"");
                sb.AppendLine("Content-Transfer-Encoding: base64");
                sb.AppendLine($"Content-Disposition: attachment; filename=\"{att.FileName}\"");
                if (!string.IsNullOrWhiteSpace(att.ContentId))
                    sb.AppendLine($"Content-ID: <{att.ContentId}>");
                sb.AppendLine();
                byte[] bytes;
                try { bytes = store.ReadAttachmentData(message.Summary, att); }
                catch { bytes = []; }
                sb.AppendLine(Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks));
            }
            sb.AppendLine($"--{boundary}--");
        }
        else
        {
            AppendBody(sb, message);
        }

        return sb.ToString();
    }

    public static void SaveMessage(PstStore store, MailMessage message, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".html" or ".htm")
        {
            File.WriteAllText(path, message.DisplayBody, Encoding.UTF8);
            return;
        }
        if (ext is ".txt")
        {
            var text = string.IsNullOrWhiteSpace(message.BodyText)
                ? HtmlToText(message.BodyHtml)
                : message.BodyText;
            File.WriteAllText(path, text, Encoding.UTF8);
            return;
        }
        File.WriteAllText(path, ToEml(store, message), Encoding.UTF8);
    }

    public static int SaveFolder(PstStore store, MailFolder folder, string directory, bool recursive)
    {
        Directory.CreateDirectory(directory);
        var count = 0;
        foreach (var summary in store.GetMessages(folder))
        {
            try
            {
                var msg = store.OpenMessage(summary);
                var name = Sanitize(string.IsNullOrWhiteSpace(summary.Subject) ? $"message-{summary.Nid:X}" : summary.Subject);
                var path = UniquePath(Path.Combine(directory, name + ".eml"));
                SaveMessage(store, msg, path);
                count++;
            }
            catch
            {
                // skip unreadable
            }
        }
        if (recursive)
        {
            foreach (var child in folder.Children)
                count += SaveFolder(store, child, Path.Combine(directory, Sanitize(child.Name)), true);
        }
        return count;
    }

    private static void AppendBody(StringBuilder sb, MailMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.BodyHtml))
        {
            sb.AppendLine("Content-Type: text/html; charset=\"utf-8\"");
            sb.AppendLine("Content-Transfer-Encoding: 8bit");
            sb.AppendLine();
            sb.AppendLine(message.BodyHtml);
        }
        else
        {
            sb.AppendLine("Content-Type: text/plain; charset=\"utf-8\"");
            sb.AppendLine("Content-Transfer-Encoding: 8bit");
            sb.AppendLine();
            sb.AppendLine(message.BodyText);
        }
    }

    private static string EncodeHeader(string value)
    {
        if (string.IsNullOrEmpty(value) || value.All(c => c < 128)) return value;
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return $"=?utf-8?B?{b64}?=";
    }

    private static string SafeMime(string mime) =>
        string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime;

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim();
        if (name.Length > 80) name = name[..80];
        return string.IsNullOrWhiteSpace(name) ? "item" : name;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var t = System.Text.RegularExpressions.Regex.Replace(html, "<script[\\s\\S]*?</script>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, "<style[\\s\\S]*?</style>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, "<br\\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, "</p>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, "<[^>]+>", "");
        return WebUtility.HtmlDecode(t);
    }
}
