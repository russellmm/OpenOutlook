using OpenOutlook.Desktop;
using PstCore;

namespace OpenOutlook.Tests;

public sealed class PstMessageHeaderTests
{
    private static MailMessage Message(string html, params MailAttachment[] attachments) => new()
    {
        Summary = new MailSummary { Nid = 1, FolderNid = 2, To = "Fallback Person" },
        BodyHtml = html,
        Recipients =
        [
            new MailRecipient { Name = "Ann", Email = "ann@example.test", Kind = RecipientKind.To },
            new MailRecipient { Name = "", Email = "bob@example.test", Kind = RecipientKind.To },
            new MailRecipient { Name = "Ann", Email = "ann@example.test", Kind = RecipientKind.To },
            new MailRecipient { Name = "Cy", Email = "cy@example.test", Kind = RecipientKind.Cc },
            new MailRecipient { Name = "Di", Email = "di@example.test", Kind = RecipientKind.Bcc },
        ],
        Attachments = attachments
    };

    [Fact]
    public void Recipients_show_to_cc_and_bcc_lines_with_names_and_no_duplicates()
    {
        Assert.Equal("To   Ann; bob@example.test\nCc   Cy\nBcc   Di", PstMessageHeader.Recipients(Message("")));
    }

    [Fact]
    public void Recipients_fall_back_to_the_summary_when_the_table_is_empty()
    {
        var m = new MailMessage { Summary = new MailSummary { Nid = 1, FolderNid = 2, To = "Someone" } };
        Assert.Equal("To   Someone", PstMessageHeader.Recipients(m));
    }

    [Fact]
    public void Inline_and_hidden_attachments_are_not_listed()
    {
        var m = Message("<img src=\"cid:logo@x\">",
            new MailAttachment { Nid = 1, FileName = "logo.png", ContentId = "<logo@x>", Size = 10 },
            new MailAttachment { Nid = 2, FileName = "unused-inline.png", ContentId = "other@x", Size = 20 },
            new MailAttachment { Nid = 3, FileName = "secret.dat", IsHidden = true, Size = 30 },
            new MailAttachment { Nid = 4, FileName = "report.pdf", Size = 1_300_000 });
        var visible = PstMessageHeader.VisibleAttachments(m);
        Assert.Equal(["unused-inline.png", "report.pdf"], visible.Select(a => a.FileName).ToArray());
        Assert.Equal("Attachments: unused-inline.png (20 B), report.pdf (1.2 MB)", PstMessageHeader.AttachmentLine(visible));
        Assert.Equal("", PstMessageHeader.AttachmentLine([]));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(310_000, "303 KB")]
    [InlineData(5_242_880, "5 MB")]
    public void Sizes_are_human_readable(long bytes, string expected) => Assert.Equal(expected, PstMessageHeader.FormatSize(bytes));

    [Fact]
    public void Headers_view_shows_the_stored_internet_headers()
    {
        var m = new MailMessage { Summary = new MailSummary { Nid = 1, FolderNid = 2 }, Headers = "Received: from a\r\nSubject: Hi\r\n" };
        Assert.Equal("Received: from a\nSubject: Hi", PstMessageHeader.HeadersText(m));
    }

    [Fact]
    public void Headers_view_falls_back_to_the_stored_properties_for_mail_without_internet_headers()
    {
        var m = new MailMessage
        {
            Summary = new MailSummary { Nid = 1, FolderNid = 2, From = "Ann", Subject = "Draft", MessageClass = "IPM.Note", Importance = 2, Size = 2048 },
            Recipients = [new MailRecipient { Name = "Bob", Email = "bob@example.test", Kind = RecipientKind.To }],
            HasText = true, HasRtf = true,
        };
        var text = PstMessageHeader.HeadersText(m);
        Assert.StartsWith("(This message has no internet headers", text);
        Assert.Contains("From: Ann", text);
        Assert.Contains("To: Bob <bob@example.test>", text);
        Assert.Contains("Importance: High", text);
        Assert.Contains("Size: 2 KB", text);
        Assert.Contains("Body formats: Rich Text, Plain Text", text);
    }
}
