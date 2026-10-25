using OpenOutlook.JunkCleaner;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

/// <summary>
/// The twelve header and subject rules from the owner's Junk folder survey (2026-10-25: 63 messages the first six rules missed). Header values are
/// shaped like the ones Outlook.com wrote on that mail.
/// </summary>
public sealed class JunkSignalsTests
{
    private const string Own = "rmarrash@hotmail.com";
    private const string Unsigned = "mx.microsoft.com 1; spf=none (sender IP is 20.234.82.250) smtp.mailfrom=UqWKN2DZw1.com; dkim=none (message not signed) header.d=none;dmarc=none action=none header.from=-----mail.zkreEmux2K.com;";
    private const string Signed = "mx.microsoft.com 1; spf=pass (sender IP is 12.174.236.223) smtp.mailfrom=r1806783.example.com; dkim=pass (signature was verified) header.d=example.com;dmarc=pass action=none header.from=example.com;compauth=pass reason=100";

    private static List<KeyValuePair<string, string>> Headers(params (string Name, string Value)[] h) => h.Select(x => new KeyValuePair<string, string>(x.Name, x.Value)).ToList();
    private static JunkMessageFacts Facts(string? subject = "Hello", string? from = "news@example.com", string? fromHeader = null, List<KeyValuePair<string, string>>? headers = null, int? recipients = 1) =>
        new(subject, from, fromHeader, headers ?? [], recipients, Own);

    [Fact]
    public void Unverified_senders_match_by_microsofts_verdict_and_unknown_never_does()
    {
        Assert.True(JunkSignals.IsUnauthenticated(Headers(("Authentication-Results", Unsigned))));
        Assert.False(JunkSignals.IsUnauthenticated(Headers(("Authentication-Results", Signed))));
        Assert.True(JunkSignals.IsUnauthenticated(Headers(("Authentication-Results", "spf=pass smtp.mailfrom=x; dkim=pass header.d=x;dmarc=fail action=none header.from=navy.example;compauth=fail reason=001"))));
        Assert.False(JunkSignals.IsUnauthenticated(Headers(("Authentication-Results", "spf=pass; dkim=pass; dmarc=bestguesspass;compauth=pass reason=109"))));
        Assert.True(JunkSignals.IsUnauthenticated(Headers(("Authentication-Results", "spf=softfail; dkim=none"))));            // no compauth: neither passed
        Assert.False(JunkSignals.IsUnauthenticated(Headers(("Authentication-Results", "spf=none; dkim=pass"))));
        Assert.False(JunkSignals.IsUnauthenticated([]));                                                                         // no header: unknown
    }

    [Theory]
    [InlineData("info@-----mail.zkreEmux2K.com", true)]
    [InlineData("firstpremier@firstpremier.us-SnlvK6gTno253K2br0huidc1vwo0", true)]
    [InlineData("no-reply@cuJIHka.🥹🫶", true)]
    [InlineData("a@b..com", true)]
    [InlineData("nobody", true)]
    [InlineData("info@mail.zkreemux2k.com", false)]
    [InlineData("seapowermail@navyleague.org", false)]
    [InlineData("x@bücher.xn--p1ai", false)]
    [InlineData("hello@em.refinance-rates.co.uk", false)]
    public void Impossible_sender_domains_are_fake(string address, bool fake) => Assert.Equal(fake, JunkSignals.IsFakeAddress(address));

    [Fact]
    public void A_company_name_matches_only_when_the_address_is_not_the_companys()
    {
        var brands = JunkSignals.DefaultBrands;
        Assert.Equal("McAfee", JunkSignals.ImpersonatedBrand("\"McAfee Account Services\" <info@-----mail.zkreEmux2K.com>", "info@-----mail.zkreEmux2K.com", brands));
        Assert.Equal("Sam's Club", JunkSignals.ImpersonatedBrand("\"Sam’s Club Plus\" <info@x.example>", "info@x.example", brands));             // curly apostrophe
        Assert.Null(JunkSignals.ImpersonatedBrand("\"McAfee\" <billing@mcafee.com>", "billing@mcafee.com", brands));                               // the real company
        Assert.Null(JunkSignals.ImpersonatedBrand("\"Sam's Club\" <news@email.samsclub.com>", "news@email.samsclub.com", brands));
        Assert.Null(JunkSignals.ImpersonatedBrand("\"Targeted Offers\" <a@x.example>", "a@x.example", brands));                                    // whole words only
        Assert.Null(JunkSignals.ImpersonatedBrand("info@x.example", "info@x.example", brands));                                                    // no display name
        Assert.Equal("Acme", JunkSignals.ImpersonatedBrand("\"Acme Support\" <a@x.example>", "a@x.example", ["Acme"]));                            // the owner's own list
    }

