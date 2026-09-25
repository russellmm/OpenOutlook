using System.Text;

namespace PstCore;

public sealed class MailFolder
{
    public required uint Nid { get; init; }
    public required string Name { get; init; }
    public required uint ParentNid { get; init; }
    public int ContentCount { get; init; }
    public int UnreadCount { get; init; }
    public bool HasSubfolders { get; init; }
    public List<MailFolder> Children { get; } = [];
    public override string ToString() => Name;
}

public sealed class MailSummary
{
    public required uint Nid { get; init; }
    public required uint FolderNid { get; set; }
    public string Subject { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public DateTime Received { get; init; }
    public DateTime Sent { get; init; }
    public bool IsRead { get; set; }
    public bool HasAttachment { get; init; }
    public bool Flagged { get; set; }
    public int Size { get; init; }
    public string MessageClass { get; init; } = "";
    public int Importance { get; init; }
}

public sealed class MailRecipient
{
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public RecipientKind Kind { get; init; }
    public override string ToString() => string.IsNullOrWhiteSpace(Email) ? Name : $"{Name} <{Email}>".Trim();
}

public enum RecipientKind
{
    To = 1,
    Cc = 2,
    Bcc = 3
}

public sealed class MailAttachment
{
    public required uint Nid { get; init; }
    public string FileName { get; init; } = "";
    public string MimeTag { get; init; } = "";
    public int Size { get; init; }
    public int Method { get; init; }
    public string ContentId { get; init; } = "";
    public bool IsHidden { get; init; }
}

public sealed class MailMessage
{
    public required MailSummary Summary { get; init; }
    public string BodyText { get; init; } = "";
    public string BodyHtml { get; init; } = "";
    public string Headers { get; init; } = "";
    public IReadOnlyList<MailRecipient> Recipients { get; init; } = [];
    public IReadOnlyList<MailAttachment> Attachments { get; init; } = [];
    public string DisplayBody
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(BodyHtml)) return BodyHtml;
            if (!string.IsNullOrWhiteSpace(BodyText)) return WebUtilityLite.PlainToHtml(BodyText);
            return "<html><body><i>(No body)</i></body></html>";
        }
    }
}

internal static class WebUtilityLite
{
    public static string PlainToHtml(string text)
    {
        var enc = System.Net.WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br/>\n");
        return $"<html><body style=\"font-family:Segoe UI,sans-serif;font-size:14px\">{enc}</body></html>";
    }
}

internal static class MailFlags
{
    public const int Read = 0x0001;
    public const int HasAttach = 0x0010;
}

internal static class StringProps
{
    public static string Best(PropertyBag bag, Encoding? ansi, params ushort[] ids)
    {
        foreach (var id in ids)
        {
            var s = bag.GetString(id, ansi);
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
        }
        return "";
    }
}
