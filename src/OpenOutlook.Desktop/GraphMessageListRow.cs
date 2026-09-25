using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

public sealed class GraphMessageListRow(GraphInboxMessage message)
{
    public GraphInboxMessage Message { get; } = message;
    public bool HasAttachment => Message.HasAttachments;
    public string FromSort => Message.From;
    public string FromDisplay => Message.IsRead ? Message.From : "● " + Message.From;
    public string Subject => Message.Subject;
    public DateTime ReceivedSort => Message.Received?.LocalDateTime ?? DateTime.MinValue;
    public string ReceivedDisplay => Message.Received?.ToLocalTime().ToString("g") ?? "";
    public string DateGroup => MessageDateGroups.Label(ReceivedSort, DateTime.Today);
    public int? SizeBytes => Message.SizeBytes;
    public string SizeDisplay => SizeBytes is null ? "" : $"{(SizeBytes.Value + 1023L) / 1024:N0} KB";
}