    [Fact]
    public void Subject_rules_match_the_surveyed_junk_and_leave_ordinary_subjects_alone()
    {
        Assert.True(JunkSignals.HasHiddenCharacters("‮gnidnatstuo llitS sI etadpU tnemyaP ruoY"));
        Assert.True(JunkSignals.HasHiddenCharacters("Pay​ment due"));
        Assert.False(JunkSignals.HasHiddenCharacters("Family 👨‍👩‍👧 photos 🏴󠁧󠁢󠁳󠁣󠁴󠁿"));                                                        // emoji joiners and tag characters

        Assert.True(JunkSignals.HasOtherAlphabet("周末窝在沙发上看电影"));
        Assert.True(JunkSignals.HasOtherAlphabet("초대장: 💳 Auto-payment will be processed"));
        Assert.True(JunkSignals.HasOtherAlphabet("Привет, вы выиграли"));
        Assert.False(JunkSignals.HasOtherAlphabet("Schluss mit nächtlichen Knieschmerzen"));                                          // Latin with accents
        Assert.False(JunkSignals.HasOtherAlphabet("Ω offer"));                                                                         // one stray letter

        Assert.True(JunkSignals.StartsWithEmoji("🎰The casino for you🍀"));
        Assert.True(JunkSignals.StartsWithEmoji("⚡ Quick and easy loans"));
        Assert.True(JunkSignals.StartsWithEmoji("  ⏰ Time to renew"));
        Assert.False(JunkSignals.StartsWithEmoji("Up to 30% off flights to South America 💸"));                                       // emoji at the end
        Assert.False(JunkSignals.StartsWithEmoji("\"Quoted\" subject"));

        Assert.True(JunkSignals.HasRandomCode("K-#0BL9C5G1BC2_LE4PIUUBZ7-DT90M6CWKSpecial Information"));
        Assert.False(JunkSignals.HasRandomCode("Your order 112-4567890-1234567 has shipped"));                                       // digits only
        Assert.False(JunkSignals.HasRandomCode("Tracking 1Z999AA10123456784"));                                                       // three letters
        Assert.False(JunkSignals.HasRandomCode("Internationalization roundup"));                                                      // a long word

        Assert.True(JunkSignals.UsesUsername("rmarrash Start Your Window Upgrade", null, Own));
        Assert.True(JunkSignals.UsesUsername("Hi", "\"RMarrash friend\" <a@x.example>", Own));
        Assert.False(JunkSignals.UsesUsername("Hi there", "\"Bob\" <a@x.example>", Own));
        Assert.False(JunkSignals.UsesUsername("bob is here", null, "bob@x.example"));                                                // under 4 letters: too common

        Assert.True(JunkSignals.IsInvitation("Invitation: 💳 Auto-payment will be processed within 24 hours", []));
        Assert.True(JunkSignals.IsInvitation("초대장: 💳 Auto-payment", []));
        Assert.True(JunkSignals.IsInvitation("Lunch", Headers(("Content-Type", "text/calendar; method=REQUEST"))));
        Assert.False(JunkSignals.IsInvitation("Your invitation to save 20%", Headers(("Content-Type", "multipart/mixed"))));
    }

    [Fact]
    public void Bulk_rating_free_mail_and_crowds()
    {
        Assert.Equal(9, JunkSignals.BulkLevel(Headers(("X-Microsoft-Antispam", "BCL:9;ARA:1444111002|3600799018"))));
        Assert.Equal(0, JunkSignals.BulkLevel(Headers(("X-Microsoft-Antispam", "BCL:0;ARA:1"))));
        Assert.Null(JunkSignals.BulkLevel([]));
        Assert.True(JunkSignals.IsFreeMail("emmadhkyy76@gmail.com"));
        Assert.True(JunkSignals.IsFreeMail("x@yahoo.co.uk"));
        Assert.False(JunkSignals.IsFreeMail("info@m.onefourhouse.com"));

        var all = new JunkRuleOptions(DeleteCrowd: true, DeleteBulk: true);
        Assert.Equal(["Sent to 188 people"], JunkSignals.Reasons(Facts(recipients: 188), all));
        Assert.Empty(JunkSignals.Reasons(Facts(recipients: 9), all));                                                               // default crowd: 10
        Assert.Empty(JunkSignals.Reasons(Facts(recipients: null), all));                                                            // unknown
        Assert.Equal(["Sent to 3 people"], JunkSignals.Reasons(Facts(recipients: 3), all with { CrowdSize = 3 }));
        var bcl5 = Headers(("X-Microsoft-Antispam", "BCL:5;"));
        Assert.Equal(["Bulk rating 5"], JunkSignals.Reasons(Facts(headers: bcl5), all));                                            // default level: 5
        Assert.Empty(JunkSignals.Reasons(Facts(headers: bcl5), all with { BulkLevel = 6 }));
    }

