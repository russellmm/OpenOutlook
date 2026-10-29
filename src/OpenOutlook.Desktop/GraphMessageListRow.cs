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
            nameof(SizeBytes), nameof(SizeDisplay), nameof(IsUnread), nameof(TextWeight),
            nameof(ImportanceSort), nameof(ImportanceGlyph), nameof(ImportanceBrush), nameof(ImportanceName), nameof(IsFlagged), nameof(FlagName), nameof(AttachmentName) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public bool HasAttachment => Message.HasAttachments;
    public string FromSort => Message.From;
    public bool IsUnread => !Message.IsRead;

    /// <summary>Classic Outlook marks unread mail by bolding the row, not with a marker glyph.</summary>
    public Avalonia.Media.FontWeight TextWeight => IsUnread ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal;

    public string FromDisplay => Message.From;
    public string Subject => (Message.IsDraft ? "[Draft] " : "") +
        (Message.IsFlagged ? "⚑ " : "") + Message.Subject;
    public DateTime ReceivedSort => Message.Received?.LocalDateTime ?? DateTime.MinValue;
    public string ReceivedDisplay => Message.Received?.ToLocalTime().ToString("g") ?? "";
    public string DateGroup => MessageDateGroups.Label(ReceivedSort, DateTime.Today);
    public int ImportanceSort => Message.Importance;
    public string ImportanceGlyph => ImportanceSort switch { 2 => "!", 0 => "\u2193", _ => "" };
    public Avalonia.Media.IBrush ImportanceBrush => ImportanceSort == 0 ? Avalonia.Media.Brushes.SteelBlue : Avalonia.Media.Brushes.Firebrick;
    public string ImportanceName => ImportanceSort switch { 2 => "High importance", 0 => "Low importance", _ => "Normal importance" };
    public bool IsFlagged => Message.IsFlagged;
    public string FlagName => IsFlagged ? "Flagged" : "No flag";
    public string AttachmentName => HasAttachment ? "With attachments" : "No attachments";
    public int? SizeBytes => Message.SizeBytes;

    /// <summary>Why the Inbox spam check suspects this message (MainWindow.SpamCheck.cs); null = not suspected.</summary>
    public IReadOnlyList<string>? SpamReasons { get; private set; }
    public bool IsSpamSuspect => SpamReasons is { Count: > 0 };
    public string SpamTip => IsSpamSuspect ? "Suspected spam: " + string.Join(", ", SpamReasons!) + ". Untick if it is not spam." : "";
    private bool _spamTicked;

    /// <summary>The first column's check box: ticked = included in Block Sender and Delete / Delete.</summary>
    public bool IsSpamTicked
    {
        get => _spamTicked;
        set
        {
            if (_spamTicked == value) return;
            _spamTicked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpamTicked)));
        }
    }

    internal void SetSpam(IReadOnlyList<string>? reasons, bool ticked)
    {
        if (ReferenceEquals(SpamReasons, reasons) && _spamTicked == ticked) return;
        SpamReasons = reasons is { Count: > 0 } ? reasons : null;
        IsSpamTicked = IsSpamSuspect && ticked;
        foreach (var name in new[] { nameof(SpamReasons), nameof(IsSpamSuspect), nameof(SpamTip) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public string SizeDisplay => SizeBytes is null ? "" : $"{(SizeBytes.Value + 1023L) / 1024:N0} KB";
}
