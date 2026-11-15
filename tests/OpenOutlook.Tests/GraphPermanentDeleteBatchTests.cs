using System.Net;
using System.Text;
using System.Text.Json;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

/// <summary>Permanently deleting from Deleted Items is batched (20 per call, two calls per 20: a check, then the delete) and never touches a message that is no longer in Deleted Items.</summary>
public sealed class GraphPermanentDeleteBatchTests
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

    private static (GraphMailWriter Writer, List<(string Method, string Url)> Parts) Make(Func<string, (int Status, string Parent)> read)
    {
        var parts = new List<(string, string)>();
        var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1.0/me") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"owner"}""") };
            if (path == "/v1.0/me/mailFolders/deleteditems") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"TRASH"}""") };
            if (path != "/v1.0/$batch") throw new Exception("Unexpected Graph endpoint " + path);
            using var doc = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var responses = new List<string>();
            foreach (var part in doc.RootElement.GetProperty("requests").EnumerateArray())
            {
                var method = part.GetProperty("method").GetString()!;
                var url = part.GetProperty("url").GetString()!;
                parts.Add((method, url));
                var id = Uri.UnescapeDataString(url.Split("/messages/")[1].Split('/', '?')[0]);
                string body; int status;
                if (method == "GET")
                {
                    Assert.False(part.TryGetProperty("body", out _), "a GET carries no body");
                    var (s, parent) = read(id);
                    status = s;
                    body = s == 200 ? $$"""{"id":"{{id}}","parentFolderId":"{{parent}}"}""" : """{"error":{"code":"x"}}""";
                }
                else { status = 204; body = "null"; }
                responses.Add("{\"id\":\"" + part.GetProperty("id").GetString() + "\",\"status\":" + status + ",\"body\":" + body + "}");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"responses\":[" + string.Join(",", responses) + "]}", Encoding.UTF8, "application/json") };
        }));
        return (new GraphMailWriter(http, "owner"), parts);
    }

    private static List<string> Ids(int n) => Enumerable.Range(0, n).Select(i => "m" + i).ToList();

    [Fact]
    public async Task Forty_five_messages_take_three_checks_and_three_deletes()
    {
        var (writer, parts) = Make(_ => (200, "TRASH"));
        var progress = new List<int>();
        var result = await writer.DeletePermanentlyManyAsync("access", Ids(45), progress.Add);
        Assert.Equal(45, result.Done.Count);
        Assert.Empty(result.Failed);
        Assert.Equal(45, parts.Count(p => p.Method == "GET"));
        Assert.Equal(45, parts.Count(p => p.Method == "POST" && p.Url.EndsWith("/permanentDelete")));
        Assert.Equal(45, progress[^1]);
    }

    [Fact]
    public async Task A_message_moved_out_of_deleted_items_is_refused_and_never_deleted()
    {
        var (writer, parts) = Make(id => (200, id == "m3" ? "INBOX" : "TRASH"));
        var result = await writer.DeletePermanentlyManyAsync("access", Ids(5));
        Assert.Equal(4, result.Done.Count);
        Assert.Contains("m3", result.Failed.Keys);
        Assert.DoesNotContain(parts, p => p.Method == "POST" && p.Url.Contains("/m3/"));
    }

    [Fact]
    public async Task A_message_already_gone_counts_as_done_without_a_delete()
    {
        var (writer, parts) = Make(id => id == "m1" ? (404, "") : (200, "TRASH"));
        var result = await writer.DeletePermanentlyManyAsync("access", Ids(3));
        Assert.Equal(3, result.Done.Count);
        Assert.Empty(result.Failed);
        Assert.DoesNotContain(parts, p => p.Method == "POST" && p.Url.Contains("/m1/"));
    }
}
