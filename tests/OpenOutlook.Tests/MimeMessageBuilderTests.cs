using System.Text;
using PstCore;

namespace OpenOutlook.Tests;

public sealed class MimeMessageBuilderTests
{
    private static MailMessage Message(string subject = "Hello", string text = "plain body", string html = "", int importance = 1,
        IReadOnlyList<MailAttachment>? attachments = null, string headers = "", IReadOnlyList<MailRecipient>? recipients = null) => new()
    {
        Summary = new MailSummary
        {
            Nid = 1, FolderNid = 2, Subject = subject, From = "Ann Example <ann@example.org>", To = "bob@example.org",
            Sent = new DateTime(2026, 10, 1, 12, 30, 0, DateTimeKind.Utc), Received = new DateTime(2026, 10, 1, 12, 31, 0, DateTimeKind.Utc), Importance = importance
        },
        BodyText = text, BodyHtml = html, Headers = headers,
        Recipients = recipients ?? [new MailRecipient { Name = "Bob", Email = "bob@example.org", Kind = RecipientKind.To }],
        Attachments = attachments ?? []
    };

    private static MailAttachment Att(uint nid, string name, string mime, string cid = "", bool hidden = false) =>
        new() { Nid = nid, FileName = name, MimeTag = mime, ContentId = cid, Method = 1, IsHidden = hidden };

    [Fact]
    public void A_plain_message_round_trips_with_unicode_in_every_header()
    {
        var m = Message(subject: "Grüße 🎉 – naïve café", text: "Zeile eins\nZeile zwei é");
        var raw = MimeMessageBuilder.Build(m, _ => throw new InvalidOperationException());
        Assert.All(raw, b => Assert.True(b < 128, "the wire format must be 7-bit ASCII"));
        var parsed = EmlParser.Parse(raw);
        Assert.Equal("Grüße 🎉 – naïve café", parsed.Subject);
        Assert.Contains("Zeile zwei é", parsed.BodyText);
        Assert.Equal("ann@example.org", parsed.SenderEmail);
        Assert.Equal("bob@example.org", Assert.Single(parsed.Recipients).Email);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 30, 0), parsed.Sent!.Value.ToUniversalTime());
    }

    [Fact]
    public void Html_and_text_become_alternatives_and_inline_pictures_keep_their_content_id()
    {
        var png = Enumerable.Range(0, 3000).Select(i => (byte)(i * 13)).ToArray();
        var pdf = Enumerable.Range(0, 70_000).Select(i => (byte)(i * 7)).ToArray();
        var m = Message(text: "text version", html: "<p>see <img src=\"cid:logo1@x\"></p>", attachments:
        [
            Att(10, "logo.png", "image/png", cid: "logo1@x"),
            Att(11, "résumé – final.pdf", "application/pdf")
        ]);
        var raw = MimeMessageBuilder.Build(m, a => a.Nid == 10 ? png : pdf);
        var parsed = EmlParser.Parse(raw);
        Assert.Contains("cid:logo1@x", parsed.BodyHtml);
        Assert.Contains("text version", parsed.BodyText);
        var inline = Assert.Single(parsed.Attachments, a => a.ContentId.Length > 0);
        Assert.Equal("logo1@x", inline.ContentId.Trim('<', '>'));
        Assert.Equal(png, inline.Data);
        var file = Assert.Single(parsed.Attachments, a => a.ContentId.Length == 0);
        Assert.Equal("résumé – final.pdf", file.FileName);
        Assert.Equal(pdf, file.Data);
    }

    [Fact]
    public void Message_id_threading_headers_and_importance_are_kept_but_other_headers_are_not_copied()
    {
        var headers = "Received: from mx.example.org by x\r\nMessage-ID: <abc123@example.org>\r\nIn-Reply-To: <prev@example.org>\r\n" +
                      "References: <a@x>\r\n <prev@example.org>\r\nX-Spam-Score: 9\r\n";
        var raw = Encoding.ASCII.GetString(MimeMessageBuilder.Build(Message(importance: 2, headers: headers), _ => []));
        Assert.Contains("Message-ID: <abc123@example.org>", raw);
        Assert.Contains("In-Reply-To: <prev@example.org>", raw);
        Assert.Contains("References: <a@x> <prev@example.org>", raw);
        Assert.Contains("Importance: high", raw);
        Assert.DoesNotContain("X-Spam-Score", raw);
        Assert.DoesNotContain("Received:", raw);
        Assert.Contains("Importance: low", Encoding.ASCII.GetString(MimeMessageBuilder.Build(Message(importance: 0), _ => [])));
    }

    [Fact]
    public void Cc_and_bcc_recipients_and_a_display_name_with_a_comma_survive()
    {
        var m = Message(recipients:
        [
            new MailRecipient { Name = "Example, Bob", Email = "bob@example.org", Kind = RecipientKind.To },
            new MailRecipient { Name = "Çelik", Email = "c@example.org", Kind = RecipientKind.Cc },
            new MailRecipient { Name = "", Email = "hidden@example.org", Kind = RecipientKind.Bcc }
        ]);
        var parsed = EmlParser.Parse(MimeMessageBuilder.Build(m, _ => []));
        Assert.Equal(["To:Example, Bob<bob@example.org>", "Cc:Çelik<c@example.org>", "Bcc:<hidden@example.org>"],
            parsed.Recipients.Select(r => $"{r.Kind}:{r.Name}<{r.Email}>").ToArray());
    }

    [Fact]
    public void An_embedded_item_or_unreadable_attachment_stops_the_transfer_instead_of_dropping_data()
    {
        var embedded = new MailAttachment { Nid = 5, FileName = "forwarded.msg", Method = 5 };
        Assert.Throws<NotSupportedException>(() => MimeMessageBuilder.Build(Message(attachments: [embedded]), _ => []));
        Assert.Throws<IOException>(() => MimeMessageBuilder.Build(Message(attachments: [Att(6, "a.bin", "application/octet-stream")]), _ => throw new InvalidOperationException("gone")));
    }

    [Fact]
    public void Header_text_cannot_inject_extra_headers()
    {
        var raw = Encoding.ASCII.GetString(MimeMessageBuilder.Build(Message(subject: "Hi\r\nBcc: evil@example.org"), _ => []));
        var headerBlock = raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        Assert.DoesNotContain("\r\nBcc: evil@example.org", headerBlock);
    }
}
