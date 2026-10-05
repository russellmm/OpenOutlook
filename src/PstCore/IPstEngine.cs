namespace PstCore;

/// <summary>
/// The PST engine contract the UI codes against. The native OpenPST library implements it (OpenOutlook.PstNative.NativePstEngine).
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

    /// <summary>
    /// Files messages (EML files, Graph messages, ...) into a folder and returns their summaries. Large batches are written in several
    /// atomic transactions (each is all-or-nothing; a cancel or failure keeps the transactions already committed).
    /// </summary>
    /// <summary>
    /// Copies messages of this archive into a folder of ANOTHER open archive (both editable-native), translating named properties.
    /// Returns the new summaries in the destination; the originals are untouched (a "move" deletes them afterwards).
    /// </summary>
    IReadOnlyList<MailSummary> CopyMessagesTo(IPstEngine destination, MailFolder destFolder, IReadOnlyList<MailSummary> messages);

    IReadOnlyList<MailSummary> ImportMessages(MailFolder folder, IReadOnlyList<MailImport> messages,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default);
    void DeleteFolder(uint folderNid);

    IReadOnlyList<string> VerifyIntegrity();
}
