using System.Text;
using OpenOutlook.Desktop;
using OpenOutlook.JunkCleaner;

namespace OpenOutlook.Tests;

/// <summary>Safe Senders, Safe Recipients and Blocked Senders (docs/junk-lists-plan.md).</summary>
public sealed class JunkSenderListsTests
{
    [Fact]
    public void Entries_are_normalised_like_outlooks_list_domains_first_and_bad_ones_dropped()
    {
        var list = JunkLists.Normalize(JunkListKind.SafeSenders,
            ["  Bob@Example.COM ", "example.org", "*@shop.example", "@amazon.com", "bob@example.com", "two words@x.com", "a@b@c.com", "nodot", "", null, "x@.com"]);
        Assert.Equal(["@amazon.com", "@example.org", "@shop.example", "bob@example.com"], list);
        Assert.Equal(["cn", "ru"], JunkLists.Normalize(JunkListKind.BlockedTopLevelDomains, [".ru", "RU", "cn", "c n"]));
    }

    [Fact]
    public void Safe_wins_over_blocked_and_domains_tlds_recipients_and_contacts_count()
    {
        var lists = new JunkLists()
            .With(JunkListKind.SafeSenders, ["@alertsp.chase.com", "friend@spam.example"])
            .With(JunkListKind.SafeRecipients, ["openhornet@noreply.github.com"])
            .With(JunkListKind.BlockedSenders, ["@spam.example", "1ppmgfh@peterlink.ru"])
            .With(JunkListKind.BlockedTopLevelDomains, ["cn"]);
        Assert.Equal(JunkListVerdict.Safe, lists.Classify("Chase <no-reply@alertsp.chase.com>"));
        Assert.Equal(JunkListVerdict.None, lists.Classify("x@email.alertsp.chase.com"));                    // a subdomain is not the domain
        Assert.Equal(JunkListVerdict.Safe, lists.Classify("friend@spam.example"));                          // safe address beats blocked domain
        Assert.Equal(JunkListVerdict.Blocked, lists.Classify("other@SPAM.example"));
        Assert.Equal(JunkListVerdict.Blocked, lists.Classify("1PPMGFH@peterlink.ru"));
        Assert.Equal(JunkListVerdict.Blocked, lists.Classify("a@mail.shop.cn"));
        Assert.Equal(JunkListVerdict.Safe, lists.Classify("other@spam.example", ["OpenHornet <openhornet@noreply.github.com>"]));
        Assert.Equal(JunkListVerdict.Safe, lists.Classify("pal@x.com", null, new HashSet<string> { "pal@x.com" }));
        Assert.Equal(JunkListVerdict.None, lists.Classify(null));
        Assert.Equal("@spam.example", JunkLists.MatchingEntry(lists.BlockedSenders, "z@spam.example"));
    }

