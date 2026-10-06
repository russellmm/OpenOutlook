using System.Net;
using System.Text;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

[Collection("GraphVerificationCache")]
public sealed class GraphAccountVerificationTests
{
    private sealed class Handler(List<string> urls) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            urls.Add(url);
            var body = url.Contains("/messages?", StringComparison.Ordinal) ? "{\"value\":[]}"
                : url.Contains("/mailFolders/", StringComparison.Ordinal) ? "{\"id\":\"inbox-id\",\"displayName\":\"Inbox\",\"totalItemCount\":0,\"unreadItemCount\":0}"
                : "{\"id\":\"acct\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request });
        }
    }

    [Fact]
    public async Task A_token_is_checked_against_me_once_when_the_cache_is_on()
    {
        var urls = new List<string>();
        using var client = new HttpClient(new Handler(urls));
        var reader = new GraphInboxReader(client, "acct");
        GraphAccountVerification.CacheEnabled = true;
        try
        {
            await reader.GetInboxAsync("token-for-cache-test");
            await reader.GetInboxAsync("token-for-cache-test");
            Assert.Equal(1, urls.Count(u => u.Contains("/me?$select=id", StringComparison.Ordinal)));
            await reader.GetInboxAsync("a-different-token");                                  // a new token is checked again
            Assert.Equal(2, urls.Count(u => u.Contains("/me?$select=id", StringComparison.Ordinal)));
        }
        finally { GraphAccountVerification.CacheEnabled = false; }
    }

    [Fact]
    public async Task Every_call_checks_the_account_when_the_cache_is_off()
    {
        var urls = new List<string>();
        using var client = new HttpClient(new Handler(urls));
        var reader = new GraphInboxReader(client, "acct");
        await reader.GetInboxAsync("t1");
        await reader.GetInboxAsync("t1");
        Assert.Equal(2, urls.Count(u => u.Contains("/me?$select=id", StringComparison.Ordinal)));
    }
}
