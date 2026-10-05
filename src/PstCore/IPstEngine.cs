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

    /// <summary>Full structural scan (works on read-only archives): the findings, one line each, and how many
    /// inconsistencies the built-in repair can fix.</summary>
    PstScanReport Scan();

    /// <summary>Applies the built-in repairs (the "minor inconsistencies" SCANPST reports in edited files) as one
    /// atomic transaction, then rescans. Throws when the archive is read-only.</summary>
    PstScanReport Repair();
}

/// <param name="Findings">One line per problem the scan found; empty when the archive is clean.</param>
/// <param name="Fixable">Inconsistencies the built-in repair can fix (before a repair).</param>
/// <param name="Fixed">Inconsistencies a repair just fixed (0 for a plain scan).</param>
public sealed record PstScanReport(IReadOnlyList<string> Findings, int Fixable, int Fixed);
