using System.ComponentModel;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>Presentation-only row; sorting uses raw values while the grid displays readable values.</summary>
public sealed class MessageListRow(MailSummary summary) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public MailSummary Summary { get; } = summary;
    public bool HasAttachment => Summary.HasAttachment;
    public string FromSort => Summary.From;

    /// <summary>Local read state, seeded from the archive (plus any stored override) and updated in
    /// place when the Reading Pane marks the item as read.</summary>
    public bool IsRead { get; private set; } = summary.IsRead;

    public void SetRead(bool read)
    {
        if (IsRead == read) return;
        IsRead = read;
        Summary.IsRead = read;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUnread)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextWeight)));
    }

    public bool IsUnread => !IsRead;

    /// <summary>Classic Outlook marks unread mail by bolding the row, not with a marker glyph.</summary>
    public Avalonia.Media.FontWeight TextWeight => IsUnread ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal;

    public string FromDisplay => Summary.From;
    public string Subject => Summary.Subject;
    public DateTime ReceivedSort => Summary.Received == DateTime.MinValue ? Summary.Sent : Summary.Received;
    public string ReceivedDisplay => ReceivedSort == DateTime.MinValue ? "" : ReceivedSort.ToString("g");
    public string DateGroup => MessageDateGroups.Label(ReceivedSort, DateTime.Today);
    public int SizeBytes => Summary.Size;
    public string SizeDisplay => SizeBytes <= 0 ? "" : $"{(SizeBytes + 1023L) / 1024:N0} KB";
}
