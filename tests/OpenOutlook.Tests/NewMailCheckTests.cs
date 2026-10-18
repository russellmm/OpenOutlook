using System.Net;
using System.Text;
using OpenOutlook.Desktop;
using OpenOutlook.Providers.Google;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

/// <summary>The scheduled new-mail check (Options > Advanced > Send and receive): what it asks the servers and when it leads to a sync.</summary>
public sealed class NewMailCheckTests
{
    [Fact]
    public void Tracker_reports_first_same_and_changed_per_account_and_folder()
    {
        var tracker = new NewMailTracker();
        Assert.Equal(NewMailVerdict.First, tracker.Observe("a", "inbox", "3/1/m1"));
        Assert.Equal(NewMailVerdict.Same, tracker.Observe("a", "inbox", "3/1/m1"));
        Assert.Equal(NewMailVerdict.First, tracker.Observe("b", "inbox", "3/1/m1"));          // another account is kept apart
        Assert.Equal(NewMailVerdict.First, tracker.Observe("a", "sent", "3/1/m1"));           // and another folder
        Assert.Equal(NewMailVerdict.Changed, tracker.Observe("a", "inbox", "4/2/m9"));
        Assert.Equal(NewMailVerdict.Same, tracker.Observe("a", "inbox", "4/2/m9"));
    }

    [Fact]
    public void A_sync_that_did_not_complete_makes_the_next_check_count_as_changed()
    {
        var tracker = new NewMailTracker();
        tracker.Observe("a", "inbox", "4/2/m9");
        tracker.Observe("a", "history", "100");
        tracker.Observe("ab", "inbox", "1/0/x");
        tracker.Invalidate("a");
        Assert.Equal(NewMailVerdict.Changed, tracker.Observe("a", "inbox", "4/2/m9"));
        Assert.Equal(NewMailVerdict.Changed, tracker.Observe("a", "history", "100"));
        Assert.Equal(NewMailVerdict.Same, tracker.Observe("ab", "inbox", "1/0/x"));         // only that account ("a|", not "ab|")
    }

    [Theory]
    [InlineData(NewMailVerdict.Same, null, true, false, false)]
    [InlineData(NewMailVerdict.Changed, null, false, false, true)]                         // new mail in the Inbox
    [InlineData(NewMailVerdict.Same, NewMailVerdict.Changed, true, false, true)]          // the open folder changed
    [InlineData(NewMailVerdict.Same, NewMailVerdict.First, true, false, false)]           // a folder just opened is only a baseline
    [InlineData(NewMailVerdict.First, null, false, false, false)]                         // before the start-up sync: that sync brings everything
    [InlineData(NewMailVerdict.First, null, true, false, true)]                           // after it: mail may have come in between
    [InlineData(NewMailVerdict.Same, null, true, true, true)]                             // Send/Receive All always synchronises
    public void Sync_only_when_the_inbox_or_the_open_folder_changed(NewMailVerdict inbox, NewMailVerdict? open, bool synced, bool manual, bool expected) =>
        Assert.Equal(expected, NewMailTracker.ShouldSync(inbox, open, synced, manual));

