using System.Net;
using System.Text;
using System.Text.Json;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

/// <summary>Many-message actions go to Graph as $batch calls of at most 20 (one call per message made deleting hundreds of junk mails take minutes).</summary>
public sealed class GraphMailWriterBatchTests
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

    /// <summary>A fake Graph: <paramref name="answer"/> gives each batched part's status (by message id and how often that id was seen).</summary>
    private static (GraphMailWriter Writer, List<List<(string Method, string Url, string Body)>> Batches) Make(Func<string, int, int> answer)
    {
        var batches = new List<List<(string, string, string)>>();
        var seen = new Dictionary<string, int>();
        var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1.0/me") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"owner"}""") };
            if (path != "/v1.0/$batch" || request.Method != HttpMethod.Post) throw new Exception("Unexpected Graph endpoint " + path);
            using var doc = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var parts = new List<(string, string, string)>();
            var responses = new List<string>();
            foreach (var part in doc.RootElement.GetProperty("requests").EnumerateArray())
            {
                var url = part.GetProperty("url").GetString()!;
                parts.Add((part.GetProperty("method").GetString()!, url, part.GetProperty("body").GetRawText()));
                var id = Uri.UnescapeDataString(url.Split("/messages/")[1].Split('/')[0]);
                seen[id] = seen.GetValueOrDefault(id) + 1;
                var status = answer(id, seen[id]);
                var headers = status == 429 ? ""","headers":{"Retry-After":"1"}""" : "";
                var body = status is 200 or 201 ? $$"""{"id":"new-{{id}}"}""" : """{"error":{"code":"x"}}""";
                responses.Add("{\"id\":\"" + part.GetProperty("id").GetString() + "\",\"status\":" + status + headers + ",\"body\":" + body + "}");
            }
            batches.Add(parts);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"responses\":[" + string.Join(",", responses) + "]}", Encoding.UTF8, "application/json") };
        }));
        return (new GraphMailWriter(http, "owner"), batches);
    }

    private static List<string> Ids(int n) => Enumerable.Range(0, n).Select(i => "m" + i).ToList();

    [Fact]
    public async Task Moving_179_messages_takes_nine_calls_of_at_most_twenty()
    {
        var (writer, batches) = Make((_, _) => 201);
        var progress = new List<int>();
        var result = await writer.MoveManyAsync("access", Ids(179), "deleteditems", progress.Add);
        Assert.Equal(9, batches.Count);
        Assert.All(batches, b => Assert.True(b.Count <= GraphMailWriter.BatchSize));
        Assert.Equal(179, batches.Sum(b => b.Count));
        Assert.Equal(179, result.Done.Count);
        Assert.Empty(result.Failed);
        Assert.Equal("new-m7", result.Done["m7"]);                                  // a move gives the message a new id
        Assert.Equal(("POST", "/me/messages/m0/move"), (batches[0][0].Method, batches[0][0].Url));
        Assert.Equal("deleteditems", JsonDocument.Parse(batches[0][0].Body).RootElement.GetProperty("destinationId").GetString());
        Assert.Equal(179, progress[^1]);
    }

    [Fact]
    public async Task One_refused_message_does_not_stop_the_rest()
    {
        var (writer, _) = Make((id, _) => id == "m3" ? 404 : 201);
        var result = await writer.MoveManyAsync("access", Ids(25), "deleteditems");
        Assert.Equal(24, result.Done.Count);
        Assert.Equal(HttpStatusCode.NotFound, Assert.Single(result.Failed).Value.StatusCode);
    }

    [Fact]
    public async Task Throttled_messages_are_sent_again_after_the_delay_microsoft_asks_for()
    {
        var (writer, batches) = Make((id, attempt) => id is "m1" or "m4" && attempt == 1 ? 429 : 201);
        var result = await writer.MoveManyAsync("access", Ids(6), "archive");
        Assert.Equal(6, result.Done.Count);
        Assert.Equal(2, batches.Count);
        Assert.Equal(["/me/messages/m1/move", "/me/messages/m4/move"], batches[1].Select(p => p.Url));
    }

    [Fact]
    public async Task A_refused_sign_in_stops_further_calls()
    {
        var (writer, batches) = Make((_, _) => 401);
        var result = await writer.MoveManyAsync("access", Ids(45), "deleteditems");
        Assert.Single(batches);
        Assert.Equal(45, result.Failed.Count);
        Assert.All(result.Failed.Values, e => Assert.Equal(HttpStatusCode.Unauthorized, e.StatusCode));
    }

    [Fact]
    public async Task Read_and_flag_changes_are_batched_patches()
    {
        var (writer, batches) = Make((_, _) => 200);
        var read = await writer.SetReadManyAsync("access", Ids(3), true);
        var flag = await writer.SetFlagManyAsync("access", Ids(2), false);
        Assert.Equal(3, read.Done.Count);
        Assert.Equal("m0", read.Done["m0"]);                                        // a change keeps the id
        Assert.Equal(("PATCH", "/me/messages/m0"), (batches[0][0].Method, batches[0][0].Url));
        Assert.True(JsonDocument.Parse(batches[0][0].Body).RootElement.GetProperty("isRead").GetBoolean());
        Assert.Equal("notFlagged", JsonDocument.Parse(batches[1][0].Body).RootElement.GetProperty("flag").GetProperty("flagStatus").GetString());
        Assert.Equal(2, flag.Done.Count);
    }
}
