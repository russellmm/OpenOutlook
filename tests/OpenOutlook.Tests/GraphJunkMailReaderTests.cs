using System.Net;
using System.Text;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

public sealed class GraphJunkMailReaderTests
{
    [Fact]
    public async Task Reads_only_verified_accounts_well_known_junk_folder_and_paginates()
    {
        var requests = new List<string>();
        using var client = Client(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("secret-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("graph.microsoft.com", request.RequestUri?.Host);
            var url = request.RequestUri!.AbsoluteUri;
            requests.Add(url);
            return Json(requests.Count switch
            {
                1 => """{"id":"account-1"}""",
                2 => """{"id":"opaque-folder-id","displayName":"Junk Email"}""",
                3 => """{"value":[{"id":"m1","subject":"subject","from":{"emailAddress":{"address":"from@example.org"}},"sender":{"emailAddress":{"address":"sender@example.org"}},"toRecipients":[{"emailAddress":{"address":"to@example.org"}}],"ccRecipients":[{"emailAddress":{"address":"cc@example.org"}}],"importance":"high","internetMessageHeaders":[{"name":"X-Test","value":"header"}]}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/mailFolders/opaque-folder-id/messages?$skiptoken=abc&$top=2"}""",
                4 => """{"value":[{"id":"m2"}]}""",
                _ => throw new Xunit.Sdk.XunitException("Unexpected request")
            });
        });
        var reader = new GraphJunkMailReader(client, _ => ValueTask.FromResult("secret-token"), "account-1");
        var messages = await reader.ListJunkMessagesAsync(pageSize: 2, maxMessages: 3);
        Assert.Equal(2, messages.Count);
        Assert.Equal("from@example.org", messages[0].From);
        Assert.Equal("sender@example.org", messages[0].Sender);
        Assert.Equal(["to@example.org"], messages[0].ToRecipients);
        Assert.Equal(["cc@example.org"], messages[0].CcRecipients);
        Assert.Equal("high", messages[0].Importance);
        Assert.True(messages[0].HasToRecipients);
        Assert.Null(messages[1].HasToRecipients);
        Assert.Equal(new GraphInternetHeader("X-Test", "header"), Assert.Single(messages[0].InternetMessageHeaders));
        Assert.All(requests, url =>
        {
            Assert.StartsWith("https://graph.microsoft.com/v1.0/me", url, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-token", url, StringComparison.Ordinal);
        });
        Assert.Contains("/mailFolders/junkemail?", requests[1], StringComparison.Ordinal);
        Assert.Contains("/mailFolders/opaque-folder-id/messages?", requests[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotating_token_provider_is_called_once_for_me_folder_and_all_pages()
    {
        var tokenCalls = 0;
        var requests = new List<(string Path, string? Bearer)>();
        using var client = Client(request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter));
            return Json(requests.Count switch
            {
                1 => """{"id":"account"}""",
                2 => """{"id":"junk-id"}""",
                3 => """{"value":[{"id":"one"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/mailFolders/junk-id/messages?$skiptoken=next&$top=1"}""",
                4 => """{"value":[{"id":"two"}]}""",
                _ => throw new Xunit.Sdk.XunitException("Unexpected request")
            });
        });
        var reader = new GraphJunkMailReader(client,
            _ => ValueTask.FromResult($"rotating-token-{++tokenCalls}"), "account");
        Assert.Equal(2, (await reader.ListJunkMessagesAsync(pageSize: 1, maxMessages: 2)).Count);
        Assert.Equal(1, tokenCalls);
        Assert.Equal(4, requests.Count);
        Assert.Equal("/v1.0/me", requests[0].Path);
        Assert.Equal("/v1.0/me/mailFolders/junkemail", requests[1].Path);
        Assert.All(requests, request => Assert.Equal("rotating-token-1", request.Bearer));
    }

    [Fact]
    public async Task Rotating_token_provider_is_called_once_for_get_junk_folder()
    {
        var tokenCalls = 0;
        var requests = new List<string?>();
        using var client = Client(request =>
        {
            requests.Add(request.Headers.Authorization?.Parameter);
            return Json(requests.Count switch
            {
                1 => """{"id":"account"}""",
                2 => """{"id":"junk-id"}""",
                _ => throw new Xunit.Sdk.XunitException("Unexpected request")
            });
        });
        var reader = new GraphJunkMailReader(client,
            _ => ValueTask.FromResult($"rotating-token-{++tokenCalls}"), "account");
        Assert.Equal("junk-id", (await reader.GetJunkFolderAsync()).Id);
        Assert.Equal(1, tokenCalls);
        Assert.Equal(["rotating-token-1", "rotating-token-1"], requests);
    }

    [Fact]
    public async Task Redirect_is_rejected_without_following_location_or_exposing_token()
    {
        var calls = 0;
        using var client = Client(request =>
        {
            ++calls;
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://evil.example/steal-token") }
            };
        });
        var reader = new GraphJunkMailReader(client, _ => ValueTask.FromResult("secret-token"), "account");
        var error = await Assert.ThrowsAsync<GraphMailException>(() => reader.GetJunkFolderAsync());
        Assert.Equal(1, calls);
        Assert.Equal(HttpStatusCode.Redirect, error.StatusCode);
        Assert.DoesNotContain("secret-token", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_account_stops_before_folder_or_messages()
    {
        var calls = 0;
        using var client = Client(_ => { calls++; return Json("""{"id":"another-account"}"""); });
        var reader = new GraphJunkMailReader(client, _ => ValueTask.FromResult("secret"), "expected-account");
        var error = await Assert.ThrowsAsync<GraphMailException>(() => reader.ListJunkMessagesAsync());
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("https://evil.example/v1.0/me/mailFolders/junk-id/messages?$top=1")]
    [InlineData("http://graph.microsoft.com/v1.0/me/mailFolders/junk-id/messages?$top=1")]
    [InlineData("https://graph.microsoft.com/v1.0/me/mailFolders/other-id/messages?$top=1")]
    [InlineData("https://graph.microsoft.com/v1.0/users/victim/mailFolders/junk-id/messages?$top=1")]
    [InlineData("https://graph.microsoft.com/v1.0/me/mailFolders/junk-id/messages?$select=body")]
    public async Task Unsafe_pagination_does_not_send_followup_request(string nextLink)
    {
        var calls = 0;
        using var client = Client(_ => Json(++calls switch
        {
            1 => """{"id":"account"}""",
            2 => """{"id":"junk-id"}""",
            3 => """{"value":[],"@odata.nextLink":""" + nextLink + "\"}",
            _ => throw new Xunit.Sdk.XunitException("Unsafe followup sent")
        }));
        var reader = new GraphJunkMailReader(client, _ => ValueTask.FromResult("secret"), "account");
        await Assert.ThrowsAsync<GraphMailException>(() => reader.ListJunkMessagesAsync(pageSize: 1));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Throttling_reports_bounded_retry_without_exposing_response_or_token()
    {
        using var client = Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Content = new StringContent("secret-token in response") };
            response.Headers.TryAddWithoutValidation("Retry-After", "10000");
            return response;
        });
        var reader = new GraphJunkMailReader(client, _ => ValueTask.FromResult("secret-token"), "account");
        var error = await Assert.ThrowsAsync<GraphMailException>(() => reader.GetJunkFolderAsync());
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(300), error.RetryAfter);
        Assert.DoesNotContain("secret-token", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_prevents_access_token_request_and_network_call()
    {
        using var client = Client(_ => throw new Xunit.Sdk.XunitException("Network call after cancellation"));
        var reader = new GraphJunkMailReader(client, _ => throw new Xunit.Sdk.XunitException("Token call after cancellation"), "account");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ListJunkMessagesAsync(cancellationToken: cts.Token));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new FakeHandler(responder));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
