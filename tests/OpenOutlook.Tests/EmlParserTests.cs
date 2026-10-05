using System.Text;
using PstCore;

namespace OpenOutlook.Tests;

public sealed class EmlParserTests
{
    private static MailImport Parse(string eml, bool markRead = false) => EmlParser.Parse(Encoding.Latin1.GetBytes(eml), markRead);
    private static MailImport ParseUtf8(string eml) => EmlParser.Parse(Encoding.UTF8.GetBytes(eml));

    [Fact]
    public void Simple_text_message_with_encoded_headers_and_a_quoted_printable_latin1_body()
    {
        var mail = Parse(
            "From: =?UTF-8?B?SsO8cmdlbiBNw7xsbGVy?= <juergen@example.test>\r\n" +
            "To: \"Smith, Ann\" <ann@example.test>, bob@example.test (Bob B)\r\n" +
            "Cc: team: x@example.test, y@example.test;\r\n" +
            "Subject: =?iso-8859-1?Q?Caf=E9_men=FC?= =?iso-8859-1?Q?_today?=\r\n" +
            "Date: Tue, 14 Jan 2020 10:30:00 -0800\r\n" +
            "Message-ID: <abc@example.test>\r\n" +
            "Importance: High\r\n" +
            "Received: from a by b; Tue, 14 Jan 2020 18:31:00 +0000\r\n" +
            "Content-Type: text/plain; charset=iso-8859-1\r\n" +
            "Content-Transfer-Encoding: quoted-printable\r\n" +
            "\r\n" +
            "Caf=E9 au lait=\r\n" +
            " and more\r\n");
        Assert.Equal("Jürgen Müller", mail.SenderName);
        Assert.Equal("juergen@example.test", mail.SenderEmail);
        Assert.Equal("Café menü today", mail.Subject);
        Assert.Equal(new DateTime(2020, 1, 14, 18, 30, 0, DateTimeKind.Utc), mail.Sent);
        Assert.Equal(new DateTime(2020, 1, 14, 18, 31, 0, DateTimeKind.Utc), mail.Received);
        Assert.Equal("<abc@example.test>", mail.MessageId);
        Assert.Equal(2, mail.Importance);
        Assert.Equal("Café au lait and more", mail.BodyText.Trim().Replace("\r\n", "\n"));
        Assert.Collection(mail.Recipients,
            r => { Assert.Equal(("Smith, Ann", "ann@example.test", RecipientKind.To), (r.Name, r.Email, r.Kind)); },
            r => { Assert.Equal(("Bob B", "bob@example.test", RecipientKind.To), (r.Name, r.Email, r.Kind)); },
            r => { Assert.Equal(("", "x@example.test", RecipientKind.Cc), (r.Name, r.Email, r.Kind)); },
            r => { Assert.Equal(("", "y@example.test", RecipientKind.Cc), (r.Name, r.Email, r.Kind)); });
        Assert.StartsWith("From: ", mail.TransportHeaders);
    }

    [Fact]
    public void Multipart_with_alternative_bodies_inline_picture_and_attachments()
    {
        var pdf = Convert.ToBase64String(Enumerable.Range(0, 300).Select(i => (byte)(i * 3)).ToArray(), Base64FormattingOptions.InsertLineBreaks).Replace("\n", "\r\n");
        var mail = ParseUtf8(
            "From: dan@example.test\r\nTo: ann@example.test\r\nSubject: Report\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed;\r\n boundary=\"outer\"\r\n\r\n" +
            "preamble ignored\r\n" +
            "--outer\r\n" +
            "Content-Type: multipart/alternative; boundary=alt\r\n\r\n" +
            "--alt\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nPlain body é\r\n" +
            "--alt\r\nContent-Type: multipart/related; boundary=rel\r\n\r\n" +
            "--rel\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes("<p>Hi <img src=\"cid:logo@x\"> é</p>")) + "\r\n" +
            "--rel\r\nContent-Type: image/png; name=\"logo.png\"\r\nContent-ID: <logo@x>\r\nContent-Transfer-Encoding: base64\r\nContent-Disposition: inline\r\n\r\n" +
            Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]) + "\r\n" +
            "--rel--\r\n--alt--\r\n" +
            "--outer\r\n" +
            "Content-Type: application/pdf\r\nContent-Disposition: attachment;\r\n filename*=utf-8''r%C3%A9sum%C3%A9.pdf\r\nContent-Transfer-Encoding: base64\r\n\r\n" + pdf + "\r\n" +
            "--outer--\r\ntrailing epilogue\r\n");
        Assert.Equal("Plain body é", mail.BodyText.Trim());
        Assert.Equal("<p>Hi <img src=\"cid:logo@x\"> é</p>", mail.BodyHtml);
        Assert.Equal(2, mail.Attachments.Count);
        var logo = mail.Attachments[0];
        Assert.Equal(("logo.png", "image/png", "logo@x"), (logo.FileName, logo.MimeType, logo.ContentId));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }, logo.Data);
        var report = mail.Attachments[1];
        Assert.Equal(("résumé.pdf", "application/pdf", ""), (report.FileName, report.MimeType, report.ContentId));
        Assert.Equal(Enumerable.Range(0, 300).Select(i => (byte)(i * 3)).ToArray(), report.Data);
    }

    [Fact]
    public void Sloppy_input_is_tolerated()
    {
        var mail = Parse("Subject: no charset, bare LF\nFrom: x@example.test\nContent-Type: text/plain\n\nbody é in latin-1\n", markRead: true);
        Assert.Equal("no charset, bare LF", mail.Subject);
        Assert.True(mail.Read);
        Assert.Contains("body", mail.BodyText);
        Assert.Contains('é', mail.BodyText);                       // bytes that are not UTF-8 fall back to Windows-1252
        Assert.Null(Parse("Subject: x\r\nDate: not a date\r\n\r\nb").Sent);
    }

    [Fact]
    public void A_file_that_is_not_mail_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => EmlParser.Parse(new byte[] { 0x00, 0x01, 0x02, 0x03 }));
        Assert.Throws<InvalidDataException>(() => EmlParser.Parse(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData("=?UTF-8?Q?a=C3=A9_b?=", "aé b")]
    [InlineData("=?utf-8?b?w6k=?= =?utf-8?b?w6k=?=", "éé")]
    [InlineData("plain =?bogus-charset?Q?x?= text", "plain x text")]
    [InlineData("=?UTF-8?B?not base64!?=", "=?UTF-8?B?not base64!?=")]
    public void Encoded_words_decode(string input, string expected) => Assert.Equal(expected, EmlParser.Decode(input));
}
