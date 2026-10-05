using System.Net;
using System.Text;
using OpenOutlook.Providers.Google;

namespace OpenOutlook.Tests;

public sealed class GmailMailboxTests
{
    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static HttpClient Fake(Func<string, string> body, List<string>? urls = null) => new(new FakeHandler(request =>
    {
        var path = request.RequestUri!.PathAndQuery;
        urls?.Add(path);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(path), Encoding.UTF8, "application/json") };
    }));

    private static GmailMailbox Mailbox(HttpClient client, string token = "tok") =>
        new(client, _ => ValueTask.FromResult(token), "me@example.org");

    [Fact]
    public async Task Verifies_the_account_once_per_token_and_lists_labels_with_counts()
    {
        var urls = new List<string>();
        using var client = Fake(path => path switch
        {
            "/gmail/v1/users/me/profile" => """{"emailAddress":"ME@example.org"}""",
            "/gmail/v1/users/me/labels" => """{"labels":[{"id":"INBOX","name":"INBOX","type":"system"},{"id":"Label_1","name":"Receipts","type":"user"}]}""",
            "/gmail/v1/users/me/labels/INBOX" => """{"messagesTotal":120,"messagesUnread":7}""",
            "/gmail/v1/users/me/labels/Label_1" => """{"messagesTotal":3,"messagesUnread":0}""",
            _ => throw new Xunit.Sdk.XunitException("Unexpected " + path)
        }, urls);
        var box = Mailbox(client);
        var labels = await box.ListLabelsAsync();
        await box.ListLabelsAsync();
        Assert.Equal(2, labels.Count);
        Assert.Equal(new GmailLabel("INBOX", "INBOX", true, 120, 7), labels[0]);
        Assert.Equal(new GmailLabel("Label_1", "Receipts", false, 3, 0), labels[1]);
        Assert.Equal(1, urls.Count(u => u.EndsWith("/profile", StringComparison.Ordinal)));        // verified once for the same token
    }

    [Fact]
    public async Task Rejects_a_token_for_another_account()
    {
        using var client = Fake(_ => """{"emailAddress":"someone-else@example.org"}""");
        await Assert.ThrowsAsync<GmailReadException>(() => Mailbox(client).ListLabelsAsync());
    }

    [Fact]
    public async Task Lists_a_label_newest_first_and_builds_summaries_in_order()
    {
        using var client = Fake(path =>
        {
            if (path.EndsWith("/profile", StringComparison.Ordinal)) return """{"emailAddress":"me@example.org"}""";
            if (path.Contains("/messages?labelIds=INBOX", StringComparison.Ordinal)) return """{"messages":[{"id":"m2"},{"id":"m1"}]}""";
            var id = path.Contains("/messages/m1", StringComparison.Ordinal) ? "m1" : "m2";
            return $$$"""
            {"id":"{{{id}}}","threadId":"t","labelIds":["INBOX","UNREAD","STARRED"],"internalDate":"1700000000000","sizeEstimate":2048,"snippet":"hi there",
             "payload":{"mimeType":"multipart/mixed","headers":[{"name":"From","value":"Ann <ann@example.org>"},{"name":"Subject","value":"Subject {{{id}}}"},{"name":"To","value":"me@example.org"}]}}
            """;
        });
        var box = Mailbox(client);
        var ids = await box.ListLabelMessageIdsAsync("INBOX", 10);
        Assert.Equal(["m2", "m1"], ids);
        var summaries = await box.GetSummariesAsync(ids);
        Assert.Equal(["m2", "m1"], summaries.Select(s => s.Id));
        var first = summaries[0];
        Assert.Equal("Subject m2", first.Subject);
        Assert.Equal("Ann <ann@example.org>", first.From);
        Assert.True(first.IsUnread);
        Assert.True(first.IsStarred);
        Assert.True(first.HasAttachments);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), first.Date);
        Assert.Equal(2048, first.SizeBytes);
    }

    [Fact]
    public async Task Reads_html_and_text_bodies_and_attachment_names_from_nested_parts()
    {
        var html = "<p>Héllo</p>";
        using var client = Fake(path => path.EndsWith("/profile", StringComparison.Ordinal)
            ? """{"emailAddress":"me@example.org"}"""
            : $$$"""
            {"id":"m1","snippet":"s","payload":{"mimeType":"multipart/mixed","headers":[{"name":"Subject","value":"S"}],"parts":[
              {"mimeType":"multipart/alternative","parts":[
                {"mimeType":"text/plain","body":{"data":"{{{B64("plain body")}}}"}},
                {"mimeType":"text/html","body":{"data":"{{{B64(html)}}}"}}]},
              {"mimeType":"application/pdf","filename":"report.pdf","body":{"size":1234,"attachmentId":"x"}}]}}
            """);
        var content = await Mailbox(client).GetContentAsync("m1");
        Assert.Equal(html, content.Html);
        Assert.Equal("plain body", content.Text);
        Assert.Equal(new GmailAttachmentInfo("report.pdf", "application/pdf", 1234), Assert.Single(content.Attachments));
        Assert.Contains(content.Headers, h => h.Name == "Subject" && h.Value == "S");
    }

    [Fact]
    public async Task Rejects_invalid_ids_and_labels_before_any_request()
    {
        var calls = 0;
        using var client = Fake(_ => { calls++; return "{}"; });
        var box = Mailbox(client);
        await Assert.ThrowsAsync<ArgumentException>(() => box.GetContentAsync("../evil"));
        await Assert.ThrowsAsync<ArgumentException>(() => box.ListLabelMessageIdsAsync("bad?label"));
        Assert.Equal(0, calls);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
