using OpenOutlook.PstNative;
using PstCore;
using Xunit;

namespace OpenOutlook.Tests;

public sealed class PstBigValueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oo-big-" + Guid.NewGuid().ToString("N"));
    public PstBigValueTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData("recipients")]
    [InlineData("subject")]
    [InlineData("headers")]
    [InlineData("r20")]
    [InlineData("r150")]
    [InlineData("r200short")]
    [InlineData("r300short")]
    [InlineData("r700short")]
    [InlineData("sender")]
    public void Very_long_values_are_stored_and_the_file_stays_clean(string which)
    {
        if (!OpenPst.NativeLibraryLoader.IsAvailable) return;
        using var e = PstEngineFactory.Create(Path.Combine(_dir, "big.pst"), "me@example.org");
        var top = e.AllFolders().Single(f => f.Name == "me@example.org");
        var inbox = e.CreateFolder(top.Nid, "Inbox");
        var m = new MailImport { Subject = "S", SenderName = "Ann", SenderEmail = "ann@example.org", BodyText = "x", Received = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) };
        switch (which)
        {
            case "recipients": for (var i = 0; i < 300; i++) m.Recipients.Add(new ImportRecipient("Person Number " + i, $"person{i}@example.org", RecipientKind.To)); break;
            case "r20": for (var i = 0; i < 20; i++) m.Recipients.Add(new ImportRecipient("P" + i, $"p{i}@e.org", RecipientKind.To)); break;
            case "r150": for (var i = 0; i < 150; i++) m.Recipients.Add(new ImportRecipient("P" + i, $"p{i}@e.org", RecipientKind.To)); break;
            case "r200short": for (var i = 0; i < 200; i++) m.Recipients.Add(new ImportRecipient("P", $"p{i}@e.org", RecipientKind.To)); break;
            case "r300short": for (var i = 0; i < 300; i++) m.Recipients.Add(new ImportRecipient("P", $"p{i}@e.org", RecipientKind.To)); break;
            case "r700short": for (var i = 0; i < 700; i++) m.Recipients.Add(new ImportRecipient("P", $"p{i}@e.org", RecipientKind.To)); break;
            case "subject": m.Subject = new string('s', 5000); break;
            case "headers": m.TransportHeaders = string.Join("\r\n", Enumerable.Range(0, 400).Select(i => "X-Header-" + i + ": value " + i)); break;
            case "sender": m.SenderName = new string('n', 4000); break;
        }
        var s = e.ImportMessages(inbox, [m]);
        Assert.Single(s);
        var findings = e.Scan().Findings;
        if (Environment.GetEnvironmentVariable("OO_KEEP") is { Length: > 0 } keep) { e.Dispose(); File.Copy(Path.Combine(_dir, "big.pst"), Path.Combine(keep, "big_" + which + ".pst"), true); return; }
        Assert.True(findings.Count == 0, string.Join(" | ", findings));
    }
}
