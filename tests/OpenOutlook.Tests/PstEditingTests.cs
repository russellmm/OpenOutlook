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

    // ---- Phase B: real message deletion (Row-ID BTH unlink, no allocator) ----

    private static List<(uint Nid, string Subject)> Snapshot(PstStore store, uint folderNid)
    {
        var folder = store.AllFolders().First(f => f.Nid == folderNid);
        return store.GetMessages(folder).Select(m => (m.Nid, m.Subject)).ToList();
    }

    private static MailSummary GetByNid(PstStore store, uint folderNid, uint messageNid)
    {
        var folder = store.AllFolders().First(f => f.Nid == folderNid);
        return store.GetMessages(folder).First(m => m.Nid == messageNid);
    }

    [Fact]
    public void DeleteRemovesTheMessageAndKeepsEveryOtherRowIntact()
    {
        var src = FixturePath();
        if (src is null) return;
        var tmp = CopyToTemp(src);
        try
        {
            uint folderNid;
            List<(uint Nid, string Subject)> before;
            using (var session = PstEditSession.Begin(tmp))
            {
                var folder = session.Store.AllFolders()
                    .First(f => session.Store.GetMessages(f).Count >= 5);
                folderNid = folder.Nid;
                before = Snapshot(session.Store, folderNid);
                var target = GetByNid(session.Store, folderNid, before[0].Nid);
                session.Store.DeleteMessage(target);
                // Live store must reflect the delete without a reload.
                Assert.DoesNotContain(before[0].Nid, Snapshot(session.Store, folderNid).Select(m => m.Nid));
                session.Commit();
            }

            using (var reopened = PstStore.Open(tmp, writable: false))
            {
                var after = Snapshot(reopened, folderNid);
                Assert.Equal(before.Skip(1).ToList(), after); // exact order and subjects survive
                Assert.Empty(reopened.VerifyIntegrity());
            }
        }
            finally { Cleanup(tmp, tmp + ".bak"); }
    }

    [Fact]
    public void SequentialDeletesStayConsistentAcrossReopen()
    {
        var src = FixturePath();
        if (src is null) return;
        var tmp = CopyToTemp(src);
        try
        {
            uint folderNid;
            List<(uint Nid, string Subject)> before;
            using (var session = PstEditSession.Begin(tmp))
            {
                var folder = session.Store.AllFolders()
                    .First(f => session.Store.GetMessages(f).Count >= 6);
                folderNid = folder.Nid;
                before = Snapshot(session.Store, folderNid);
                for (var i = 0; i < 4; i++)
                {
                    var current = Snapshot(session.Store, folderNid);
                    session.Store.DeleteMessage(GetByNid(session.Store, folderNid, current[0].Nid));
                }
                session.Commit();
            }

            using (var reopened = PstStore.Open(tmp, writable: false))
            {
                var after = Snapshot(reopened, folderNid);
                Assert.Equal(before.Count - 4, after.Count);
                Assert.Equal(before.Skip(4).ToList(), after); // survivors untouched by the shifting
                Assert.Empty(reopened.VerifyIntegrity());
            }
        }
            finally { Cleanup(tmp, tmp + ".bak"); }
    }

    [Fact]
    public void UncommittedDeleteRestoresTheOriginalBytes()
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
                session.Store.DeleteMessage(message); // real write...
            } // ...no Commit -> Dispose restores every byte
            Assert.Equal(HashAll(tmp), HashAll(src));
        }
            finally { Cleanup(tmp, tmp + ".bak"); }
    }

    [Fact]
    public void DeleteUpdatesFolderCountsAcrossReopen()
    {
        var src = FixturePath();
        if (src is null) return;
        var tmp = CopyToTemp(src);
        try
        {
            uint folderNid;
            int contentBefore, unreadBefore;
            bool wasUnread;
            using (var session = PstEditSession.Begin(tmp))
            {
                var folder = session.Store.AllFolders()
                    .First(f => session.Store.GetMessages(f).Count >= 2);
                folderNid = folder.Nid;
                contentBefore = folder.ContentCount;
                unreadBefore = folder.UnreadCount;
                var message = session.Store.GetMessages(folder).First();
                wasUnread = !message.IsRead;
                session.Store.DeleteMessage(message);
                session.Commit();
            }
            using (var reopened = PstStore.Open(tmp, writable: false))
            {
                var folder = reopened.AllFolders().First(f => f.Nid == folderNid);
                if (contentBefore >= 0)
                    Assert.Equal(Math.Max(0, contentBefore - 1), folder.ContentCount);
                Assert.Equal(Math.Max(0, unreadBefore - (wasUnread ? 1 : 0)), folder.UnreadCount);
            }
        }
            finally { Cleanup(tmp, tmp + ".bak"); }
    }

    // ---- Phase C: real move between folders (re-link semantics) ----

    private static MailFolder Folder(PstStore store, string name) =>
        store.AllFolders().FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"fixture has no {name} folder");

    /// <summary>Any real mail folder holding at least one message, for move tests that must run
    /// against every owner archive (folder names differ between them).</summary>
    private static MailFolder SourceFolder(PstStore store)
    {
        foreach (var f in store.AllFolders())
        {
            if (f.Name.Equals("Deleted Items", StringComparison.OrdinalIgnoreCase)) continue;
            try { if (store.GetMessages(f).Count > 0) return f; }
            catch (PstException) { }
        }
        throw new InvalidOperationException("fixture has no folder containing messages");
    }

    [Fact]
    public void MoveMessageToDeletedItemsPersistsAcrossReopen()
    {
        var src = FixturePath();
        if (src is null) return;
        var tmp = CopyToTemp(src);
        try
        {
            uint movedNid; string movedSubject, movedFrom; DateTime movedReceived; int srcCountBefore, dstCountBefore;
            using (var session = PstEditSession.Begin(tmp))
            {
                var source = SourceFolder(session.Store);
                var dest = Folder(session.Store, "Deleted Items");
                var message = session.Store.GetMessages(source).First();
                movedNid = message.Nid; movedSubject = message.Subject; movedFrom = message.From; movedReceived = message.Received;
                srcCountBefore = session.Store.GetMessages(source).Count;
                dstCountBefore = session.Store.GetMessages(dest).Count;
                session.Store.MoveMessage(message, dest);
                // live store reflects the move without reload:
                Assert.DoesNotContain(session.Store.GetMessages(source), m => m.Nid == movedNid);
                Assert.Contains(session.Store.GetMessages(dest), m => m.Nid == movedNid);
                session.Commit(); // full VerifyIntegrity runs inside
            }
            using (var reopened = PstStore.Open(tmp, writable: false))
            {
                var source = SourceFolder(reopened);
                var dest = Folder(reopened, "Deleted Items");
                Assert.Empty(reopened.VerifyIntegrity());
                var srcAfter = reopened.GetMessages(source);
                var dstAfter = reopened.GetMessages(dest);
                Assert.Equal(srcCountBefore - 1, srcAfter.Count);
                Assert.Equal(dstCountBefore + 1, dstAfter.Count);
                Assert.DoesNotContain(srcAfter, m => m.Nid == movedNid);
                var landed = Assert.Single(dstAfter, m => m.Nid == movedNid);
                Assert.Equal(movedSubject, landed.Subject);
                Assert.Equal(movedFrom, landed.From);
                Assert.Equal(movedReceived, landed.Received);
            }
        }
        finally { Cleanup(tmp, tmp + ".bak"); }
    }

    [Fact]
    public void UncommittedMoveRestoresOriginalBytes()
    {
        var src = FixturePath();
        if (src is null) return;
        var tmp = CopyToTemp(src);
        try
        {
            var before = HashAll(tmp);
            using (var session = PstEditSession.Begin(tmp))
            {
                var source = SourceFolder(session.Store);
                var dest = Folder(session.Store, "Deleted Items");
                var message = session.Store.GetMessages(source).First();
                session.Store.MoveMessage(message, dest);
                // dispose without commit:
            }
            Assert.Equal(before, HashAll(tmp));
        }
        finally { Cleanup(tmp, tmp + ".bak"); }
    }

    [Fact]
    public void SequentialMovesAndBackRoundTripStayConsistent()
    {
        var src = FixturePath();
        if (src is null) return;
        var tmp = CopyToTemp(src);
        try
        {
            uint first, second;
            using (var session = PstEditSession.Begin(tmp))
            {
                var treasury = SourceFolder(session.Store);
                var trash = Folder(session.Store, "Deleted Items");
                var msgs = session.Store.GetMessages(treasury);
                first = msgs[0].Nid; second = msgs[1].Nid;
                session.Store.MoveMessage(msgs[0], trash);
                session.Store.MoveMessage(session.Store.GetMessages(treasury).First(m => m.Nid == second), trash);
                // round trip: bring the first one back
                var inTrash = session.Store.GetMessages(trash).First(m => m.Nid == first);
                session.Store.MoveMessage(inTrash, treasury);
                session.Commit();
            }
            using (var reopened = PstStore.Open(tmp, writable: false))
            {
                Assert.Empty(reopened.VerifyIntegrity());
                var treasury = SourceFolder(reopened);
                var trash = Folder(reopened, "Deleted Items");
                var t = reopened.GetMessages(treasury);
                var d = reopened.GetMessages(trash);
                Assert.Contains(t, m => m.Nid == first);
                Assert.DoesNotContain(d, m => m.Nid == first);
                Assert.Contains(d, m => m.Nid == second);
                Assert.DoesNotContain(t, m => m.Nid == second);
            }
        }
        finally { Cleanup(tmp, tmp + ".bak"); }
    }

    [Fact]
    public void MovedMessageOpensAndKeepsBodyAfterCommit()
    {
        var src = FixturePath();
        if (src is null) return;
        var tmp = CopyToTemp(src);
        try
        {
            uint movedNid; string bodyBefore;
            using (var session = PstEditSession.Begin(tmp))
            {
                var source = SourceFolder(session.Store);
                var dest = Folder(session.Store, "Deleted Items");
                var message = session.Store.GetMessages(source).First();
                movedNid = message.Nid;
                bodyBefore = session.Store.OpenMessage(message).BodyText;
                session.Store.MoveMessage(message, dest);
                session.Commit();
            }
            using (var reopened = PstStore.Open(tmp, writable: false))
            {
                var dest = Folder(reopened, "Deleted Items");
                var landed = reopened.GetMessages(dest).First(m => m.Nid == movedNid);
                Assert.Equal(bodyBefore, reopened.OpenMessage(landed).BodyText); // the item itself never moved
                Assert.Empty(reopened.VerifyIntegrity());
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
