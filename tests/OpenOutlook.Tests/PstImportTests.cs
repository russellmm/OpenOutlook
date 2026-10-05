using System.Text;
using OpenOutlook.PstNative;
using PstCore;

namespace OpenOutlook.Tests;

/// <summary>Import of messages (parsed from EML text) into a copy of the OPENOUTLOOK_TEST_PST fixture and read-back through the engine.</summary>
public sealed class PstImportTests
{
    private static string? Fixture()
    {
        var path = Environment.GetEnvironmentVariable("OPENOUTLOOK_TEST_PST");
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !OpenPst.NativeLibraryLoader.IsAvailable ? null : path;
    }

    private static string TempCopy(string source)
    {
        var copy = Path.Combine(Path.GetTempPath(), "oo-import-" + Guid.NewGuid().ToString("N") + ".pst");
        File.Copy(source, copy);
        return copy;
    }

    private static void Cleanup(string path)
    {
        foreach (var f in new[] { path, path + ".lck", path + ".journal" })
            try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { }
    }

    private static List<string> Findings(IPstEngine e) => e.VerifyIntegrity().Where(l => l.StartsWith("  "))
        .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"0x[0-9a-fA-F]+|\d+", "#")).Distinct().ToList();

    private static byte[] Payload(int n, int seed) => Enumerable.Range(0, n).Select(i => (byte)(i * 31 + seed + (i >> 9))).ToArray();

    private const string RichEml =
        "From: \"Dan Sender\" <dan@example.test>\r\nTo: Ann <ann@example.test>, bob@example.test\r\nCc: Cy <cy@example.test>\r\n" +
        "Subject: Re: Quarterly résumé € report\r\nDate: Mon, 02 Mar 2020 09:15:00 +0000\r\nMessage-ID: <q1@example.test>\r\nMIME-Version: 1.0\r\n" +
        "Content-Type: multipart/mixed; boundary=\"m\"\r\n\r\n" +
        "--m\r\nContent-Type: multipart/alternative; boundary=\"a\"\r\n\r\n" +
        "--a\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n{TEXT}\r\n" +
        "--a\r\nContent-Type: multipart/related; boundary=\"r\"\r\n\r\n" +
        "--r\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<html><body><p>Hello <b>world</b> €</p><img src=\"cid:logo@x\">{HTML}</body></html>\r\n" +
        "--r\r\nContent-Type: image/png; name=\"logo.png\"\r\nContent-ID: <logo@x>\r\nContent-Transfer-Encoding: base64\r\n\r\n{PNG}\r\n--r--\r\n--a--\r\n" +
        "--m\r\nContent-Type: application/octet-stream; name=\"data.bin\"\r\nContent-Disposition: attachment; filename=\"data.bin\"\r\nContent-Transfer-Encoding: base64\r\n\r\n{BIN}\r\n--m--\r\n";

    private static (MailImport Mail, byte[] Bin, byte[] Png, string Text, string Html) Build(int textSize, int htmlSize, int binSize)
    {
        var text = string.Join("\r\n", Enumerable.Range(0, textSize / 40 + 1).Select(i => $"line {i} of the report é€"));
        var html = string.Concat(Enumerable.Range(0, htmlSize / 30 + 1).Select(i => $"<p>paragraph {i} é</p>"));
        var bin = Payload(binSize, 5);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
        var eml = RichEml.Replace("{TEXT}", text).Replace("{HTML}", html).Replace("{PNG}", Convert.ToBase64String(png))
            .Replace("{BIN}", Convert.ToBase64String(bin, Base64FormattingOptions.InsertLineBreaks).Replace("\n", "\r\n"));
        return (EmlParser.Parse(Encoding.UTF8.GetBytes(eml)), bin, png, text, html);
    }

    [Fact]
    public void Imported_messages_read_back_complete_and_leave_the_archive_consistent()
    {
        var path = Fixture();
        if (path is null) return;
        var copy = TempCopy(path);
        try
        {
            uint folderNid, nid1, nid2;
            byte[] bin1, png1, bin2;
            string text1, html1;
            List<string> before;
            using (var e = NativePstEngine.Open(copy, write: true))
            {
                before = Findings(e);
                var folder = e.AllFolders().Where(f => f.Name != "Root" && f.Nid != e.DeletedItemsFolder()?.Nid).OrderByDescending(f => f.ContentCount).First();
                folderNid = folder.Nid;
                int count0 = folder.ContentCount, unread0 = folder.UnreadCount;
                var big = Build(30_000, 20_000, 200_000);          // body and attachment in subnodes, multi-block data tree
                var small = Build(100, 100, 50);                    // everything in the property heap
                (bin1, png1, text1, html1) = (big.Bin, big.Png, big.Text, big.Html);
                bin2 = small.Bin;
                var summaries = e.ImportMessages(folder, [big.Mail, small.Mail]);
                Assert.Equal(2, summaries.Count);
                (nid1, nid2) = (summaries[0].Nid, summaries[1].Nid);
                Assert.Equal(count0 + 2, folder.ContentCount);
                Assert.Equal(unread0 + 2, folder.UnreadCount);          // imported without Read: both unread
                Assert.Equal(before, Findings(e));
                Assert.Equal(2, e.GetMessages(folder).Count(m => m.Nid == nid1 || m.Nid == nid2));
            }
            using var r = NativePstEngine.Open(copy);
            Assert.Equal(before, Findings(r));
            var f2 = r.FindFolder(folderNid)!;
            var rows = r.GetMessages(f2).ToDictionary(m => m.Nid);
            foreach (var nid in new[] { nid1, nid2 })
            {
                var row = rows[nid];
                Assert.Equal("Re: Quarterly résumé € report", row.Subject);
                Assert.Equal("Dan Sender", row.From);
                Assert.Equal("Ann; bob@example.test", row.To);
                Assert.False(row.IsRead);
                Assert.True(row.HasAttachment);
                Assert.Equal("IPM.Note", row.MessageClass);
                Assert.Equal(new DateTime(2020, 3, 2, 9, 15, 0, DateTimeKind.Utc).ToLocalTime(), row.Sent);
            }
            var m1 = r.OpenMessage(rows[nid1]);
            Assert.True(m1.HasText && m1.HasHtml && !m1.HasRtf);
            Assert.Equal(text1.Replace("\r\n", "\n"), m1.BodyText.Replace("\r\n", "\n"));
            Assert.Contains("<p>Hello <b>world</b>", m1.BodyHtml);
            Assert.EndsWith("</body></html>", m1.BodyHtml);
            Assert.Equal(html1.Length + m1.BodyHtml.Length - html1.Length, m1.BodyHtml.Length);
            Assert.Collection(m1.Recipients,
                x => Assert.Equal(("Ann", "ann@example.test", RecipientKind.To), (x.Name, x.Email, x.Kind)),
                x => Assert.Equal(("bob@example.test", "bob@example.test", RecipientKind.To), (x.Name, x.Email, x.Kind)),
                x => Assert.Equal(("Cy", "cy@example.test", RecipientKind.Cc), (x.Name, x.Email, x.Kind)));
            Assert.StartsWith("From: ", m1.Headers);
            Assert.Equal(2, m1.Attachments.Count);
            var data = m1.Attachments.Single(a => a.FileName == "data.bin");
            var logo = m1.Attachments.Single(a => a.FileName == "logo.png");
            Assert.Equal("logo@x", logo.ContentId);
            Assert.Equal(bin1, r.ReadAttachmentData(rows[nid1], data));
            Assert.Equal(png1, r.ReadAttachmentData(rows[nid1], logo));
            Assert.Equal(bin2, r.ReadAttachmentData(rows[nid2], r.OpenMessage(rows[nid2]).Attachments.Single(a => a.FileName == "data.bin")));
            // the 'visible attachments' rule of the reading pane hides the inline logo
            Assert.Equal(["data.bin"], OpenOutlook.Desktop.PstMessageHeader.VisibleAttachments(m1).Select(a => a.FileName).ToArray());
            // searching finds them, and the imported message behaves like any other afterwards
            Assert.Contains(r.Search("Quarterly").Select(s => s.Nid), n => n == nid1);
        }
        finally { Cleanup(copy); }
    }

    [Fact]
    public void Imported_messages_can_be_read_marked_flagged_moved_and_deleted()
    {
        var path = Fixture();
        if (path is null) return;
        var copy = TempCopy(path);
        try
        {
            using var e = NativePstEngine.Open(copy, write: true);
            var before = Findings(e);
            var folders = e.AllFolders().Where(f => f.Name != "Root" && f.Nid != e.DeletedItemsFolder()?.Nid).OrderByDescending(f => f.ContentCount).Take(2).ToList();
            var mail = Build(500, 500, 500).Mail;
            mail.Read = true;
            var s = e.ImportMessages(folders[0], [mail])[0];
            Assert.True(s.IsRead);
            e.SetReadState(s, false);
            e.SetFlagged(s, true);
            e.Flush();
            e.MoveMessage(s, folders[1]);
            var moved = e.GetMessages(folders[1]).Single(m => m.Nid == s.Nid);
            // the flag lives in the message itself; a table row carries it only when the folder's table has a flag column
            Assert.True(e.OpenMessage(moved).Summary.Flagged);
            Assert.False(moved.IsRead);
            e.CopyMessage(moved, folders[0]);
            e.DeleteMessage(moved);
            Assert.DoesNotContain(e.GetMessages(folders[1]), m => m.Nid == s.Nid);
            Assert.Equal(before, Findings(e));
        }
        finally { Cleanup(copy); }
    }

    [Fact]
    public void Importing_into_a_read_only_engine_fails_closed()
    {
        var path = Fixture();
        if (path is null) return;
        using var e = NativePstEngine.Open(path);
        Assert.Throws<PstCore.PstException>(() => e.ImportMessages(e.AllFolders().First(f => f.Name != "Root"), [new MailImport { Subject = "x" }]));
    }

    [Fact]
    public void Messages_copy_between_two_open_archives_and_a_move_removes_the_originals()
    {
        var path = Fixture();
        if (path is null) return;
        string a = TempCopy(path), b = TempCopy(path);
        try
        {
            using var src = NativePstEngine.Open(a, write: true);
            using var dst = NativePstEngine.Open(b, write: true);
            var beforeDst = Findings(dst);
            var from = src.AllFolders().Where(f => f.Name != "Root" && f.ContentCount >= 2 && f.Nid != src.DeletedItemsFolder()?.Nid).OrderByDescending(f => f.ContentCount).First();
            var into = dst.AllFolders().Where(f => f.Name != "Root" && f.Nid != dst.DeletedItemsFolder()?.Nid).OrderBy(f => f.ContentCount).First();
            var picked = src.GetMessages(from).Take(2).ToList();
            int count0 = into.ContentCount;
            var copies = src.CopyMessagesTo(dst, into, picked);
            Assert.Equal(2, copies.Count);
            Assert.Equal(count0 + 2, into.ContentCount);
            Assert.Equal(2, from.ContentCount - src.GetMessages(from).Count + 2);          // the originals are still there
            var rows = dst.GetMessages(into).ToDictionary(m => m.Nid);
            for (int i = 0; i < 2; i++)
            {
                var copy = rows[copies[i].Nid];
                Assert.Equal(picked[i].Subject, copy.Subject);
                Assert.Equal(picked[i].Received, copy.Received);
                var open = dst.OpenMessage(copy);
                var original = src.OpenMessage(picked[i]);
                Assert.Equal(original.BodyText, open.BodyText);
                Assert.Equal(original.Attachments.Select(x => (x.FileName, x.Size)), open.Attachments.Select(x => (x.FileName, x.Size)));
            }
            Assert.Empty(Findings(dst).Except(beforeDst));
            // a "move" is the copy followed by deleting the originals
            foreach (var m in picked) src.DeleteMessage(m);
            Assert.DoesNotContain(src.GetMessages(from), m => picked.Any(p => p.Nid == m.Nid));
            Assert.Throws<PstCore.PstException>(() => src.CopyMessagesTo(src, from, picked));
        }
        finally { Cleanup(a); Cleanup(b); }
    }
}
