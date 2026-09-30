using System.Net;
using OpenOutlook.Desktop;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

public sealed class GraphMailBatchDeleterTests
{
    [Fact]
    public async Task Canceling_permanent_delete_changes_no_messages()
    {
        var mutations = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get) mutations++;
            var id = path.Split('/').Last();
            return path switch
            {
                "/v1.0/me" => Json(HttpStatusCode.OK, "{\"id\":\"owner\"}", request),
                "/v1.0/me/mailFolders/deleteditems" => Json(HttpStatusCode.OK, "{\"id\":\"trash\"}", request),
                _ when path.StartsWith("/v1.0/me/messages/", StringComparison.Ordinal) =>
                    Json(HttpStatusCode.OK, $"{{\"id\":\"{id}\",\"parentFolderId\":\"trash\"}}", request),
                _ => throw new InvalidOperationException(path)
            };
        }));
        var prompted = 0;
        var result = await GraphMailBatchDeleter.DeleteAsync(new GraphMailWriter(http, "owner"),
            "access", [Message("first"), Message("second")], count =>
            {
                prompted = count;
                return Task.FromResult(false);
            });
        Assert.Equal(2, prompted);
        Assert.True(result.Canceled);
        Assert.Empty(result.Completed);
        Assert.Equal(0, mutations);
    }

    [Fact]
    public async Task Deletes_every_selected_draft_once()
    {
        var moved = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var id = path.Split('/').Last();
            if (request.Method == HttpMethod.Post && path.EndsWith("/move", StringComparison.Ordinal))
            {
                moved.Add(path.Split('/')[4]);
                return Json(HttpStatusCode.Created, "{\"id\":\"moved\"}", request);
            }
            return path switch
            {
                "/v1.0/me" => Json(HttpStatusCode.OK, "{\"id\":\"owner\"}", request),
                "/v1.0/me/mailFolders/deleteditems" => Json(HttpStatusCode.OK, "{\"id\":\"trash\"}", request),
                _ when path.StartsWith("/v1.0/me/messages/", StringComparison.Ordinal) =>
                    Json(HttpStatusCode.OK, $"{{\"id\":\"{id}\",\"parentFolderId\":\"drafts\"}}", request),
                _ => throw new InvalidOperationException(path)
            };
        }));
        var selected = new[] { Message("first"), Message("second"), Message("third") };
        var result = await GraphMailBatchDeleter.DeleteAsync(new GraphMailWriter(http, "owner"),
            "access", selected, _ => throw new Exception("Unexpected permanent-delete prompt"));
        Assert.Equal(["first", "second", "third"], moved);
        Assert.Equal(3, result.Completed.Count);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task Reports_partial_completion_without_retrying_completed_deletes()
    {
        var moved = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/move", StringComparison.Ordinal))
            {
                var id = path.Split('/')[4];
                moved.Add(id);
                return id == "second" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    { RequestMessage = request } : Json(HttpStatusCode.Created, "{\"id\":\"moved\"}", request);
            }
            var idFromPath = path.Split('/').Last();
            return path switch
            {
                "/v1.0/me" => Json(HttpStatusCode.OK, "{\"id\":\"owner\"}", request),
                "/v1.0/me/mailFolders/deleteditems" => Json(HttpStatusCode.OK, "{\"id\":\"trash\"}", request),
                _ when path.StartsWith("/v1.0/me/messages/", StringComparison.Ordinal) =>
                    Json(HttpStatusCode.OK, $"{{\"id\":\"{idFromPath}\",\"parentFolderId\":\"drafts\"}}", request),
                _ => throw new InvalidOperationException(path)
            };
        }));
        var result = await GraphMailBatchDeleter.DeleteAsync(new GraphMailWriter(http, "owner"),
            "access", [Message("first"), Message("second"), Message("third")],
            _ => Task.FromResult(true));
        Assert.Equal(["first", "second"], moved);
        Assert.Equal("first", Assert.Single(result.Completed).Id);
        Assert.NotNull(result.Failure);
        Assert.Equal(3, result.Requested);
    }

    private static GraphInboxMessage Message(string id) =>
        new(id, "Draft", "", "", null, null, false, true, "", IsDraft: true);

    private static HttpResponseMessage Json(HttpStatusCode code, string body, HttpRequestMessage request) =>
        new(code) { Content = new StringContent(body), RequestMessage = request };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
