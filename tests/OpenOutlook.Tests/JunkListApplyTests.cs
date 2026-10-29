using System.Net;
using System.Text;
using System.Text.Json;
using OpenOutlook.JunkCleaner;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

/// <summary>OpenOutlook applying the Safe / Blocked Senders lists, and the Graph calls behind the Junk menu (docs/junk-lists-plan.md).</summary>
public sealed class JunkListApplyTests
{
    private static string Message(string id, string from, string to = "me@hotmail.test") =>
        $$$"""{"id":"{{{id}}}","subject":"s {{{id}}}","from":{"emailAddress":{"address":"{{{from}}}"}},"sender":{"emailAddress":{"address":"{{{from}}}"}},"toRecipients":[{"emailAddress":{"address":"{{{to}}}"}}],"importance":"normal"}""";

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var r = respond(request); r.RequestMessage = request; return Task.FromResult(r);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (MicrosoftJunkCleanerRunner Runner, List<(string Id, string To)> Moves) Make(string[] junk, string[] inbox)
    {
        var moves = new List<(string, string)>();
        var client = new HttpClient(new Handler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post && url.EndsWith("/move", StringComparison.Ordinal))
            {
                var id = url.Split("/messages/")[1].Split('/')[0];
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
                moves.Add((id, body.RootElement.GetProperty("destinationId").GetString()!));
                return Json("{\"id\":\"new-" + id + "\"}", HttpStatusCode.Created);
            }
            if (url.Contains("/mailFolders/junkemail?", StringComparison.Ordinal)) return Json("{\"id\":\"junk-id\",\"displayName\":\"Junk Email\"}");
            if (url.Contains("/mailFolders/inbox?", StringComparison.Ordinal)) return Json("{\"id\":\"inbox-id\",\"displayName\":\"Inbox\"}");
            if (url.Contains("/mailFolders/junk-id/messages", StringComparison.Ordinal)) return Json("{\"value\":[" + string.Join(",", junk) + "]}");
            if (url.Contains("/mailFolders/inbox-id/messages", StringComparison.Ordinal)) return Json("{\"value\":[" + string.Join(",", inbox) + "]}");
            return Json("{\"id\":\"acct\"}");
        }));
        var reader = new GraphJunkMailReader(client, _ => ValueTask.FromResult("t"), "acct");
        return (new MicrosoftJunkCleanerRunner(reader, new GraphMailWriter(client, "acct"), _ => Task.FromResult("t"), "acct"), moves);
    }

    private static readonly JunkLists Lists = new JunkLists()
        .With(JunkListKind.SafeSenders, ["@amazon.com", "friend@example.org"])
        .With(JunkListKind.SafeRecipients, ["list@groups.example"])
        .With(JunkListKind.BlockedSenders, ["@spam.example"]);

    [Fact]
    public async Task Safe_mail_leaves_junk_email_and_blocked_mail_leaves_the_inbox_nothing_else_moves()
    {
        var (runner, moves) = Make(
            junk: [Message("j1", "orders@amazon.com"), Message("j2", "x@other.example"), Message("j3", "y@nowhere.example", to: "list@groups.example")],
            inbox: [Message("i1", "deal@spam.example"), Message("i2", "friend@example.org"), Message("i3", "a@b.example")]);
        var result = await runner.ApplyListsAsync(Lists);
        Assert.Equal([("j1", "inbox"), ("j3", "inbox"), ("i1", "junkemail")], moves);
        Assert.Equal(2, result.Rescued.Count);
        Assert.Single(result.Blocked);
        Assert.Empty(result.Failures);
        Assert.Equal("Safe sender", result.Rescued[0].Reasons[0]);
    }

    [Fact]
    public async Task The_junk_cleaner_never_offers_mail_from_a_safe_sender_and_empty_lists_do_nothing()
    {
        var (runner, moves) = Make(junk: [Message("j1", "promo@amazon.com"), Message("j2", "promo@temu.example")], inbox: []);
        var settings = new JunkCleanerAccountSettings { AccountId = "acct", Enabled = true, Keywords = ["promo"] };
        Assert.Equal(["j1", "j2"], (await runner.ScanAsync(settings)).Matched.Select(m => m.MessageId));
        Assert.Equal(["j2"], (await runner.ScanAsync(settings, default, Lists)).Matched.Select(m => m.MessageId));
        var none = await runner.ApplyListsAsync(JunkLists.Empty);
        Assert.Empty(none.Rescued);
        Assert.Empty(moves);
    }

    [Fact]
    public async Task The_inbox_spam_check_marks_rule_and_blocked_matches_skips_safe_mail_and_moves_nothing()
    {
        var (runner, moves) = Make(junk: [],
            inbox: [Message("i1", "promo@temu.example"), Message("i2", "deal@spam.example"), Message("i3", "promo@amazon.com"), Message("i4", "a@b.example"),
                    Message("i5", "promo@spam.example")]);
        // the Junk Cleaner's own opt-in is off: the Inbox check has its own switch
        var settings = new JunkCleanerAccountSettings { AccountId = "acct", Enabled = false, Keywords = ["promo"] };
        var result = await runner.ScanInboxAsync(settings, 200, default, Lists);
        Assert.Equal(["i1", "i5", "i2"], result.Matched.Select(m => m.MessageId));
        Assert.Equal(["From keyword"], result.Matched[0].Reasons);
        Assert.Equal(["Blocked sender", "From keyword"], result.Matched[1].Reasons);
        Assert.Equal(["Blocked sender"], result.Matched[2].Reasons);
        Assert.Equal("deal@spam.example", result.Matched[2].Sender);
        Assert.Equal(["i1", "i2", "i3", "i4", "i5"], result.ScannedIds);           // the safe one (i3) was looked at too
        Assert.Empty(moves);
    }

    [Fact]
    public void The_inbox_spam_check_remembers_unticked_messages_and_keeps_its_own_rules()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oo-spam-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new InboxSpamCheckStore(dir);
            var fresh = store.For("acct");
            Assert.True(fresh.Enabled);
            Assert.True(fresh.Rules.DeleteUnauthenticated);
            Assert.True(fresh.Rules.DeleteMissingTo);                               // mass mail from hijacked accounts passes every sender check
            Assert.False(fresh.Rules.DeleteFlagged);                                // Junk-folder rules such as Flagged stay off for the Inbox
            var account = InboxSpamCheckStore.WithNotSpam(fresh, "m1", true);
            account = InboxSpamCheckStore.WithNotSpam(account, "m2", true);
            account = InboxSpamCheckStore.WithNotSpam(account, "m1", false);         // ticked again
            store.Save(account with { Rules = new JunkRuleOptions(DeleteEmojiSubject: true) });
            store.Save(new InboxSpamCheckAccount { AccountId = "other", Enabled = false });
            var back = new InboxSpamCheckStore(dir).For("acct");
            Assert.Equal(["m2"], back.NotSpam);
            Assert.True(back.Rules.DeleteEmojiSubject);
            Assert.False(back.Rules.DeleteUnauthenticated);
            Assert.False(store.For("other").Enabled);
            store.Save(back with { NotSpam = Enumerable.Range(0, InboxSpamCheckStore.MaxNotSpam + 5).Select(i => "x" + i).ToArray() });
            var trimmed = store.For("acct").NotSpam;
            Assert.Equal(InboxSpamCheckStore.MaxNotSpam, trimmed.Count);
            Assert.Equal("x5", trimmed[0]);                                         // the oldest are dropped
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task The_junk_menu_reads_sender_and_recipient_addresses_and_reports_to_microsofts_beta_endpoint()
    {
        var calls = new List<(string Url, string? Body)>();
        var client = new HttpClient(new Handler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            calls.Add((url, request.Content?.ReadAsStringAsync().Result));
            if (url.EndsWith("/me?$select=id", StringComparison.Ordinal)) return Json("{\"id\":\"acct\"}");
            if (url.Contains("m2/reportMessage", StringComparison.Ordinal)) return Json("{}");
            if (url.Contains("reportMessage", StringComparison.Ordinal)) return Json("{\"id\":\"m1-moved\"}");
            return Json("""{"id":"m1","from":{"emailAddress":{"name":"Shop","address":"Deals@Shop.example"}},"toRecipients":[{"emailAddress":{"address":"list@groups.example"}}],"ccRecipients":[{"emailAddress":{"address":"me@hotmail.test"}}]}""");
        }));
        var writer = new GraphMailWriter(client, "acct");
        var (from, recipients) = await writer.GetAddressesAsync("t", "m1");
        Assert.Equal("Deals@Shop.example", from);
        Assert.Equal(["list@groups.example", "me@hotmail.test"], recipients);
        Assert.Equal("m1-moved", await writer.ReportMessageAsync("t", "m1", "notJunk", move: true));
        var report = calls.First(c => c.Url.Contains("reportMessage"));
        Assert.StartsWith("https://graph.microsoft.com/beta/me/messages/m1/reportMessage", report.Url);
        using var body = JsonDocument.Parse(report.Body!);
        Assert.True(body.RootElement.GetProperty("IsMessageMoveRequested").GetBoolean());
        Assert.Equal("notJunk", body.RootElement.GetProperty("ReportAction").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => writer.ReportMessageAsync("t", "m1", "spam", move: false));
        Assert.Equal("m2", await writer.ReportMessageAsync("t", "m2", "junk", move: false));                 // Hotmail's reply has no id
    }
}
