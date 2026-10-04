namespace PstCore;

/// <summary>
/// The PST engine contract the UI codes against. <see cref="PstStore"/> (managed) implements it today; a native
/// OpenPST-backed engine will implement it in a later phase. Members mirror <see cref="PstStore"/> exactly.
/// </summary>
public interface IPstEngine : IDisposable
{
    PstHeader Header { get; }
    string DisplayName { get; }
    bool CanWrite { get; }
    MailFolder Root { get; }

    IEnumerable<MailFolder> AllFolders();
    MailFolder? FindFolder(uint nid);
    MailFolder? DeletedItemsFolder();

    IReadOnlyList<MailSummary> GetMessages(MailFolder folder);
    MailMessage OpenMessage(MailSummary summary);
    byte[] ReadAttachmentData(MailSummary message, MailAttachment attachment);
    byte[] ReadAttachmentData(MailSummary message, MailAttachment attachment, int maxBytes,
        CancellationToken cancellationToken = default);
    IReadOnlyList<MailSummary> Search(string query, MailFolder? folder = null);

    void SetReadState(MailSummary message, bool read);
    void SetFlagged(MailSummary message, bool flagged);
    void MoveMessage(MailSummary message, MailFolder destFolder);
    void CopyMessage(MailSummary message, MailFolder destFolder);
    void DeleteMessage(MailSummary message);
    MailFolder CreateFolder(uint parentNid, string name);
    void DeleteFolder(uint folderNid);

    IReadOnlyList<string> VerifyIntegrity();
}
