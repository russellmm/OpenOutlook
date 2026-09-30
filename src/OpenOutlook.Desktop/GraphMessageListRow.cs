using System.ComponentModel;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

public sealed class GraphMessageListRow(GraphInboxMessage message) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public GraphInboxMessage Message { get; private set; } = message;
    public void Update(GraphInboxMessage message)
    {
        if (Message == message) return;
        Message = message;
        foreach (var name in new[] { nameof(HasAttachment), nameof(FromSort), nameof(FromDisplay),
            nameof(Subject), nameof(ReceivedSort), nameof(ReceivedDisplay), nameof(DateGroup),
            nameof(SizeBytes), nameof(SizeDisplay) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public bool HasAttachment => Message.HasAttachments;
    public string FromSort => Message.From;
    public string FromDisplay => Message.IsRead ? Message.From : "● " + Message.From;
    public string Subject => (Message.IsDraft ? "[Draft] " : "") +
        (Message.IsFlagged ? "⚑ " : "") + Message.Subject;
    public DateTime ReceivedSort => Message.Received?.LocalDateTime ?? DateTime.MinValue;
    public string ReceivedDisplay => Message.Received?.ToLocalTime().ToString("g") ?? "";
    public string DateGroup => MessageDateGroups.Label(ReceivedSort, DateTime.Today);
    public int? SizeBytes => Message.SizeBytes;
    public string SizeDisplay => SizeBytes is null ? "" : $"{(SizeBytes.Value + 1023L) / 1024:N0} KB";
}
