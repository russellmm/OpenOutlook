using System.Net;
using System.Text;
using OpenOutlook.JunkCleaner;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

public sealed class MicrosoftJunkCleanerRunnerTests
{
    private static string Messages(int spam, int clean)
    {
        var items = new List<string>();
        for (var i = 0; i < spam; i++)
            items.Add($$$"""{"id":"s{{{i}}}","subject":"win {{{i}}}","from":{"emailAddress":{"address":"promo@temu.example"}},"sender":{"emailAddress":{"address":"promo@temu.example"}},"toRecipients":[{"emailAddress":{"address":"me@x.org"}}],"importance":"normal"}""");
        for (var i = 0; i < clean; i++)
            items.Add($$$"""{"id":"c{{{i}}}","subject":"hello","from":{"emailAddress":{"address":"friend@example.org"}},"sender":{"emailAddress":{"address":"friend@example.org"}},"toRecipients":[{"emailAddress":{"address":"me@x.org"}}],"importance":"normal"}""");
        return "{\"value\":[" + string.Join(",", items) + "]}";
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(WithRequest(respond(request), request));
        private static HttpResponseMessage WithRequest(HttpResponseMessage r, HttpRequestMessage q) { r.RequestMessage = q; return r; }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (MicrosoftJunkCleanerRunner Runner, List<string> Moves) Make(int spam, int clean, string? failId = null, int cap = MicrosoftJunkCleanerRunner.MaxMovesPerRun)
    {
        var moves = new List<string>();
        var client = new HttpClient(new Handler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post && url.Contains("/move", StringComparison.Ordinal))
            {
                var id = url.Split("/messages/")[1].Split('/')[0];
                if (id == failId) return Json("{}", HttpStatusCode.InternalServerError);
                moves.Add(id);
                return Json("{\"id\":\"new-" + id + "\"}", HttpStatusCode.Created);
            }
            if (url.Contains("/junkemail?", StringComparison.Ordinal)) return Json("{\"id\":\"junk-id\",\"displayName\":\"Junk Email\"}");
            if (url.Contains("/messages?", StringComparison.Ordinal)) return Json(Messages(spam, clean));
            return Json("{\"id\":\"acct\"}");                                      // /me
        }));
        var reader = new GraphJunkMailReader(client, _ => ValueTask.FromResult("t"), "acct");
        var writer = new GraphMailWriter(client, "acct");
        return (new MicrosoftJunkCleanerRunner(reader, writer, _ => Task.FromResult("t"), "acct", cap), moves);
    }

    private static JunkCleanerAccountSettings Settings(bool enabled = true) =>
        new() { AccountId = "acct", Enabled = enabled, Keywords = ["temu"] };

    [Fact]
    public async Task Scan_matches_keywords_and_changes_nothing()
    {
        var (runner, moves) = Make(2, 3);
        var scan = await runner.ScanAsync(Settings());
        Assert.Equal(5, scan.Scanned);
        Assert.Equal(["s0", "s1"], scan.Matched.Select(m => m.MessageId));
        Assert.Empty(moves);
    }

    [Fact]
    public async Task Clean_moves_only_matches_to_deleted_items()
    {
        var (runner, moves) = Make(2, 3);
        var result = await runner.CleanAsync(Settings());
        Assert.Empty(result.Failures);
        Assert.Equal(["s0", "s1"], moves);
        Assert.Equal(2, result.Moved.Count);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task A_turned_off_account_matches_nothing()
    {
        var (runner, moves) = Make(2, 0);
        var result = await runner.CleanAsync(Settings(enabled: false));
        Assert.Empty(result.Matched);
        Assert.Empty(moves);
    }

    [Fact]
    public async Task One_failed_move_does_not_stop_the_rest()
    {
        var (runner, moves) = Make(3, 0, failId: "s1");
        var result = await runner.CleanAsync(Settings());
        Assert.Equal(["s0", "s2"], moves);
        Assert.Single(result.Failures);
    }

    [Fact]
    public async Task A_run_moves_at_most_the_cap()
    {
        var (runner, moves) = Make(5, 0, cap: 3);
        var result = await runner.CleanAsync(Settings());
        Assert.Equal(3, moves.Count);
        Assert.True(result.CapReached);
    }
}
