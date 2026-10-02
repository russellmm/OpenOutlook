using PstCore;

namespace OpenOutlook.Tests;

/// <summary>
/// Write-path tests for PST editing. Opt-in like the read-only suite (OPENOUTLOOK_TEST_PST) so no
/// private fixture is ever required or packaged - and unlike the read tests, these NEVER touch the
/// fixture itself: every test copies it to a temp file first and only writes there.
/// </summary>
public sealed class PstEditingTests
{
    private static string? FixturePath()
    {
        var path = Environment.GetEnvironmentVariable("OPENOUTLOOK_TEST_PST");
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : Path.GetFullPath(path);
    }

    private static string CopyToTemp(string source)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "oo-edit-" + Guid.NewGuid().ToString("N") + ".pst");
        File.Copy(source, tmp);
        return tmp;
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    [Fact]
    public void BeginBacksUpAndFreshArchivePassesIntegrity()
    {
        var src = FixturePath();
        if (src is null) return;
        var tmp = CopyToTemp(src);
        try
        {
            using var session = PstEditSession.Begin(tmp);
            Assert.True(File.Exists(session.BackupPath));
            Assert.Equal(new FileInfo(src).Length, new FileInfo(session.BackupPath).Length);
            Assert.True(session.Store.CanWrite);
            Assert.Empty(session.Store.VerifyIntegrity());
        }
        finally { Cleanup(tmp, tmp + ".bak"); }
    }

    [Fact]
    public void UncommittedSessionRestoresTheOriginalBytes()
    {
        var src = FixturePath();
        if (src is null) return;
        var tmp = CopyToTemp(src);
        try
        {
            using (var session = PstEditSession.Begin(tmp))
            {
                var folder = session.Store.AllFolders().First(f => session.Store.GetMessages(f).Any());
                var message = session.Store.GetMessages(folder).First();
                session.Store.SetReadState(message, !message.IsRead); // real write...
            } // ...no Commit -> Dispose must roll back
            Assert.Equal(HashAll(tmp), HashAll(src));
        }
        finally { Cleanup(tmp, tmp + ".bak"); }
    }

    [Fact]
    public void ReadFlagPersistsAcrossReopenAndMovesTheUnreadBadge()
    {
        var src = FixturePath();
        if (src is null) return;
        var tmp = CopyToTemp(src);
        try
        {
            uint folderNid, messageNid;
            bool target;
            int unreadBefore;
            using (var session = PstEditSession.Begin(tmp))
            {
                var folder = session.Store.AllFolders().First(f => session.Store.GetMessages(f).Any());
                // Prefer flipping READ -> UNREAD so the badge must go up even in an all-read archive.
                var message = session.Store.GetMessages(folder).FirstOrDefault(m => m.IsRead)
                              ?? session.Store.GetMessages(folder).First();
                target = !message.IsRead;
                folderNid = folder.Nid;
                messageNid = message.Nid;
                unreadBefore = folder.UnreadCount;
                session.Store.SetReadState(message, target);
                session.Commit();
            }

            using (var reopened = PstStore.Open(tmp, writable: false))
            {
                var folder = reopened.AllFolders().First(f => f.Nid == folderNid);
                var message = reopened.GetMessages(folder).First(m => m.Nid == messageNid);
                Assert.Equal(target, message.IsRead);
                var expectedUnread = Math.Max(0, unreadBefore + (target ? -1 : +1));
                Assert.Equal(expectedUnread, folder.UnreadCount);
            }

            // Flipping back restores the original flag and badge.
            using (var session = PstEditSession.Begin(tmp))
            {
                var folder = session.Store.AllFolders().First(f => f.Nid == folderNid);
                var message = session.Store.GetMessages(folder).First(m => m.Nid == messageNid);
                session.Store.SetReadState(message, !target);
                session.Commit();
            }
            using (var reopened = PstStore.Open(tmp, writable: false))
            {
                var folder = reopened.AllFolders().First(f => f.Nid == folderNid);
                Assert.Equal(unreadBefore, folder.UnreadCount);
            }
        }
        finally { Cleanup(tmp, tmp + ".bak"); }
    }

    [Fact]
    public void FlaggedStatePersistsAcrossReopen()
    {
        var src = FixturePath();
        if (src is null) return;
        var tmp = CopyToTemp(src);
        try
        {
            uint messageNid;
            bool target;
            using (var session = PstEditSession.Begin(tmp))
            {
                // Some items carry no PR_FLAG_STATUS at all (adding a property is later-phase work),
                // so find one whose flag can actually be toggled in this fixture.
                var candidates = session.Store.AllFolders().SelectMany(session.Store.GetMessages).Take(25).ToList();
                MailSummary? picked = null;
                foreach (var m in candidates)
                {
                    try { session.Store.SetFlagged(m, !m.Flagged); picked = m; break; }
                    catch (PstException) { /* property absent on this item */ }
                }
                if (picked is null) return; // fixture has no flaggable item; nothing to assert
                target = !candidates.First(m => m.Nid == picked.Nid).Flagged || !picked.Flagged;
                target = picked.Flagged; // SetFlagged already applied the flipped value and updated the model
                messageNid = picked.Nid;
                session.Commit();
            }
            using (var reopened = PstStore.Open(tmp, writable: false))
            {
                var message = reopened.AllFolders()
                    .SelectMany(reopened.GetMessages)
                    .First(m => m.Nid == messageNid);
                Assert.Equal(target, message.Flagged);
            }
        }
        finally { Cleanup(tmp, tmp + ".bak"); }
    }

    private static string HashAll(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}
