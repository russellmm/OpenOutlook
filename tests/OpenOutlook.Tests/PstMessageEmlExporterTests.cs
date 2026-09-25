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
    public async Task RejectsHeaderInjectionAndLeavesNoFile()
    {
        using var directory = new TestDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => PstMessageEmlExporter.ExportAsync(
            Message("hello\r\nBcc: attacker@example.org"), directory.Output("bad.eml")));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task RejectsAttachmentsIncludingUnverifiedMetadataAndHtmlOnly()
    {
        using var directory = new TestDirectory();
        // Metadata-only attachment flag must not be silently ignored.
        var flagged = new MailMessage { Summary = new MailSummary { Nid = 1, FolderNid = 2,
            HasAttachment = true }, BodyText = "body" };
        await Assert.ThrowsAsync<NotSupportedException>(() => PstMessageEmlExporter.ExportAsync(flagged,
            directory.Output("flagged.eml")));
        var attached = new MailMessage { Summary = Summary(), BodyText = "body",
            Attachments = [new MailAttachment { Nid = 3 }] };
        await Assert.ThrowsAsync<NotSupportedException>(() => PstMessageEmlExporter.ExportAsync(attached,
            directory.Output("attached.eml")));
        var htmlOnly = new MailMessage { Summary = Summary(), BodyHtml = "<b>only</b>" };
        await Assert.ThrowsAsync<NotSupportedException>(() => PstMessageEmlExporter.ExportAsync(htmlOnly,
            directory.Output("html.eml")));
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
