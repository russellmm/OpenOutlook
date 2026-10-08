using OpenOutlook.Desktop;
using OpenOutlook.PstNative;
using PstCore;

namespace OpenOutlook.Tests;

/// <summary>The MIME transfer path against the real PST engine: what a message looks like after PST -> MIME -> PST, and that both files stay clean.</summary>
public sealed class PstTransferEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oo-transfer-" + Guid.NewGuid().ToString("N"));
    public PstTransferEndToEndTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static bool Native => OpenPst.NativeLibraryLoader.IsAvailable;

    private static MailImport Rich(int i, byte[] png, byte[] pdf)
    {
        var m = new MailImport
        {
            Subject = $"Rapport {i} – 🎉 café", SenderName = "Ann Example", SenderEmail = "ann@example.org",
            BodyText = "Plain text für alle", BodyHtml = "<html><body><p>Hello <b>world</b></p><img src=\"cid:logo@x\"></body></html>",
            Sent = new DateTime(2026, 10, 1, 12, i, 0, DateTimeKind.Utc), Received = new DateTime(2026, 10, 1, 12, i, 30, DateTimeKind.Utc),
            Importance = 2, Read = i % 2 == 0, MessageId = $"<id{i}@example.org>",
            TransportHeaders = $"Received: from mx.example.org\r\nMessage-ID: <id{i}@example.org>\r\nX-Spam-Score: 1\r\n"
        };
        m.Recipients.Add(new ImportRecipient("Bob", "bob@example.org", RecipientKind.To));
        m.Recipients.Add(new ImportRecipient("Cy", "cy@example.org", RecipientKind.Cc));
        m.Attachments.Add(new ImportAttachment("logo.png", "image/png", "logo@x", png));
        m.Attachments.Add(new ImportAttachment("résumé.pdf", "application/pdf", "", pdf));
        return m;
    }

    [Fact]
    public async Task A_move_through_mime_keeps_the_message_and_leaves_both_files_clean()
    {
        if (!Native) return;
        var png = Enumerable.Range(0, 4000).Select(i => (byte)(i * 11)).ToArray();
        var pdf = Enumerable.Range(0, 120_000).Select(i => (byte)(i * 5)).ToArray();
        using var source = PstEngineFactory.CreateDataFile(Path.Combine(_dir, "source.pst"), "Source");
        using var dest = PstEngineFactory.CreateDataFile(Path.Combine(_dir, "dest.pst"), "Dest");
        var sourceFolder = source.CreateFolder(source.Root.Children.Single(c => c.Name == "Top of Outlook data file").Nid, "Reports");
        var destFolder = dest.CreateFolder(dest.Root.Children.Single(c => c.Name == "Top of Outlook data file").Nid, "Filed");
        var summaries = source.ImportMessages(sourceFolder, Enumerable.Range(1, 45).Select(i => Rich(i, png, pdf)).ToList());
        Assert.Equal(45, summaries.Count);

        var byId = summaries.ToDictionary(s => s.Nid.ToString("x8"));
        var items = summaries.Select(s => new TransferItem(s.Nid.ToString("x8"), s.Subject, s.IsRead)).ToList();
        var result = await MailTransfer.RunAsync(new PstTransferSource(source, byId), items, new PstTransferSink(dest, destFolder), move: true);

        Assert.True(result.AllDone, string.Join("; ", result.Failures.Select(f => f.Error)));
        Assert.Equal((45, 45), (result.Transferred, result.Removed));
        Assert.Empty(source.GetMessages(source.FindFolder(sourceFolder.Nid)!));
        var arrived = dest.GetMessages(dest.FindFolder(destFolder.Nid)!);
        Assert.Equal(45, arrived.Count);
        Assert.Empty(source.Scan().Findings);
        Assert.Empty(dest.Scan().Findings);

        var one = arrived.Single(a => a.Subject == "Rapport 4 – 🎉 café");
        Assert.True(one.IsRead);                                  // read state travels
        Assert.Equal(2, one.Importance);                          // so does importance
        Assert.Equal(22, arrived.Count(a => a.IsRead));
        var opened = dest.OpenMessage(one);
        Assert.Contains("Hello", opened.BodyHtml);
        Assert.Contains("Plain text für alle", opened.BodyText);
        Assert.Equal(["bob@example.org", "cy@example.org"], opened.Recipients.Select(r => r.Email).ToArray());
        var logo = Assert.Single(opened.Attachments, a => a.ContentId.Trim('<', '>') == "logo@x");
        Assert.Equal(png, dest.ReadAttachmentData(one, logo));
        var pdfAtt = Assert.Single(opened.Attachments, a => a.FileName == "résumé.pdf");
        Assert.Equal(pdf, dest.ReadAttachmentData(one, pdfAtt));
        Assert.Contains("<id4@example.org>", opened.Headers);     // the original Message-ID is kept
        Assert.Equal(new DateTime(2026, 10, 1, 12, 4, 0), one.Sent.ToUniversalTime());
    }

    [Fact]
    public async Task A_copy_through_mime_leaves_the_source_untouched()
    {
        if (!Native) return;
        using var source = PstEngineFactory.CreateDataFile(Path.Combine(_dir, "s2.pst"), "S2");
        using var dest = PstEngineFactory.CreateDataFile(Path.Combine(_dir, "d2.pst"), "D2");
        var sf = source.CreateFolder(source.Root.Children.Single(c => c.Name == "Top of Outlook data file").Nid, "In");
        var df = dest.CreateFolder(dest.Root.Children.Single(c => c.Name == "Top of Outlook data file").Nid, "Out");
        var summaries = source.ImportMessages(sf, [Rich(1, [1, 2, 3], [4, 5, 6])]);
        var byId = summaries.ToDictionary(s => s.Nid.ToString("x8"));
        var result = await MailTransfer.RunAsync(new PstTransferSource(source, byId),
            summaries.Select(s => new TransferItem(s.Nid.ToString("x8"), s.Subject, s.IsRead)).ToList(), new PstTransferSink(dest, df), move: false);
        Assert.Equal((1, 0), (result.Transferred, result.Removed));
        Assert.Single(source.GetMessages(source.FindFolder(sf.Nid)!));
        Assert.Single(dest.GetMessages(dest.FindFolder(df.Nid)!));
        Assert.Empty(source.Scan().Findings);
        Assert.Empty(dest.Scan().Findings);
    }

    [Fact]
    public async Task A_message_the_builder_cannot_represent_stays_in_the_source_and_the_rest_still_move()
    {
        if (!Native) return;
        using var source = PstEngineFactory.CreateDataFile(Path.Combine(_dir, "s3.pst"), "S3");
        using var dest = PstEngineFactory.CreateDataFile(Path.Combine(_dir, "d3.pst"), "D3");
        var sf = source.CreateFolder(source.Root.Children.Single(c => c.Name == "Top of Outlook data file").Nid, "In");
        var df = dest.CreateFolder(dest.Root.Children.Single(c => c.Name == "Top of Outlook data file").Nid, "Out");
        var summaries = source.ImportMessages(sf, [Rich(1, [1], [2]), Rich(2, [3], [4])]);
        // a source whose reading fails for the first message only (an unreadable attachment, for instance)
        var inner = new PstTransferSource(source, summaries.ToDictionary(s => s.Nid.ToString("x8")));
        var flaky = new FlakySource(inner, summaries[0].Nid.ToString("x8"));
        var result = await MailTransfer.RunAsync(flaky, summaries.Select(s => new TransferItem(s.Nid.ToString("x8"), s.Subject, s.IsRead)).ToList(), new PstTransferSink(dest, df), move: true);
        Assert.Equal((1, 1), (result.Transferred, result.Removed));
        Assert.Single(result.Failures);
        var left = Assert.Single(source.GetMessages(source.FindFolder(sf.Nid)!));
        Assert.Equal(summaries[0].Nid, left.Nid);                 // the one that failed is still there
        Assert.Single(dest.GetMessages(dest.FindFolder(df.Nid)!));
        Assert.Empty(source.Scan().Findings);
        Assert.Empty(dest.Scan().Findings);
    }

    private sealed class FlakySource(ITransferSource inner, string failId) : ITransferSource
    {
        public Task<byte[]> ReadMimeAsync(TransferItem item, CancellationToken ct) =>
            item.Id == failId ? throw new IOException("attachment unreadable") : inner.ReadMimeAsync(item, ct);
        public Task RemoveAsync(IReadOnlyList<TransferItem> items, CancellationToken ct) => inner.RemoveAsync(items, ct);
    }
}