    [Fact]
    public void Rules_that_are_off_never_match()
    {
        var spam = Facts("🎰 K-#0BL9C5G1BC2_LE4PIUUBZ7 rmarrash 周末窝在沙发", "x@gmail.com", "\"McAfee\" <x@gmail.com>",
            Headers(("Authentication-Results", Unsigned), ("X-Microsoft-Antispam", "BCL:9;"), ("Content-Type", "text/calendar")), 500);
        Assert.Empty(JunkSignals.Reasons(spam, new JunkRuleOptions()));
        Assert.Equal(10, JunkSignals.Reasons(spam, AllOn).Count);                                                                   // all but the fake address and hidden characters
    }

    private static readonly JunkRuleOptions AllOn = new(DeleteUnauthenticated: true, DeleteFakeSenderAddress: true, DeleteImpersonation: true,
        DeleteHiddenText: true, DeleteInvitations: true, DeleteCrowd: true, DeleteForeignScript: true, DeleteUsernameInSubject: true,
        DeleteEmojiSubject: true, DeleteBulk: true, DeleteFreeMail: true, DeleteRandomCode: true);

    [Fact]
    public void The_preview_applies_the_new_rules_to_graph_messages_with_the_accounts_company_list()
    {
        var messages = new[]
        {
            new GraphJunkMessage("mcafee", "Action Needed: Resolve payment issue", "info@-----mail.zkreEmux2K.com", null, [Own], [], "normal",
                [new GraphInternetHeader("Authentication-Results", Unsigned), new GraphInternetHeader("From", "\"McAfee Account Services\" <info@-----mail.zkreEmux2K.com>")], HasToRecipients: true),
            new GraphJunkMessage("museum", "Seaport Museum At Home Issue 148", "info@seany.org", null, [Own], [], "normal",
                [new GraphInternetHeader("Authentication-Results", Signed), new GraphInternetHeader("From", "\"Seaport Museum\" <info@seany.org>")], HasToRecipients: true),
            new GraphJunkMessage("acme", "Hello", "info@x.example", null, [Own], [], "normal",
                [new GraphInternetHeader("Authentication-Results", Signed), new GraphInternetHeader("From", "\"Acme Billing\" <info@x.example>")], HasToRecipients: true),
        };
        var settings = new JunkCleanerAccountSettings { AccountId = "acct", Enabled = true, Rules = AllOn };
        var preview = new MicrosoftJunkPreviewMatcher("acct", Own).Preview("acct", settings, messages);
        Assert.Equal(["mcafee"], preview.Matches.Select(m => m.MessageId));                                                          // the signed newsletter stays
        Assert.Equal(["Sender not verified", "Fake sender address", "Pretends to be McAfee"], preview.Matches[0].Reasons);
        preview = new MicrosoftJunkPreviewMatcher("acct", Own).Preview("acct", settings with { Brands = ["Acme"] }, messages);
        Assert.Equal(["mcafee", "acme"], preview.Matches.Select(m => m.MessageId));
        Assert.DoesNotContain("Pretends to be McAfee", preview.Matches[0].Reasons);                                                 // the owner's list replaces the built-in one
    }

    [Fact]
    public void The_rules_numbers_and_company_list_survive_saving_and_travel_with_an_exported_list()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oo-junksignals-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new JunkCleanerSettingsStore(dir);
            var rules = AllOn with { CrowdSize = 25, BulkLevel = 7 };
            store.Save(new JunkCleanerSettings { Accounts = [new JunkCleanerAccountSettings { AccountId = "a1", Enabled = true, Rules = rules, Brands = ["Acme", "Globex"] }] });
            var loaded = store.Load().Accounts.Single();
            Assert.Equal(rules, loaded.Rules);
            Assert.Equal(["Acme", "Globex"], loaded.Brands);
            var export = Path.Combine(dir, "export.json");
            JunkCleanerSettingsStore.ExportPortable(export, loaded);
            var imported = JunkCleanerSettingsStore.PreviewLegacyConfig(export).ImportForAccount("a2");
            Assert.Equal(rules, imported.Rules);
            Assert.Equal(["Acme", "Globex"], imported.Brands);
            File.WriteAllText(export, "{\"keywords\":[\"casino\"],\"deleteHighImportance\":true}");                                    // an old file: the new rules stay off
            Assert.Equal(new JunkRuleOptions(DeleteHighImportance: true), JunkCleanerSettingsStore.PreviewLegacyConfig(export).Rules);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
