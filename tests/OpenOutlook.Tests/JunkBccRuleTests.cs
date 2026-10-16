using System.Net;
using System.Text;
using OpenOutlook.Desktop;
using OpenOutlook.JunkCleaner;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

/// <summary>
/// "Also remove junk with anyone in Bcc" (2026-10-16: phishing that named its targets in a visible BCC: header) and the bounded history of what the
/// Junk Cleaner removed.
/// </summary>
public sealed class JunkBccRuleTests
{
    private static readonly FromFields Fields = new(null, "x@spam.example", "x@spam.example", null, null, "x@spam.example");

    [Fact]
    public void Anyone_in_Bcc_matches_only_when_the_rule_is_on_and_the_field_is_known()
    {
        var on = new JunkRuleOptions(DeleteWithBcc: true);
        Assert.Equal(["Bcc recipients"], JunkMatcher.MatchReasons(Fields, [], 1, true, on, false, hasBcc: true));
        Assert.Empty(JunkMatcher.MatchReasons(Fields, [], 1, true, on, false, hasBcc: false));
        Assert.Empty(JunkMatcher.MatchReasons(Fields, [], 1, true, on, false, hasBcc: null));          // unknown is not a match
        Assert.Empty(JunkMatcher.MatchReasons(Fields, [], 1, true, new JunkRuleOptions(), false, hasBcc: true));
    }

    [Fact]
    public async Task Bcc_comes_from_graphs_list_or_a_bcc_header_with_an_address()
    {
        var calls = 0;
        string? select = null;
        using var client = new HttpClient(new FakeHandler(request =>
        {
            calls++;
            if (calls == 3) select = Uri.UnescapeDataString(request.RequestUri!.Query);
            return Json(calls switch
            {
                1 => """{"id":"account-1"}""",
                2 => """{"id":"junk","displayName":"Junk Email"}""",
                _ => """
                    {"value":[
                      {"id":"list","bccRecipients":[{"emailAddress":{"address":"someone@example.org"}}],"internetMessageHeaders":[]},
                      {"id":"header","bccRecipients":[],"internetMessageHeaders":[{"name":"BCC","value":"<rmarrash@hotmail.com>, <admin@pgvs.example>"}]},
                      {"id":"none","bccRecipients":[],"internetMessageHeaders":[{"name":"Bcc","value":"undisclosed recipients"}]},
                      {"id":"unknown"}
                    ]}
                    """
            });
        }));
        var reader = new GraphJunkMailReader(client, _ => ValueTask.FromResult("token"), "account-1");
        var messages = await reader.ListJunkMessagesAsync(pageSize: 10, maxMessages: 10);
        Assert.Contains("bccRecipients", select);
        Assert.Equal([true, true, false, null], messages.Select(m => m.HasBccRecipients).ToArray());
    }

    [Fact]
    public void The_rule_survives_saving_and_travels_with_an_exported_list()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oo-junkbcc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var account = new JunkCleanerAccountSettings { AccountId = "a1", Enabled = true, Rules = new JunkRuleOptions(DeleteWithBcc: true) };
            var store = new JunkCleanerSettingsStore(dir);
            store.Save(new JunkCleanerSettings { Accounts = [account] });
            Assert.True(store.Load().Accounts.Single().Rules.DeleteWithBcc);
            var export = Path.Combine(dir, "list.json");
            JunkCleanerSettingsStore.ExportPortable(export, account);
            Assert.True(JunkCleanerSettingsStore.PreviewLegacyConfig(export).Rules.DeleteWithBcc);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_removal_history_never_grows_without_limit()
    {
        var path = Path.Combine(Path.GetTempPath(), "oo-junklog-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var log = new JunkCleanerLog(path);
            var hits = Enumerable.Range(0, 100).Select(i => new JunkCleanerHit("m" + i, "A fairly long junk subject line number " + i, "sender" + i + "@spam.example", ["Bcc recipients"])).ToList();
            for (var run = 0; run < 60; run++)                                   // 6,000 entries written
                log.Append("me@example.org", new JunkCleanerRunResult(100, hits, hits, [], false), scheduled: true);
            Assert.True(new FileInfo(path).Length <= JunkCleanerLog.MaxBytes, $"{new FileInfo(path).Length} bytes");
            Assert.Equal(JunkCleanerLog.MaxEntries, File.ReadAllLines(path).Length);
            Assert.Contains("number 99", log.Recent(1).Single());                // the newest entries are the ones kept
        }
        finally { File.Delete(path); }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
