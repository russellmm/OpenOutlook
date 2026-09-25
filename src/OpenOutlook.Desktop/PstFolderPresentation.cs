using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>Keep PST bookkeeping folders out of the normal mailbox tree without changing the archive.</summary>
public static class PstFolderPresentation
{
    public static IReadOnlyList<MailFolder> VisibleRoots(MailFolder root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var mailbox = root.Children.FirstOrDefault(folder =>
            folder.Name.Equals("Top of Outlook data file", StringComparison.OrdinalIgnoreCase));
        if (mailbox is not null)
        {
            // A nonempty wrapper remains reachable rather than silently hiding its messages.
            return mailbox.ContentCount > 0
                ? [mailbox]
                : mailbox.Children.ToArray();
        }
        return root.Children.Where(folder =>
            folder.ContentCount > 0 || folder.Children.Count > 0 ||
            (!folder.Name.Equals("IPM_COMMON_VIEWS", StringComparison.OrdinalIgnoreCase) &&
             !folder.Name.Equals("Search Root", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}
