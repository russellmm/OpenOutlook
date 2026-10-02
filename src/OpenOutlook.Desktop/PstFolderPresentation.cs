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
            var visible = mailbox.ContentCount > 0
                ? new List<MailFolder> { mailbox }
                : mailbox.Children.ToList();
            // Folders created at the archive root (siblings of the wrapper) belong in the same
            // list - otherwise pickers and panes silently hide exactly the folders users just made.
            foreach (var sibling in root.Children)
                if (!ReferenceEquals(sibling, mailbox) && !visible.Contains(sibling) &&
                    !sibling.Name.Equals("IPM_COMMON_VIEWS", StringComparison.OrdinalIgnoreCase) &&
                    !sibling.Name.Equals("Search Root", StringComparison.OrdinalIgnoreCase))
                    visible.Add(sibling);
            return visible;
        }
        return root.Children.Where(folder =>
            folder.ContentCount > 0 || folder.Children.Count > 0 ||
            (!folder.Name.Equals("IPM_COMMON_VIEWS", StringComparison.OrdinalIgnoreCase) &&
             !folder.Name.Equals("Search Root", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}
