using OpenOutlook.Desktop;
using PstCore;

namespace OpenOutlook.Tests;

public sealed class PstFolderPresentationTests
{
    [Fact]
    public void Mailbox_wrapper_is_hidden_but_its_mail_folders_remain_visible()
    {
        var root = Folder(1, "Root");
        var views = Folder(2, "IPM_COMMON_VIEWS");
        var search = Folder(3, "Search Root");
        var mailbox = Folder(4, "Top of Outlook data file");
        var inbox = Folder(5, "Inbox", 12);
        root.Children.AddRange([views, search, mailbox]);
        mailbox.Children.Add(inbox);

        Assert.Equal([inbox], PstFolderPresentation.VisibleRoots(root));
    }

    [Fact]
    public void Nonempty_wrapper_and_nonempty_system_folder_stay_reachable()
    {
        var root = Folder(1, "Root");
        var mailbox = Folder(2, "Top of Outlook data file", 1);
        root.Children.Add(mailbox);
        Assert.Equal([mailbox], PstFolderPresentation.VisibleRoots(root));

        var alternateRoot = Folder(3, "Root");
        var systemWithMail = Folder(4, "Search Root", 1);
        alternateRoot.Children.Add(systemWithMail);
        Assert.Equal([systemWithMail], PstFolderPresentation.VisibleRoots(alternateRoot));
    }

    private static MailFolder Folder(uint nid, string name, int count = 0) => new()
    {
        Nid = nid, ParentNid = 0, Name = name, ContentCount = count
    };
}
