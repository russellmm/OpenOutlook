using OpenOutlook.JunkCleaner;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

/// <summary>"Also remove junk not addressed to me" (owner 2026-10-17): the account's own address is not on the To line.</summary>
public sealed class JunkNotToMeRuleTests
{
    private static readonly FromFields Fields = new(null, "x@spam.example", "x@spam.example", null, null, "x@spam.example");
    private static readonly JunkRuleOptions On = new(DeleteNotToMe: true);

    [Fact]
    public void Matches_when_the_own_address_is_missing_from_to_and_not_when_it_is_there_or_unknown()
    {
        Assert.Equal(["Not addressed to you"], JunkMatcher.MatchReasons(Fields, [], 1, true, On, toRecipients: ["someone@else.example"], ownAddress: "rmarrash@hotmail.com"));
        Assert.Empty(JunkMatcher.MatchReasons(Fields, [], 1, true, On, toRecipients: ["a@b.example", " RMarrash@Hotmail.com "], ownAddress: "rmarrash@hotmail.com"));
        Assert.Equal(["Not addressed to you"], JunkMatcher.MatchReasons(Fields, [], 1, false, On, toRecipients: [], ownAddress: "rmarrash@hotmail.com"));   // empty To line
        Assert.Empty(JunkMatcher.MatchReasons(Fields, [], 1, null, On, toRecipients: [], ownAddress: "rmarrash@hotmail.com"));    // To unknown
        Assert.Empty(JunkMatcher.MatchReasons(Fields, [], 1, true, On, toRecipients: ["a@b.example"], ownAddress: null));         // own address unknown
        Assert.Empty(JunkMatcher.MatchReasons(Fields, [], 1, true, new JunkRuleOptions(), toRecipients: ["a@b.example"], ownAddress: "rmarrash@hotmail.com"));
    }

    [Fact]
    public void The_preview_uses_the_accounts_address_and_only_the_to_line()
    {
        var messages = new[]
        {
            new GraphJunkMessage("other", "s", "x@spam.example", "x@spam.example", ["victim@else.example"], ["rmarrash@hotmail.com"], "normal", [], HasToRecipients: true),
            new GraphJunkMessage("mine", "s", "x@spam.example", "x@spam.example", ["rmarrash@hotmail.com"], [], "normal", [], HasToRecipients: true),
        };
        var settings = new JunkCleanerAccountSettings { AccountId = "acct", Enabled = true, Rules = On };
        var preview = new MicrosoftJunkPreviewMatcher("acct", "rmarrash@hotmail.com").Preview("acct", settings, messages);
        Assert.Equal(["other"], preview.Matches.Select(m => m.MessageId));                                // in Cc only still counts as not addressed
        Assert.Empty(new MicrosoftJunkPreviewMatcher("acct").Preview("acct", settings, messages).Matches); // no own address: the rule is off
    }

    [Fact]
    public void The_rule_survives_saving_and_travels_with_an_exported_list()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oo-junknottome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new JunkCleanerSettingsStore(dir);
            store.Save(new JunkCleanerSettings { Accounts = [new JunkCleanerAccountSettings { AccountId = "a1", Enabled = true, Rules = On }] });
            Assert.True(store.Load().Accounts.Single().Rules.DeleteNotToMe);
            var export = Path.Combine(dir, "export.json");
            JunkCleanerSettingsStore.ExportPortable(export, store.Load().Accounts.Single());
            Assert.True(JunkCleanerSettingsStore.PreviewLegacyConfig(export).Rules.DeleteNotToMe);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
