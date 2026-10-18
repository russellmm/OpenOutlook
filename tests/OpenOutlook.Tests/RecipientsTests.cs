using System.Net;
using System.Text;
using OpenOutlook.Desktop;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

/// <summary>To / Cc suggestions and Select Names (owner 2026-10-17): contacts plus recently used addresses.</summary>
public sealed class RecipientsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 18, 0, 0, TimeSpan.Zero);

    private static readonly RecipientEntry[] Contacts =
    [
        new("Petri, Bryan", "Bryan.Petri@pse.com", RecipientSource.Contact),
        new("Michael Bryan", "michael.bryan@intel.com", RecipientSource.Contact),
        new("'Jessica Marrash'", "jessica.marrash@gmail.com", RecipientSource.Contact),
    ];
    private static readonly RecipientEntry[] Recent =
    [
        new("", "JESSICA.MARRASH@gmail.com", RecipientSource.Recent, Now.AddHours(-1), 4),
        new("Imperial Valley Navy League", "ivnavyleague@yahoo.com", RecipientSource.Recent, Now.AddDays(-2), 1),
        new("I Drive Safely Fulfillment Support", "processing@idrivesafely.com", RecipientSource.Recent, Now.AddDays(-9), 1),
    ];

    [Fact]
    public void Initials_are_outlooks_first_and_last_word()
    {
        Assert.Equal("PB", Contacts[0].Initials);
        Assert.Equal("JM", Contacts[2].Initials);
        Assert.Equal("IV", new RecipientEntry("Imperial Valley", "x@y.z", RecipientSource.Recent).Initials);
        Assert.Equal("R", new RecipientEntry("", "rmarrash@hotmail.com", RecipientSource.Recent).Initials);
    }

    [Fact]
    public void Clicking_into_a_box_offers_the_most_recent_and_typing_narrows_by_word_starts()
    {
        var all = RecipientDirectory.Merge(Contacts, Recent);
        Assert.Equal(5, all.Count);                                                                  // Jessica once: the contact name, the recent use
        var jessica = all.Single(e => e.Address.Equals("jessica.marrash@gmail.com", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("'Jessica Marrash'", jessica.Name);
        Assert.Equal(Now.AddHours(-1), jessica.LastUsed);

        Assert.Equal(["jessica.marrash@gmail.com", "ivnavyleague@yahoo.com", "processing@idrivesafely.com"],
            RecipientDirectory.Suggest(all, "").Select(e => e.Address.ToLowerInvariant()));          // recent first, contacts never used are not listed yet
        Assert.Equal(["Michael Bryan", "Petri, Bryan"], RecipientDirectory.Suggest(all, "bry").Select(e => e.Name));
        Assert.Equal(["Petri, Bryan"], RecipientDirectory.Suggest(all, "pse").Select(e => e.Name));  // the address's domain
        Assert.Equal(["Petri, Bryan"], RecipientDirectory.Suggest(all, "bry", exclude: ["michael.bryan@intel.com"]).Select(e => e.Name));
        Assert.Empty(RecipientDirectory.Suggest(all, "zzz"));
    }

    [Fact]
    public void A_picked_address_replaces_the_part_being_typed_and_selected_names_are_appended_once()
    {
        Assert.Equal("bry", RecipientDirectory.CurrentToken("a@b.com; bry"));
        Assert.Equal("a@b.com; Bryan.Petri@pse.com; ", RecipientDirectory.Accept("a@b.com; bry", "Bryan.Petri@pse.com"));
        Assert.Equal("x@y.com; ", RecipientDirectory.Accept("", "x@y.com"));
        Assert.Equal("a@b.com; c@d.com; ", RecipientDirectory.Append("a@b.com", ["c@d.com", "A@B.com"]));
        Assert.Equal("", RecipientDirectory.Append("", []));
    }

    [Fact]
    public void Check_names_turns_typed_names_into_addresses_and_reports_what_it_cannot_resolve()
    {
        var all = RecipientDirectory.Merge(Contacts, Recent);
        var (text, unresolved) = RecipientDirectory.Resolve("bryan petri; Jessica Marrash; x@y.com; Shop <deals@shop.example>", all);
        Assert.Equal("Bryan.Petri@pse.com; jessica.marrash@gmail.com; x@y.com; deals@shop.example; ", text);
        Assert.Empty(unresolved);
        (text, unresolved) = RecipientDirectory.Resolve("bryan; nobody here", all);                     // two Bryans; no match
        Assert.Equal("bryan; nobody here; ", text);
        Assert.Equal(["\"bryan\" (2 matches)", "\"nobody here\" (no contact or recent recipient)"], unresolved);
        Assert.Equal(["Petri, Bryan"], RecipientDirectory.Suggest(all, "bryan p").Select(e => e.Name));    // several words, any order
    }

    [Fact]
    public void Recent_recipients_are_remembered_per_account_bounded_and_damage_reads_as_empty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oo-recip-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RecentRecipientsStore(dir);
            Assert.Empty(store.Load("me@hotmail.com"));
            store.Record("me@hotmail.com", [("Bryan", "bryan.petri@pse.com"), ("", "x@y.com")], Now);
            store.Record("me@hotmail.com", [("", "BRYAN.PETRI@pse.com")], Now.AddMinutes(5));
            var mine = store.Load("me@hotmail.com");
            var bryan = mine.Single(e => e.Address == "bryan.petri@pse.com");
            Assert.Equal(2, bryan.Uses);
            Assert.Equal(Now.AddMinutes(5), bryan.LastUsed);
            Assert.Equal("Bryan", bryan.Name);
            Assert.Empty(store.Load("other@gmail.com"));
            store.RecordMany("me@hotmail.com", Enumerable.Range(0, 700).Select(i => ("", $"p{i}@x.com", Now.AddMinutes(-i))));
            Assert.Equal(RecentRecipientsStore.MaxPerAccount, store.Load("me@hotmail.com").Count);
            File.WriteAllText(store.Path, "{ not json");
            Assert.Empty(store.Load("me@hotmail.com"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Sent_items_give_the_recipients_with_their_sent_times()
    {
        string? query = null;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1.0/me") return Json("""{"id":"acct"}""");
            query = Uri.UnescapeDataString(request.RequestUri.AbsoluteUri);
            return Json("""
                {"value":[
                  {"sentDateTime":"2026-10-17T17:40:08Z","toRecipients":[{"emailAddress":{"name":"Petri, Bryan","address":"Bryan.Petri@pse.com"}}],
                   "ccRecipients":[{"emailAddress":{"address":"jessica.marrash@gmail.com"}}],"bccRecipients":[]},
                  {"sentDateTime":"2026-10-10T09:00:00Z","toRecipients":[{"emailAddress":{"name":"bad","address":""}}]}
                ]}
                """);
        }));
        var sent = await new GraphMailWriter(http, "acct").ListSentRecipientsAsync("t", 200);
        Assert.Contains("/sentitems", query ?? "", StringComparison.Ordinal);
        Assert.Contains("$orderby=sentDateTime desc", query);
        Assert.Equal([("Petri, Bryan", "Bryan.Petri@pse.com"), ("", "jessica.marrash@gmail.com")], sent.Select(s => (s.Name, s.Address)));
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 17, 40, 8, TimeSpan.Zero), sent[0].Sent);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { var r = respond(request); r.RequestMessage = request; return Task.FromResult(r); }
    }
}
