using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>What the reading pane shows above a PST message: recipient lines and the attachment strip, built from the native engine's
/// full recipient list and attachment metadata (the old engine only had the To display string and file names).</summary>
public static class PstMessageHeader
{
    /// <summary>"To   a; b" plus "Cc   c" and "Bcc   d" lines (a line is omitted when empty). Falls back to the summary's To text.</summary>
    public static string Recipients(MailMessage message)
    {
        string Join(RecipientKind kind) => string.Join("; ", message.Recipients
            .Where(r => r.Kind == kind)
            .Select(r => !string.IsNullOrWhiteSpace(r.Name) ? r.Name.Trim() : r.Email.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        var lines = new List<string>();
        var to = Join(RecipientKind.To);
        if (to.Length == 0) to = message.Summary.To;
        if (to.Length > 0) lines.Add($"To   {to}");
        var cc = Join(RecipientKind.Cc);
        if (cc.Length > 0) lines.Add($"Cc   {cc}");
        var bcc = Join(RecipientKind.Bcc);
        if (bcc.Length > 0) lines.Add($"Bcc   {bcc}");
        return lines.Count == 0 ? "To   " : string.Join("\n", lines);
    }

    /// <summary>Attachments Outlook lists under the header: not hidden, and not an inline picture the body itself shows (cid: reference).</summary>
    public static IReadOnlyList<MailAttachment> VisibleAttachments(MailMessage message) =>
        message.Attachments.Where(a => !a.IsHidden && !IsInline(a, message.BodyHtml)).ToList();

    private static bool IsInline(MailAttachment attachment, string html)
    {
        var id = attachment.ContentId?.Trim().Trim('<', '>');
        return !string.IsNullOrEmpty(id) && html.Contains("cid:" + id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Attachments: report.pdf (1.2 MB), photo.jpg (310 KB)" or "" when there is nothing to list.</summary>
    public static string AttachmentLine(IReadOnlyList<MailAttachment> attachments) =>
        attachments.Count == 0 ? "" : "Attachments: " + string.Join(", ", attachments.Select(a =>
            a.Size > 0 ? $"{a.FileName} ({FormatSize(a.Size)})" : a.FileName));

    /// <summary>
    /// The text of the Headers view: the internet headers as stored, or - for messages that never travelled by mail (drafts, notes,
    /// calendar items, copies written by other tools) - the key properties, with a line saying so.
    /// </summary>
    public static string HeadersText(MailMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Headers)) return message.Headers.Replace("\r\n", "\n").TrimEnd();
        var s = message.Summary;
        string Date(DateTime t) => t == DateTime.MinValue ? "" : t.ToString("ddd, dd MMM yyyy HH:mm:ss");
        var lines = new List<string> { "(This message has no internet headers; showing its stored properties.)", "" };
        void Add(string name, string value) { if (!string.IsNullOrWhiteSpace(value)) lines.Add($"{name}: {value}"); }
        Add("From", s.From);
        foreach (var kind in new[] { RecipientKind.To, RecipientKind.Cc, RecipientKind.Bcc })
            Add(kind.ToString(), string.Join(", ", message.Recipients.Where(r => r.Kind == kind).Select(r => r.ToString())));
        Add("Subject", s.Subject);
        Add("Sent", Date(s.Sent));
        Add("Received", Date(s.Received));
        Add("Message-Class", s.MessageClass);
        Add("Importance", s.Importance switch { 0 => "Low", 2 => "High", _ => "Normal" });
        Add("Size", FormatSize(s.Size));
        Add("Body formats", string.Join(", ", new[] { message.HasHtml ? "HTML" : "", message.HasRtf ? "Rich Text" : "", message.HasText ? "Plain Text" : "" }.Where(x => x.Length > 0)));
        return string.Join("\n", lines);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{Math.Max(1, (bytes + 512) / 1024)} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
    };
}
