using OpenOutlook.PstNative;
using PstCore;
using Xunit;

namespace OpenOutlook.Tests;

public sealed class PstCreateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oo-create-" + Guid.NewGuid().ToString("N"));
    public PstCreateTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static bool Native => OpenPst.NativeLibraryLoader.IsAvailable;

    [Fact]
    public void A_new_file_is_a_clean_empty_store_with_the_standard_folders()
    {
        if (!Native) return;
        var path = Path.Combine(_dir, "new.pst");
        using (var e = PstEngineFactory.Create(path, "me@example.org"))
        {
            Assert.True(e.CanWrite);
            Assert.Equal("me@example.org", e.DisplayName);
            Assert.Empty(e.Scan().Findings);
            var names = e.AllFolders().Select(f => f.Name).ToList();
            Assert.Contains("me@example.org", names);          // the top folder carries the store name
            Assert.Contains("Deleted Items", names);
            Assert.NotNull(e.DeletedItemsFolder());
            Assert.Equal(271360, new FileInfo(path).Length);   // one allocation section, like a blank file Outlook writes
        }
        using var again = PstEngineFactory.Open(path);
        Assert.Empty(again.Scan().Findings);
    }

    [Fact]
    public void A_created_data_file_shows_its_name_once_and_takes_new_folders()
    {
        if (!Native) return;
        var path = Path.Combine(_dir, "MyData.pst");
        using (var e = PstEngineFactory.CreateDataFile(path, "MyData"))
        {
            Assert.Equal("MyData", e.DisplayName);
            var wrapper = Assert.Single(e.Root.Children, c => c.Name == "Top of Outlook data file");
            // the folder list shows the file name once, with Deleted Items directly below it
            Assert.Equal(["Deleted Items"], OpenOutlook.Desktop.PstFolderPresentation.VisibleRoots(e.Root).Select(f => f.Name).ToArray());
            var made = e.CreateFolder(wrapper.Nid, "test");
            Assert.Contains(OpenOutlook.Desktop.PstFolderPresentation.VisibleRoots(e.Root), f => f.Name == "test");
            Assert.Equal(made.Nid, e.FindFolder(made.Nid)!.Nid);
            Assert.Empty(e.Scan().Findings);
        }
        using var again = PstEngineFactory.Open(path);
        Assert.Empty(again.Scan().Findings);
        Assert.Contains(OpenOutlook.Desktop.PstFolderPresentation.VisibleRoots(again.Root), f => f.Name == "test");
    }

    [Fact]
    public void A_new_file_takes_folders_and_messages_and_stays_clean()
    {
        if (!Native) return;
        var path = Path.Combine(_dir, "mail.pst");
        uint inbox, archive;
        uint messageNid;
        using (var e = PstEngineFactory.Create(path, "me@example.org"))
        {
            var top = e.AllFolders().Single(f => f.Name == "me@example.org");
            var a = e.CreateFolder(top.Nid, "Inbox");
            var b = e.CreateFolder(top.Nid, "Archive");
            (inbox, archive) = (a.Nid, b.Nid);
            var mails = Enumerable.Range(0, 5).Select(i => new MailImport
            {
                Subject = "Message " + i, SenderName = "Ann", SenderEmail = "ann@example.org", BodyText = "Hello " + i,
                Received = new DateTime(2026, 10, 1, 12, i, 0, DateTimeKind.Utc), Sent = new DateTime(2026, 10, 1, 12, i, 0, DateTimeKind.Utc)
            }).ToList();
            var imported = e.ImportMessages(a, mails);
            Assert.Equal(5, imported.Count);
            messageNid = imported[0].Nid;
            e.SetReadState(imported[1], true);
            e.MoveMessage(imported[2], b);
            e.CopyMessage(imported[3], b);
            e.DeleteMessage(imported[4]);
            Assert.Empty(e.Scan().Findings);
        }
        using var r = PstEngineFactory.Open(path);
        Assert.Empty(r.Scan().Findings);
        Assert.Equal(3, r.GetMessages(r.FindFolder(inbox)!).Count);
        Assert.Equal(2, r.GetMessages(r.FindFolder(archive)!).Count);
        Assert.Equal("Hello 0", r.OpenMessage(r.GetMessages(r.FindFolder(inbox)!).Single(m => m.Nid == messageNid)).BodyText.Trim());
    }

    [Fact]
    public void An_existing_file_is_never_overwritten()
    {
        if (!Native) return;
        var path = Path.Combine(_dir, "keep.pst");
        File.WriteAllText(path, "not a pst");
        Assert.Throws<PstCore.PstException>(() => PstEngineFactory.Create(path, "x"));
        Assert.Equal("not a pst", File.ReadAllText(path));
    }

    [Fact]
    public void Bad_arguments_leave_nothing_behind()
    {
        if (!Native) return;
        var path = Path.Combine(_dir, "bad.pst");
        Assert.Throws<PstCore.PstException>(() => PstEngineFactory.Create(path, ""));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".journal"));
    }
}
