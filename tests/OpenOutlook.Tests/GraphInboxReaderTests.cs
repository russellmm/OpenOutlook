using System.Net;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

public sealed class GraphInboxReaderTests
{
    [Fact]
    public async Task ListsBoundedInboxAndReadsPlainTextForVerifiedAccount()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            Assert.Equal("fake-access", request.Headers.Authorization?.Parameter);
            if (request.RequestUri.AbsolutePath == "/v1.0/me/mailFolders/inbox/messages")
                Assert.DoesNotContain("size", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
            requests.Add(request.RequestUri.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/v1.0/me" => Json("""{"id":"verified-id"}"""),
                "/v1.0/me/mailFolders/inbox" => Json("""{"id":"inbox-id","displayName":"Inbox","totalItemCount":82,"unreadItemCount":7}"""),
                "/v1.0/me/mailFolders/inbox/messages" => Json("""{"value":[{"id":"message-1","subject":"Hello","from":{"emailAddress":{"name":"Sender","address":"sender@example.test"}},"toRecipients":[{"emailAddress":{"address":"owner@example.test"}}],"receivedDateTime":"2026-09-25T16:00:00Z","hasAttachments":false,"isRead":false,"bodyPreview":"Short preview"}],"@odata.nextLink":"https://untrusted.example/never-follow"}"""),
                "/v1.0/me/messages/message-1" => Body(request),
                _ => throw new Exception("Unexpected Graph path")
            };
        }));
        var reader = new GraphInboxReader(http, "verified-id");
        var page = await reader.GetInboxAsync("fake-access");
        Assert.Equal(82, page.TotalCount);
        Assert.Equal(7, page.UnreadCount);
        Assert.True(page.HasMore);
        var message = Assert.Single(page.Messages);
        Assert.Equal("Sender", message.From);
        Assert.False(message.IsRead);
        Assert.Null(message.SizeBytes);
        Assert.Equal("owner@example.test", message.To);
        Assert.Equal("Plain text body", await reader.GetPlainTextBodyAsync("fake-access", message.Id));
        Assert.Equal(["/v1.0/me", "/v1.0/me/mailFolders/inbox", "/v1.0/me/mailFolders/inbox/messages",
            "/v1.0/me", "/v1.0/me/messages/message-1"], requests);
    }

    [Fact]
    public async Task DifferentAccountStopsBeforeMailboxRead()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            Assert.Equal("/v1.0/me", request.RequestUri!.AbsolutePath);
            return Json("""{"id":"different-account"}""");
        }));
        await Assert.ThrowsAsync<GraphMailException>(() =>
            new GraphInboxReader(http, "verified-id").GetInboxAsync("fake-access"));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task RedirectIsRejectedWithoutFollowingBearerToken()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri("https://untrusted.example/") } }));
        await Assert.ThrowsAsync<GraphMailException>(() =>
            new GraphInboxReader(http, "verified-id").GetInboxAsync("fake-access"));
    }

    [Fact]
    public async Task HtmlBodyIsNotReturnedAsDisplayText()
    {
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1.0/me" => Json("""{"id":"verified-id"}"""),
            "/v1.0/me/messages/message-1" => Json("""{"id":"message-1","body":{"contentType":"html","content":"<img src='https://example.test/tracker'>"}}"""),
            _ => throw new Exception("Unexpected Graph path")
        }));
        Assert.Null(await new GraphInboxReader(http, "verified-id")
            .GetPlainTextBodyAsync("fake-access", "message-1"));
    }

    [Fact]
    public async Task ReturnsHtmlBodyForInertDesktopPreview()
    {
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1.0/me" => Json("""{"id":"verified-id"}"""),
            "/v1.0/me/messages/message-1" => Json("""{"id":"message-1","body":{"contentType":"html","content":"<b>Rich</b>"}}"""),
            _ => throw new Exception("Unexpected Graph path")
        }));
        var body = await new GraphInboxReader(http, "verified-id")
            .GetMessageBodyAsync("fake-access", "message-1");
        Assert.Equal("html", body?.ContentType);
        Assert.Equal("<b>Rich</b>", body?.Content);
    }

    [Fact]
    public async Task ReadsSelectedFolderAndChecksItsIdentity()
    {
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1.0/me" => Json("""{"id":"verified-id"}"""),
            "/v1.0/me/mailFolders/sent-id" => Json("""{"id":"sent-id","displayName":"Sent Items","totalItemCount":2,"unreadItemCount":0}"""),
            "/v1.0/me/mailFolders/sent-id/messages" => Json("""{"value":[]}"""),
            _ => throw new Exception("Unexpected request")
        }));
        var page = await new GraphInboxReader(http, "verified-id").GetFolderAsync("fake-access", "sent-id");
        Assert.Equal("Sent Items", page.FolderName);
        Assert.Equal(2, page.TotalCount);
    }

    private static HttpResponseMessage Body(HttpRequestMessage request)
    {
        Assert.Contains(request.Headers.GetValues("Prefer"), value => value.Contains("body-content-type=\"text\""));
        return Json("""{"id":"message-1","body":{"contentType":"text","content":"Plain text body"}}""");
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    { Content = new StringContent(content) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
