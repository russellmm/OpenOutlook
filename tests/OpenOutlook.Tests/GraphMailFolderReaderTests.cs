using System.Net;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

public sealed class GraphMailFolderReaderTests
{
    [Fact]
    public async Task ListsVisibleRootAndChildFoldersWithPagination()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/v1.0/me" => Json("""{"id":"verified-id"}"""),
                "/v1.0/me/mailFolders" when request.RequestUri.Query.Contains("skiptoken") =>
                    Json("""{"value":[{"id":"sent-id","displayName":"Sent Items","childFolderCount":0,"totalItemCount":3,"unreadItemCount":0}]}"""),
                "/v1.0/me/mailFolders" => Json("""{"value":[{"id":"inbox-id","displayName":"Inbox","childFolderCount":1,"totalItemCount":8,"unreadItemCount":2},{"id":"hidden-id","displayName":"Hidden","isHidden":true,"childFolderCount":0,"totalItemCount":0,"unreadItemCount":0}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/mailFolders?$skiptoken=next"}"""),
                "/v1.0/me/mailFolders/inbox-id/childFolders" =>
                    Json("""{"value":[{"id":"archive-id","displayName":"Archive","childFolderCount":0,"totalItemCount":1,"unreadItemCount":0}]}"""),
                _ => throw new Exception("Unexpected request")
            };
        }));
        var folders = await new GraphMailFolderReader(http, "verified-id").GetFoldersAsync("fake-access");
        Assert.Equal(2, folders.Count);
        Assert.Equal("Inbox", folders[0].DisplayName);
        Assert.Equal(2, folders[0].UnreadCount);
        Assert.Equal("Archive", Assert.Single(folders[0].Children).DisplayName);
        Assert.Equal("Sent Items", folders[1].DisplayName);
        Assert.Equal(4, paths.Count);
    }

    [Fact]
    public async Task RejectsUnsafePaginationLink()
    {
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1.0/me" => Json("""{"id":"verified-id"}"""),
            _ => Json("""{"value":[],"@odata.nextLink":"https://another.example/me/mailFolders"}""")
        }));
        await Assert.ThrowsAsync<GraphMailException>(() =>
            new GraphMailFolderReader(http, "verified-id").GetFoldersAsync("fake-access"));
    }

    [Fact]
    public async Task DifferentAccountStopsBeforeFolderListing()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            requests++;
            return Json("""{"id":"another-account"}""");
        }));
        await Assert.ThrowsAsync<GraphMailException>(() =>
            new GraphMailFolderReader(http, "verified-id").GetFoldersAsync("fake-access"));
        Assert.Equal(1, requests);
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