    [Fact]
    public void Interval_is_one_to_sixty_minutes_and_on_by_default()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), NewMailTracker.Interval(0));
        Assert.Equal(TimeSpan.FromMinutes(5), NewMailTracker.Interval(5));
        Assert.Equal(TimeSpan.FromMinutes(60), NewMailTracker.Interval(900));
        var defaults = new OptionsSettings();
        Assert.True(defaults.NewMailCheckEnabled);
        Assert.Equal(1, defaults.NewMailCheckMinutes);
    }

    [Fact]
    public void Options_store_keeps_the_check_and_clamps_the_minutes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oo-newmail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OptionsSettingsStore(dir);
            store.Save(new OptionsSettings { NewMailCheckEnabled = false, NewMailCheckMinutes = 500 });
            var loaded = store.Load();
            Assert.False(loaded.NewMailCheckEnabled);
            Assert.Equal(60, loaded.NewMailCheckMinutes);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Graph_probe_asks_two_small_questions_per_folder_and_verifies_the_account_once_per_token()
    {
        var urls = new List<string>();
        var newest = "m1";
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            urls.Add(Uri.UnescapeDataString(path));
            return Json(request.RequestUri.AbsolutePath switch
            {
                "/v1.0/me" => """{"id":"acct"}""",
                "/v1.0/me/mailFolders/inbox" => """{"id":"AAInbox","totalItemCount":12,"unreadItemCount":1}""",
                "/v1.0/me/mailFolders/inbox/messages" => $$"""{"value":[{"id":"{{newest}}","receivedDateTime":"2026-10-18T09:15:00Z"}]}""",
                "/v1.0/me/mailFolders/AA%2Fsent/messages" => """{"value":[]}""",
                "/v1.0/me/mailFolders/AA%2Fsent" => """{"id":"AA/sent","totalItemCount":0,"unreadItemCount":0}""",
                _ => throw new InvalidOperationException(path)
            });
        }));
        var probe = new GraphNewMailProbe(http, "acct");
        var first = await probe.ProbeAsync("token-1", ["inbox", "AA/sent"]);
        Assert.Equal(new GraphFolderProbe("inbox", 12, 1, "m1", new DateTimeOffset(2026, 10, 18, 9, 15, 0, TimeSpan.Zero)), first[0]);
        Assert.Equal(new GraphFolderProbe("AA/sent", 0, 0, null, null), first[1]);            // an empty folder has no newest message
        Assert.Equal(5, urls.Count);
        Assert.Contains("$top=1", urls[2]);
        Assert.Contains("$orderby=receivedDateTime desc", urls[2]);

        urls.Clear();
        newest = "m2";
        var second = await probe.ProbeAsync("token-1", ["inbox"]);
        Assert.Equal(2, urls.Count);                                                           // same token: /me is not asked again
        Assert.NotEqual(first[0].Fingerprint, second[0].Fingerprint);                          // same counts, but a new newest message
        await probe.ProbeAsync("token-2", ["inbox"]);
        Assert.Equal("/v1.0/me?$select=id", urls[2]);                                          // a new token is verified again
    }

    [Fact]
    public async Task Graph_probe_refuses_another_account_and_bad_input()
    {
        using var http = new HttpClient(new Handler(request => Json("""{"id":"someone-else"}""")));
        var probe = new GraphNewMailProbe(http, "acct");
        await Assert.ThrowsAsync<GraphMailException>(() => probe.ProbeAsync("token", ["inbox"]));
        await Assert.ThrowsAsync<GraphMailException>(() => probe.ProbeAsync("token", []));
        await Assert.ThrowsAsync<GraphMailException>(() => probe.ProbeAsync("bad\ntoken", ["inbox"]));
    }

    [Fact]
    public async Task Graph_probe_refuses_a_redirect_and_reports_http_errors()
    {
        using var redirect = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.org/") } }));
        await Assert.ThrowsAsync<GraphMailException>(() => new GraphNewMailProbe(redirect, "acct").ProbeAsync("token", ["inbox"]));
        using var unauthorized = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/v1.0/me"
            ? Json("""{"id":"acct"}""") : new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var error = await Assert.ThrowsAsync<GraphMailException>(() => new GraphNewMailProbe(unauthorized, "acct").ProbeAsync("token", ["inbox"]));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task Gmail_history_id_and_label_counts_are_one_request_each()
    {
        var urls = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            urls.Add(request.RequestUri!.AbsolutePath);
            return Json(request.RequestUri.AbsolutePath switch
            {
                "/gmail/v1/users/me/profile" => """{"emailAddress":"me@example.org","historyId":"987654"}""",
                "/gmail/v1/users/me/labels/INBOX" => """{"id":"INBOX","messagesTotal":120,"messagesUnread":7}""",
                "/gmail/v1/users/me/labels/Label_9" => """{"id":"Label_9"}""",
                _ => "{}"
            });
        }));
        var box = new GmailMailbox(http, _ => ValueTask.FromResult("tok"), "me@example.org");
        Assert.Equal("987654", await box.GetHistoryIdAsync());
        Assert.Equal((120, 7), await box.GetLabelCountsAsync("INBOX"));
        Assert.Equal(["/gmail/v1/users/me/profile", "/gmail/v1/users/me/profile", "/gmail/v1/users/me/labels/INBOX"], urls);   // verification once, then one each
        await Assert.ThrowsAsync<GmailReadException>(() => box.GetLabelCountsAsync("Label_9"));            // counts missing
        await Assert.ThrowsAsync<ArgumentException>(() => box.GetLabelCountsAsync("bad\nlabel"));
    }

    [Fact]
    public async Task Gmail_history_id_must_be_digits()
    {
        using var http = new HttpClient(new Handler(_ => Json("""{"emailAddress":"me@example.org","historyId":"12a"}""")));
        var box = new GmailMailbox(http, _ => ValueTask.FromResult("tok"), "me@example.org");
        await Assert.ThrowsAsync<GmailReadException>(() => box.GetHistoryIdAsync());
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

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
