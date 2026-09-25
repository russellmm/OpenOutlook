using System.Net;
using System.Text;
using OpenOutlook.Providers.Google;

namespace OpenOutlook.Tests;

public sealed class GmailInboxReaderTests
{
    [Fact]
    public async Task Verifies_profile_then_lists_bounded_inbox_ids_by_page_token()
    {
        var urls = new List<string>();
        using var client = Client(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("secret-token", request.Headers.Authorization?.Parameter);
            urls.Add(request.RequestUri!.AbsoluteUri);
            return Json(urls.Count switch
            {
                1 => """{"emailAddress":"User@Example.Org"}""",
                2 => """{"messages":[{"id":"a1"},{"id":"b2"}],"nextPageToken":"abc+/="}""",
                3 => """{"messages":[{"id":"c3"}],"nextPageToken":"another"}""",
                _ => throw new Xunit.Sdk.XunitException("Unexpected request")
            });
        });
        var reader = new GmailInboxReader(client, _ => ValueTask.FromResult("secret-token"), "user@example.org");
        Assert.Equal(["a1", "b2", "c3"], await reader.ListInboxMessageIdsAsync(pageSize: 2, maxMessages: 3));
        Assert.Equal("https://gmail.googleapis.com/gmail/v1/users/me/profile", urls[0]);
        Assert.Equal("https://gmail.googleapis.com/gmail/v1/users/me/messages?labelIds=INBOX&maxResults=2", urls[1]);
        Assert.Equal("https://gmail.googleapis.com/gmail/v1/users/me/messages?labelIds=INBOX&maxResults=1&pageToken=abc%2B%2F%3D", urls[2]);
        Assert.All(urls, url => Assert.DoesNotContain("secret-token", url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Uses_exact_verified_token_for_all_pages_even_if_callback_rotates()
    {
        var tokenCalls = 0;
        var calls = 0;
        using var client = Client(request =>
        {
            Assert.Equal("verified-token", request.Headers.Authorization?.Parameter);
            return Json(++calls switch
            {
                1 => """{"emailAddress":"owner@example.org"}""",
                2 => """{"messages":[],"nextPageToken":"next"}""",
                3 => """{"messages":[]}""",
                _ => throw new Xunit.Sdk.XunitException("Unexpected request")
            });
        });
        var reader = new GmailInboxReader(client, _ => ValueTask.FromResult(++tokenCalls == 1 ? "verified-token" : "other-token"),
            "owner@example.org");
        Assert.Empty(await reader.ListInboxMessageIdsAsync());
        Assert.Equal(1, tokenCalls);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Different_account_stops_before_listing_or_fetching()
    {
        var calls = 0;
        using var client = Client(_ => { calls++; return Json("""{"emailAddress":"other@example.org"}"""); });
        var reader = new GmailInboxReader(client, _ => ValueTask.FromResult("secret-token"), "owner@example.org");
        var error = await Assert.ThrowsAsync<GmailReadException>(() => reader.ListInboxMessageIdsAsync());
        Assert.DoesNotContain("secret-token", error.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<GmailReadException>(() => reader.GetMessageAsync("abc"));
        Assert.Equal(2, calls); // Both operations only request /profile.
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("../users/other/messages")]
    [InlineData("abc%0D%0A")]
    [InlineData("")]
    public async Task Invalid_pagination_token_cannot_create_followup_request(string token)
    {
        var calls = 0;
        using var client = Client(_ => Json(++calls switch
        {
            1 => """{"emailAddress":"owner@example.org"}""",
            2 => """{"messages":[],"nextPageToken":""" + token + "\"}",
            _ => throw new Xunit.Sdk.XunitException("Unsafe followup request")
        }));
        var reader = new GmailInboxReader(client, _ => ValueTask.FromResult("token"), "owner@example.org");
        await Assert.ThrowsAsync<GmailReadException>(() => reader.ListInboxMessageIdsAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Repeated_pagination_token_is_rejected()
    {
        var calls = 0;
        using var client = Client(_ => Json(++calls switch
        {
            1 => """{"emailAddress":"owner@example.org"}""",
            2 or 3 => """{"messages":[],"nextPageToken":"same"}""",
            _ => throw new Xunit.Sdk.XunitException("Cycle followup request")
        }));
        var reader = new GmailInboxReader(client, _ => ValueTask.FromResult("token"), "owner@example.org");
        await Assert.ThrowsAsync<GmailReadException>(() => reader.ListInboxMessageIdsAsync());
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"messages\":{}}")]
    [InlineData("{\"messages\":[{}]}")]
    [InlineData("{\"nextPageToken\":47}")]
    public async Task Malformed_message_page_is_rejected(string page)
    {
        var calls = 0;
        using var client = Client(_ => Json(++calls == 1 ? """{"emailAddress":"owner@example.org"}""" : page));
        var reader = new GmailInboxReader(client, _ => ValueTask.FromResult("token"), "owner@example.org");
        await Assert.ThrowsAsync<GmailReadException>(() => reader.ListInboxMessageIdsAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Full_fetch_decodes_plain_text_only_and_reverifies_account()
    {
        var urls = new List<string>();
        using var client = Client(request =>
        {
            urls.Add(request.RequestUri!.AbsoluteUri);
            return Json(urls.Count == 1 ? """{"emailAddress":"owner@example.org"}""" :
                """{"id":"id1","threadId":"thread","snippet":"excerpt","payload":{"mimeType":"multipart/alternative","headers":[{"name":"Subject","value":"test"}],"parts":[{"mimeType":"text/plain","body":{"data":"aGVsbG8"}},{"mimeType":"text/html","body":{"data":"PHNjcmlwdD4"}}]}}""");
        });
        var reader = new GmailInboxReader(client, _ => ValueTask.FromResult("token"), "owner@example.org");
        var message = await reader.GetMessageAsync("id1", GmailMessageFormat.Full);
        Assert.Equal("thread", message.ThreadId);
        Assert.Equal(new GmailHeader("Subject", "test"), Assert.Single(message.Headers));
        Assert.Equal("hello", Assert.Single(message.PlainTextBodies));
        Assert.Equal("https://gmail.googleapis.com/gmail/v1/users/me/messages/id1?format=full", urls[1]);
    }

    [Fact]
    public async Task Non_success_response_never_exposes_body_or_bearer()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Content = new StringContent("secret-token in response"),
            Headers = { Location = new Uri("https://evil.example/steal") }
        });
        var reader = new GmailInboxReader(client, _ => ValueTask.FromResult("secret-token"), "owner@example.org");
        var error = await Assert.ThrowsAsync<GmailReadException>(() => reader.ListInboxMessageIdsAsync());
        Assert.Equal(HttpStatusCode.Found, error.StatusCode);
        Assert.DoesNotContain("secret-token", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_response_is_rejected()
    {
        using var client = Client(_ => Json(new string('x', 4 * 1024 * 1024 + 1)));
        var reader = new GmailInboxReader(client, _ => ValueTask.FromResult("token"), "owner@example.org");
        await Assert.ThrowsAsync<GmailReadException>(() => reader.ListInboxMessageIdsAsync());
    }

    [Fact]
    public async Task Cancellation_prevents_token_and_network_calls()
    {
        using var client = Client(_ => throw new Xunit.Sdk.XunitException("Network call after cancellation"));
        var reader = new GmailInboxReader(client, _ => throw new Xunit.Sdk.XunitException("Token call after cancellation"), "owner@example.org");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ListInboxMessageIdsAsync(cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.GetMessageAsync("abc", cancellationToken: cts.Token));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) => new(new FakeHandler(responder));
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

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