    [Fact]
    public void Outlooks_text_files_import_in_utf8_or_utf16_and_export_one_entry_per_line()
    {
        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("@amazon.com\r\nbob@x.com\r\n\r\n# note\r\n")).ToArray();
        Assert.Equal(["@amazon.com", "bob@x.com"], JunkLists.ParseTextFile(JunkListKind.SafeSenders, utf16));
        var utf8 = Encoding.UTF8.GetBytes("a@b.com\nc.org\n");
        Assert.Equal(["@c.org", "a@b.com"], JunkLists.ParseTextFile(JunkListKind.BlockedSenders, utf8));
        Assert.Equal("@c.org\r\na@b.com\r\n", Encoding.UTF8.GetString(JunkLists.ToTextFile(["@c.org", "a@b.com"])));
    }

    [Fact]
    public void Outlooks_junk_rule_condition_decodes_into_the_three_lists()
    {
        var condition = SampleCondition(blocked: ["1ppmgfh@peterlink.ru"], blockedDomains: ["@cmpgnr.com"], safeDomains: ["@amazon.com"],
            safe: ["1800usbanks@email.usbank.com"], safeRecipients: ["jessica.marrash@gmail.com"]);
        var lists = OutlookJunkRuleParser.Parse(condition);
        Assert.Equal(["@cmpgnr.com", "1ppmgfh@peterlink.ru"], lists.BlockedSenders);
        Assert.Equal(["@amazon.com", "1800usbanks@email.usbank.com"], lists.SafeSenders);
        Assert.Equal(["jessica.marrash@gmail.com"], lists.SafeRecipients);
        Assert.Throws<FormatException>(() => OutlookJunkRuleParser.Parse(condition[..^7]));                    // damaged: never reads past the end
        Assert.Throws<FormatException>(() => OutlookJunkRuleParser.Parse([0, 0, 0x2A]));
        Assert.Equal("No Automatic Filtering", new OutlookJunkOptions(0xFFFFFFFF, false, false, false).Level);
        Assert.Equal("High", new OutlookJunkOptions(3, false, false, false).Level);
    }

    [Fact]
    public void Edits_keep_outlook_entries_apart_so_a_removed_one_stays_removed_and_the_file_round_trips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oo-junklists-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JunkListsStore(dir);
            Assert.Equal("acc", store.Load("acc").AccountId);
            var account = new JunkListsAccount
            {
                AccountId = "acc",
                FromOutlook = new JunkLists().With(JunkListKind.SafeSenders, ["@amazon.com", "old@x.com"]),
            };
            account = account.WithEdited(JunkListKind.SafeSenders, ["@amazon.com", "new@y.com"]);       // removed old@x.com, added new@y.com
            Assert.Equal(["@amazon.com", "new@y.com"], account.Effective.SafeSenders);
            Assert.Equal(["old@x.com"], account.RemovedFromOutlook.SafeSenders);
            Assert.Equal(["new@y.com"], account.Own.SafeSenders);
            var reread = account with { FromOutlook = new JunkLists().With(JunkListKind.SafeSenders, ["@amazon.com", "old@x.com", "more@z.com"]) };
            Assert.Equal(["@amazon.com", "more@z.com", "new@y.com"], reread.Effective.SafeSenders);       // the next read from Outlook
            reread = reread.WithAdded(JunkListKind.SafeSenders, "old@x.com");                                  // Never Block Sender brings it back
            Assert.Contains("old@x.com", reread.Effective.SafeSenders);
            store.Save(reread);
            Assert.Equal(reread.Effective.SafeSenders, store.Load("acc").Effective.SafeSenders);
            File.WriteAllText(store.Path, "{ broken");
            Assert.Empty(store.LoadAll());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The owner's real Outlook offline file, when OO_OUTLOOK_OST points to a copy of it (never the original while Outlook runs).</summary>
    [Fact]
    public void Reads_the_lists_from_a_copy_of_outlooks_offline_file_when_one_is_given()
    {
        var path = Environment.GetEnvironmentVariable("OO_OUTLOOK_OST");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var result = OutlookJunkListReader.ReadFile(path);
        Assert.True(result.Lists.BlockedSenders.Count > 0);
        Assert.True(result.Lists.SafeSenders.Count > 0);
        Console.WriteLine($"blocked {result.Lists.BlockedSenders.Count}, safe {result.Lists.SafeSenders.Count}, safe recipients {result.Lists.SafeRecipients.Count}, level {result.Options.Level}");
    }

    [Fact]
    public void Reading_is_skipped_when_the_file_is_unchanged_or_outlook_is_running()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oo-ost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Contains("no offline file", OutlookJunkListReader.Read("me@hotmail.com", 0, outlookFolder: dir, outlookRunning: () => false).Problem);
            var file = Path.Combine(dir, "Me@Hotmail.com.ost");
            File.WriteAllBytes(file, [1, 2, 3]);
            var stamp = File.GetLastWriteTimeUtc(file).Ticks;
            Assert.True(OutlookJunkListReader.Read("me@hotmail.com", stamp, outlookFolder: dir, outlookRunning: () => false).Unchanged);
            Assert.Contains("Outlook is running", OutlookJunkListReader.Read("me@hotmail.com", 0, outlookFolder: dir, outlookRunning: () => true).Problem);
            Assert.Contains("could not be read", OutlookJunkListReader.Read("me@hotmail.com", 0, outlookFolder: dir, outlookRunning: () => false).Problem);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The shape Outlook writes ([MS-OXCSPAM]; seen in the owner's .ost, docs/junk-lists-plan.md section 1b).</summary>
    internal static byte[] SampleCondition(string[] blocked, string[] blockedDomains, string[] safeDomains, string[] safe, string[] safeRecipients)
    {
        var b = new List<byte>();
        void U8(int v) => b.Add((byte)v);
        void U32(uint v) => b.AddRange(BitConverter.GetBytes(v));
        void Content(uint tag, uint fuzzy, string s) { U8(3); U32(fuzzy); U32(tag); U32(tag); b.AddRange(Encoding.Unicode.GetBytes(s)); b.AddRange([0, 0]); }
        void Or(IReadOnlyList<string> items, uint tag, uint fuzzy) { U8(1); U32((uint)items.Count); foreach (var s in items) Content(tag, fuzzy, s); }
        b.AddRange([0, 0]);                                                                       // no named properties
        U8(0); U32(2);                                                                             // AND
        U8(1); U32(2);                                                                             //   OR
        Or(blocked, 0x0C1F001F, 0x10000);                                                          //     blocked addresses
        U8(0); U32(2);                                                                             //     AND
        U8(1); U32(2);                                                                             //       OR
        U8(0); U32(2); U8(8); U32(0x40760003); U8(4); U8(2); U32(0x40760003); U32(0x40760003); U32(0xFFFFFFFF);   // spam confidence
        Or(blockedDomains, 0x0C1F001F, 0x10001);                                                   //         blocked domains
        U8(2); U8(1); U32(2); Or(safeDomains, 0x0C1F001F, 0x10001); U8(9); U32(0x0E12000D); U8(1); U32(0);   // NOT(safe domains, recipients)
        U8(2); U8(1); U32(3);                                                                      //   NOT OR
        Or(safe, 0x0C1F001F, 0x10000);
        U8(9); U32(0x0E12000D); Or(safeRecipients, 0x3003001F, 0x10000);
        U8(1); U32(0);
        return b.ToArray();
    }
}
