using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>Presentation-only row; sorting uses raw values while the grid displays readable values.</summary>
public sealed class MessageListRow(MailSummary summary)
{
    public MailSummary Summary { get; } = summary;
    public bool HasAttachment => Summary.HasAttachment;
    public string FromSort => Summary.From;
    public string FromDisplay => Summary.IsRead ? Summary.From : "● " + Summary.From;
    public string Subject => Summary.Subject;
    public DateTime ReceivedSort => Summary.Received == DateTime.MinValue ? Summary.Sent : Summary.Received;
    public string ReceivedDisplay => ReceivedSort == DateTime.MinValue ? "" : ReceivedSort.ToString("g");
    public string DateGroup => MessageDateGroups.Label(ReceivedSort, DateTime.Today);
    public int SizeBytes => Summary.Size;
    public string SizeDisplay => SizeBytes <= 0 ? "" : $"{(SizeBytes + 1023L) / 1024:N0} KB";
}
