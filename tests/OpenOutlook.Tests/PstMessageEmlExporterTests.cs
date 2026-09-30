using System.Text;
using OpenOutlook.Desktop;
using PstCore;

namespace OpenOutlook.Tests;

public sealed class PstMessageEmlExporterTests
{
    [Fact]
    public async Task ExportsPlainTextWithSafeEncodedHeadersAndBase64Body()
    {
        using var directory = new TestDirectory();
        var path = directory.Output("sample.eml");
        await PstMessageEmlExporter.ExportAsync(Message("Héllo", "first\nsecond", "Alice <alice@example.org>",
            "Bob <bob@example.org>"), path);
        var eml = File.ReadAllText(path);
        Assert.Contains("Subject: =?utf-8?B?", eml);
        Assert.Contains("Content-Type: text/plain; charset=utf-8\r\n", eml);
        Assert.Contains("Content-Transfer-Encoding: base64\r\n", eml);
        Assert.Contains("From: =?utf-8?B?", eml);
        Assert.DoesNotContain("\nBcc:", eml);
        var encoded = eml.Split("\r\n\r\n", 2)[1].Trim();
        Assert.Equal("first\nsecond", Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path) & (UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
    }

    [Fact]
    public async Task EncodesLegacyUnquotedCommaInSenderDisplayName()
    {
        using var directory = new TestDirectory();
        var path = directory.Output("legacy.eml");
        await PstMessageEmlExporter.ExportAsync(Message(from: "Marrash, Russell <russell@example.org>"), path);
        var eml = File.ReadAllText(path);
        Assert.Contains("From: =?utf-8?B?", eml);
        Assert.Contains("<russell@example.org>", eml);
        Assert.DoesNotContain("From: Marrash, Russell", eml);
    }

    [Fact]
    public async Task UsesStructuredPstRecipientWhenDisplayToHasOnlyName()
    {
        using var directory = new TestDirectory();
        var path = directory.Output("recipient.eml");
        var message = new MailMessage
        {
            Summary = Summary(to: "Marrash, Russell"), BodyText = "body",
            Recipients = [new MailRecipient { Kind = RecipientKind.To, Name = "Marrash, Russell",
                Email = "russell@example.org" }]
        };
        await PstMessageEmlExporter.ExportAsync(message, path);
        var eml = File.ReadAllText(path);
        Assert.Contains("To: =?utf-8?B?", eml);
        Assert.Contains("<russell@example.org>", eml);
        Assert.DoesNotContain("To: Marrash, Russell", eml);
    }

    [Fact]
    public async Task RejectsHeaderInjectionAndLeavesNoFile()
    {
        using var directory = new TestDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => PstMessageEmlExporter.ExportAsync(
            Message("hello\r\nBcc: attacker@example.org"), directory.Output("bad.eml")));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task RejectsIncompleteOrUnsupportedAttachments()
    {
        using var directory = new TestDirectory();
        // Metadata-only attachment flag must not be silently ignored.
        var flagged = new MailMessage { Summary = new MailSummary { Nid = 1, FolderNid = 2,
            HasAttachment = true }, BodyText = "body" };
        await Assert.ThrowsAsync<NotSupportedException>(() => PstMessageEmlExporter.ExportAsync(flagged,
            directory.Output("flagged.eml")));
        var attached = new MailMessage { Summary = Summary(), BodyText = "body",
            Attachments = [new MailAttachment { Nid = 3, FileName = "embedded.msg", Method = 5 }] };
        await Assert.ThrowsAsync<NotSupportedException>(() => PstMessageEmlExporter.ExportAsync(attached,
            directory.Output("attached.eml")));
        var inline = new MailMessage { Summary = Summary(), BodyHtml = "<img src='cid:image1'>",
            Attachments = [new MailAttachment { Nid = 4, FileName = "image.png", Method = 1,
                ContentId = "image1", Size = 4 }] };
        await Assert.ThrowsAsync<NotSupportedException>(() => PstMessageEmlExporter.ExportAsync(inline,
            directory.Output("inline.eml"), _ => [1, 2, 3, 4]));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task ExportsHtmlOnlyAsHtmlMimePart()
    {
        using var directory = new TestDirectory();
        var path = directory.Output("html.eml");
        await PstMessageEmlExporter.ExportAsync(new MailMessage
        { Summary = Summary(), BodyHtml = "<p>Héllo</p>" }, path);
        var eml = File.ReadAllText(path);
        Assert.Contains("Content-Type: text/html; charset=utf-8\r\n", eml);
        Assert.Equal("<p>Héllo</p>", Encoding.UTF8.GetString(Convert.FromBase64String(
            eml.Split("\r\n\r\n", 2)[1].Trim())));
    }

    [Fact]
    public async Task ExportsAlternativeBodyAndFileAttachmentWithSafeMimeHeaders()
    {
        using var directory = new TestDirectory();
        var path = directory.Output("mixed.eml");
        var attachment = new MailAttachment { Nid = 3, FileName = "résumé; final.pdf",
            MimeTag = "application/pdf\r\nBcc: attacker@example.org", Method = 1, Size = 204 };
        var message = new MailMessage { Summary = Summary(), BodyText = "Plain body",
            BodyHtml = "<b>Rich body</b>", Attachments = [attachment] };
        await PstMessageEmlExporter.ExportAsync(message, path, _ => [1, 2, 3, 4]);
        var eml = File.ReadAllText(path);
        Assert.Contains("Content-Type: multipart/mixed; boundary=", eml);
        Assert.Contains("Content-Type: multipart/alternative; boundary=", eml);
        Assert.Contains("Content-Type: text/plain; charset=utf-8", eml);
        Assert.Contains("Content-Type: text/html; charset=utf-8", eml);
        Assert.Contains("Content-Type: application/octet-stream", eml);
        Assert.Contains("filename*0*=utf-8''", eml);
        Assert.DoesNotContain("Bcc: attacker", eml);
        Assert.Contains("AQIDBA==\r\n", eml);
        Assert.Equal(1, eml.Split("Content-Disposition: attachment", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task FailedAttachmentReadLeavesNoEmlOrTemporaryFile()
    {
        using var directory = new TestDirectory();
        var attachment = new MailAttachment { Nid = 3, FileName = "file.bin", Method = 1, Size = 10 };
        var message = new MailMessage { Summary = Summary(), BodyText = "body", Attachments = [attachment] };
        var failure = await Assert.ThrowsAsync<IOException>(() => PstMessageEmlExporter.ExportAsync(message,
            directory.Output("failed.eml"), _ => throw new Exception("secret attachment bytes")));
        Assert.DoesNotContain("secret", failure.ToString());
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task RejectsLargeBodiesBeforeCreatingOutput()
    {
        using var directory = new TestDirectory();
        var message = new MailMessage { Summary = Summary(),
            BodyText = new string('é', PstMessageEmlExporter.DefaultMaximumBodyBytes / 2 + 1) };
        await Assert.ThrowsAsync<InvalidDataException>(() => PstMessageEmlExporter.ExportAsync(message,
            directory.Output("huge.eml")));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task NeverOverwritesOrWritesOnCancellation()
    {
        using var directory = new TestDirectory();
        var path = directory.Output("existing.eml");
        await File.WriteAllTextAsync(path, "original");
        await Assert.ThrowsAsync<IOException>(() => PstMessageEmlExporter.ExportAsync(Message(), path));
        Assert.Equal("original", File.ReadAllText(path));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PstMessageEmlExporter.ExportAsync(
            Message(), directory.Output("cancelled.eml"), cancellation.Token));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData("bad.txt")]
    [InlineData("bad:name.eml")]
    public async Task RejectsUnsafeOutputNames(string filename)
    {
        using var directory = new TestDirectory();
        await Assert.ThrowsAsync<ArgumentException>(() => PstMessageEmlExporter.ExportAsync(
            Message(), directory.Output(filename)));
    }

    private static MailSummary Summary(string subject = "Hi", string from = "alice@example.org",
        string to = "bob@example.org") => new() { Nid = 1, FolderNid = 2, Subject = subject,
        From = from, To = to, Sent = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc) };

    private static MailMessage Message(string subject = "Hi", string body = "body",
        string from = "alice@example.org", string to = "bob@example.org") =>
        new() { Summary = Summary(subject, from, to), BodyText = body };

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "eml-export-test-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public string Output(string filename) => System.IO.Path.Combine(Path, filename);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
