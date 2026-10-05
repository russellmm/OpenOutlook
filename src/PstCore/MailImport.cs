namespace PstCore;

/// <summary>A recipient of a message being imported. Kind: To, Cc or Bcc.</summary>
public sealed record ImportRecipient(string Name, string Email, RecipientKind Kind);

/// <summary>An attachment of a message being imported. ContentId (no angle brackets) marks an inline picture the HTML body refers to by cid:.</summary>
public sealed record ImportAttachment(string FileName, string MimeType, string ContentId, byte[] Data, bool Hidden = false, DateTime? Modified = null);

/// <summary>
/// A message in neutral form: what an EML file, a Graph message or another mail store provides. Any engine can file one into a folder
/// (<see cref="IPstEngine.ImportMessage"/>); times are UTC.
/// </summary>
public sealed class MailImport
{
    public string MessageClass { get; set; } = "IPM.Note";
    public string Subject { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string BodyText { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    /// <summary>The internet headers as received, when known (the Headers view of the reading pane shows them).</summary>
    public string TransportHeaders { get; set; } = "";
    public string MessageId { get; set; } = "";
    public DateTime? Sent { get; set; }
    public DateTime? Received { get; set; }
    /// <summary>0 low, 1 normal, 2 high.</summary>
    public int Importance { get; set; } = 1;
    public bool Read { get; set; }
    public List<ImportRecipient> Recipients { get; } = [];
    public List<ImportAttachment> Attachments { get; } = [];
}
