using System.Net;
using System.Text.Json;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

public sealed class GraphMailWriterMoveCopyTests
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

    private static (GraphMailWriter Writer, List<(string Method, string Path, string Body)> Calls) Make()
    {
        var calls = new List<(string, string, string)>();
        var http = new HttpClient(new Handler(request =>
        {
            var body = request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var path = request.RequestUri!.AbsolutePath;
            if (path != "/v1.0/me") calls.Add((request.Method.Method, path, body));
            return path switch
            {
                "/v1.0/me" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"owner"}""") },
                var p when p.EndsWith("/move", StringComparison.Ordinal) || p.EndsWith("/copy", StringComparison.Ordinal) =>
                    new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"id":"new-id"}""") },
                var p when p.Contains("/mailFolders", StringComparison.Ordinal) =>
                    new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"id":"folder-9","displayName":"Receipts"}""") },
                _ => throw new Exception("Unexpected Graph endpoint " + path)
            };
        }));
        return (new GraphMailWriter(http, "owner"), calls);
    }

    [Fact]
    public async Task Move_and_copy_post_the_destination_folder_to_the_right_endpoint()
    {
        var (writer, calls) = Make();
        await writer.MoveAsync("access", "msg1", "folder-A");
        await writer.CopyAsync("access", "msg1", "folder-B");
        Assert.Equal(("POST", "/v1.0/me/messages/msg1/move"), (calls[0].Method, calls[0].Path));
        Assert.Equal("folder-A", JsonDocument.Parse(calls[0].Body).RootElement.GetProperty("destinationId").GetString());
        Assert.Equal(("POST", "/v1.0/me/messages/msg1/copy"), (calls[1].Method, calls[1].Path));
        Assert.Equal("folder-B", JsonDocument.Parse(calls[1].Body).RootElement.GetProperty("destinationId").GetString());
    }

    [Fact]
    public async Task Creates_a_folder_at_the_top_or_under_another_folder()
    {
        var (writer, calls) = Make();
        var top = await writer.CreateFolderAsync("access", null, "  Receipts ");
        Assert.Equal(("folder-9", "Receipts"), top);
        Assert.Equal("/v1.0/me/mailFolders", calls[0].Path);
        Assert.Equal("Receipts", JsonDocument.Parse(calls[0].Body).RootElement.GetProperty("displayName").GetString());
        await writer.CreateFolderAsync("access", "inbox", "Child");
        Assert.Equal("/v1.0/me/mailFolders/inbox/childFolders", calls[1].Path);
    }

    [Fact]
    public async Task Refuses_bad_folder_names_and_ids_before_any_request()
    {
        var (writer, calls) = Make();
        await Assert.ThrowsAsync<ArgumentException>(() => writer.CreateFolderAsync("access", null, "   "));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.CreateFolderAsync("access", null, new string('a', 300)));
        await Assert.ThrowsAnyAsync<Exception>(() => writer.CopyAsync("access", "msg1", ""));
        Assert.Empty(calls);
    }
}
