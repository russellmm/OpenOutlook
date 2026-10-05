using System.Net;
using System.Text;
using System.Text.Json;
using OpenOutlook.Providers.Google;
using PstCore;

namespace OpenOutlook.Tests;

public sealed class GmailSendTests
{
    private static MailImport Parse(byte[] mime) => EmlParser.Parse(mime);

    [Fact]
    public void Builds_a_plain_message_that_parses_back_with_non_ascii_text()
    {
        var mime = GmailMimeBuilder.Build(new GmailOutgoing
        {
            From = "Russell <me@example.org>", To = "Jürgen Müller <j@example.org>, second@example.org", Cc = "cc@example.org",
            Subject = "Grüße €", Text = "Hello\nsecond line é"
        });
        var m = Parse(mime);
        Assert.Equal("Grüße €", m.Subject);
        Assert.Contains("second line é", m.BodyText);
        Assert.Equal(["j@example.org", "second@example.org", "cc@example.org"], m.Recipients.Select(r => r.Email));
        Assert.Contains(m.Recipients, r => r.Name == "Jürgen Müller");
        Assert.True(mime.All(b => b < 128), "the wire format must be 7-bit ASCII");
    }

    [Fact]
    public void Builds_html_with_alternative_text_and_attachments()
    {
        var data = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        var mime = GmailMimeBuilder.Build(new GmailOutgoing
        {
            From = "me@example.org", To = "you@example.org", Subject = "With files", Text = "plain version", Html = "<p>html <b>version</b></p>",
            InReplyTo = "<orig@example.org>", References = "<a@x> <orig@example.org>",
            Attachments = { new GmailOutgoingAttachment("résumé.bin", "application/octet-stream", data), new GmailOutgoingAttachment("note.txt", "text/plain", Encoding.UTF8.GetBytes("hi")) }
        });
        var m = Parse(mime);
        Assert.Contains("html", m.BodyHtml);
        Assert.Contains("plain version", m.BodyText);
        Assert.Equal(2, m.Attachments.Count);
        Assert.Equal("résumé.bin", m.Attachments[0].FileName);
        Assert.Equal(data, m.Attachments[0].Data);
        var text = Encoding.ASCII.GetString(mime);
        Assert.Contains("In-Reply-To: <orig@example.org>", text);
        Assert.Contains("References: <a@x> <orig@example.org>", text);
    }

    [Theory]
    [InlineData("Subject\r\nBcc: evil@example.org")]
    [InlineData("a\nb")]
    public void Refuses_line_breaks_in_headers(string subject) =>
        Assert.Throws<ArgumentException>(() => GmailMimeBuilder.Build(new GmailOutgoing { From = "me@example.org", To = "you@example.org", Subject = subject, Text = "x" }));

    [Theory]
    [InlineData("not an address")]
    [InlineData("a@b@c")]
    [InlineData("Name <bad>")]
    [InlineData("x@y.org\r\nBcc: z@z.org")]
    public void Refuses_invalid_recipients(string to) =>
        Assert.Throws<ArgumentException>(() => GmailMimeBuilder.Build(new GmailOutgoing { From = "me@example.org", To = to, Subject = "s", Text = "x" }));

    [Fact]
    public void Refuses_oversized_attachments_and_requires_a_sender()
    {
        Assert.Throws<ArgumentException>(() => GmailMimeBuilder.Build(new GmailOutgoing
        {
            From = "me@example.org", To = "a@b.org", Text = "x",
            Attachments = { new GmailOutgoingAttachment("big.bin", "application/octet-stream", new byte[26 * 1024 * 1024]) }
        }));
        Assert.Throws<ArgumentException>(() => GmailMimeBuilder.Build(new GmailOutgoing { To = "a@b.org", Text = "x" }));
    }

