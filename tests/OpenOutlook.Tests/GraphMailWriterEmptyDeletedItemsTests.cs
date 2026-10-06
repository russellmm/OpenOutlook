using System.Net;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

public sealed class GraphMailWriterEmptyDeletedItemsTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    [Fact]
    public async Task Deletes_every_message_in_pages_until_the_folder_is_empty()
    {
        var remaining = new Queue<string>(Enumerable.Range(1, 120).Select(i => "m" + i));
        var permanent = new List<string>();
        var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1.0/me") return Json("""{"id":"owner"}""");
            if (path == "/v1.0/me/mailFolders/deleteditems") return Json("""{"id":"trash-id","totalItemCount":120}""");
            if (path == "/v1.0/me/mailFolders/deleteditems/messages")
            {

                var page = remaining.Take(50).ToArray();
                return Json("{\"value\":[" + string.Join(",", page.Select(id => "{\"id\":\"" + id + "\"}")) + "]}");
            }
            if (path.EndsWith("/permanentDelete", StringComparison.Ordinal))
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                var id = path.Split('/')[^2];
                permanent.Add(id);
                Assert.Equal(id, remaining.Dequeue());
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            throw new Exception("Unexpected Graph endpoint " + path);
        }));
        var writer = new GraphMailWriter(http, "owner");
        var reported = 0;
        var deleted = await writer.EmptyDeletedItemsAsync("access", "trash-id", n => reported = n);
        Assert.Equal(120, deleted);
        Assert.Equal(120, reported);
        Assert.Equal(120, permanent.Distinct().Count());
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Refuses_a_folder_that_is_not_the_deleted_items_folder_without_deleting_anything()
    {
        var deletes = 0;
        var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1.0/me") return Json("""{"id":"owner"}""");
            if (path == "/v1.0/me/mailFolders/deleteditems") return Json("""{"id":"trash-id","totalItemCount":5}""");
            if (path.EndsWith("/permanentDelete", StringComparison.Ordinal)) deletes++;
            throw new Exception("Unexpected Graph endpoint " + path);
        }));
        var writer = new GraphMailWriter(http, "owner");
        await Assert.ThrowsAsync<GraphMailException>(() => writer.EmptyDeletedItemsAsync("access", "some-other-folder"));
        Assert.Equal(0, deletes);
    }

    [Fact]
    public async Task A_failure_part_way_stops_and_reports_what_was_already_deleted()
    {
        var calls = 0;
        var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1.0/me") return Json("""{"id":"owner"}""");
            if (path == "/v1.0/me/mailFolders/deleteditems") return Json("""{"id":"trash-id","totalItemCount":3}""");
            if (path == "/v1.0/me/mailFolders/deleteditems/messages") return Json("""{"value":[{"id":"a"},{"id":"b"},{"id":"c"}]}""");
            return ++calls < 3 ? new HttpResponseMessage(HttpStatusCode.NoContent) : new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        }));
        var writer = new GraphMailWriter(http, "owner");
        var reported = 0;
        await Assert.ThrowsAsync<GraphMailException>(() => writer.EmptyDeletedItemsAsync("access", "trash-id", n => reported = n));
        Assert.Equal(2, reported);
    }
}