    [Fact]
    public void Base64Url_round_trips()
    {
        var bytes = Enumerable.Range(0, 300).Select(i => (byte)(i * 13)).ToArray();
        var text = GmailMimeBuilder.ToBase64Url(bytes);
        Assert.DoesNotContain('+', text);
        Assert.DoesNotContain('/', text);
        Assert.DoesNotContain('=', text);
        Assert.Equal(bytes, GmailMimeBuilder.FromBase64Url(text));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static (GmailMailbox Box, List<(string Method, string Path, string Body)> Calls) Make(Func<string, HttpStatusCode>? status = null)
    {
        var calls = new List<(string, string, string)>();
        var client = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath.Replace("/gmail/v1/users/me", "");
            var body = request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (path != "/profile") calls.Add((request.Method.Method, path, body));
            var code = path == "/profile" ? HttpStatusCode.OK : status?.Invoke(path) ?? HttpStatusCode.OK;
            var json = path switch
            {
                "/profile" => "{\"emailAddress\":\"me@example.org\"}",
                "/messages/send" => "{\"id\":\"sent-1\",\"threadId\":\"t1\"}",
                "/drafts" or "/drafts/send" => "{\"id\":\"draft-1\"}",
                var p when p.StartsWith("/drafts/", StringComparison.Ordinal) => "{\"id\":\"draft-1\"}",
                var p when p.Contains("/attachments/", StringComparison.Ordinal) => "{\"size\":4,\"data\":\"" + GmailMimeBuilder.ToBase64Url([1, 2, 3, 250]) + "\"}",
                _ => "{}"
            };
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }));
        return (new GmailMailbox(client, _ => ValueTask.FromResult("tok"), "me@example.org"), calls);
    }

    [Fact]
    public async Task Sends_raw_mime_in_a_thread_and_saves_and_sends_drafts()
    {
        var (box, calls) = Make();
        var mime = GmailMimeBuilder.Build(new GmailOutgoing { From = "me@example.org", To = "you@example.org", Subject = "Hi", Text = "x" });
        Assert.Equal("sent-1", await box.SendAsync(mime, "thread9"));
        var sent = JsonDocument.Parse(calls[0].Body).RootElement;
        Assert.Equal(("POST", "/messages/send"), (calls[0].Method, calls[0].Path));
        Assert.Equal("thread9", sent.GetProperty("threadId").GetString());
        Assert.Equal(mime, GmailMimeBuilder.FromBase64Url(sent.GetProperty("raw").GetString()!));

        Assert.Equal("draft-1", await box.SaveDraftAsync(null, mime));
        Assert.Equal(("POST", "/drafts"), (calls[1].Method, calls[1].Path));
        Assert.True(JsonDocument.Parse(calls[1].Body).RootElement.GetProperty("message").TryGetProperty("raw", out _));
        Assert.Equal("draft-1", await box.SaveDraftAsync("draft-1", mime));
        Assert.Equal(("PUT", "/drafts/draft-1"), (calls[2].Method, calls[2].Path));
        await box.SendDraftAsync("draft-1");
        Assert.Equal(("POST", "/drafts/send"), (calls[3].Method, calls[3].Path));
    }

    [Fact]
    public async Task Downloads_attachments_and_validates_ids()
    {
        var (box, calls) = Make();
        Assert.Equal(new byte[] { 1, 2, 3, 250 }, await box.GetAttachmentAsync("m1", "ANGjdJ_abc-123"));
        Assert.Equal("/messages/m1/attachments/ANGjdJ_abc-123", calls[0].Path);
        await Assert.ThrowsAsync<ArgumentException>(() => box.GetAttachmentAsync("m1", "../x"));
        await Assert.ThrowsAsync<ArgumentException>(() => box.GetAttachmentAsync("m 1", "abc"));
    }

    [Fact]
    public async Task A_refused_send_explains_that_sending_needs_a_new_sign_in()
    {
        var (box, _) = Make(path => path == "/messages/send" ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
        var mime = GmailMimeBuilder.Build(new GmailOutgoing { From = "me@example.org", To = "you@example.org", Subject = "Hi", Text = "x" });
        var error = await Assert.ThrowsAsync<GmailReadException>(() => box.SendAsync(mime));
        Assert.Contains("Sign in again", error.Message);
    }
}
