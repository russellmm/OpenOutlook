using OpenOutlook.JunkCleaner;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

public sealed class MicrosoftJunkPreviewMatcherTests
{
    private static readonly JunkRuleOptions AllRules = new(
        DeleteHighImportance: true, DeleteMissingTo: true, DeleteOnBehalfOf: true);

    [Fact]
    public void Requires_explicit_opt_in_and_same_bound_account()
    {
        var matcher = new MicrosoftJunkPreviewMatcher("graph-user-1");
        var message = Message("message-1", from: "offer@junk.example");
        var disabled = Settings("graph-user-1") with { Enabled = false };
        var preview = matcher.Preview("graph-user-1", disabled, [message]);
        Assert.Equal("graph-user-1", preview.AccountId);
        Assert.Equal(1, preview.ScannedCount);
        Assert.Equal(0, preview.MatchCount);
        Assert.Empty(preview.Matches);
        Assert.Throws<ArgumentException>(() => matcher.Preview("graph-user-2", Settings("graph-user-1"), [message]));
        Assert.Throws<ArgumentException>(() => matcher.Preview("graph-user-1", Settings("graph-user-2"), [message]));
        Assert.Throws<ArgumentException>(() => new MicrosoftJunkPreviewMatcher(" "));
    }

    [Fact]
    public void Matches_sender_keyword_and_optional_rules_without_exposing_content()
    {
        var matcher = new MicrosoftJunkPreviewMatcher("graph-user-1");
        var message = Message("id-1", from: "represented@example.net", sender: "DEALS@junk.example",
            importance: "high", hasTo: false,
            headers: [new GraphInternetHeader("From", "Private Sender <private@example.net>")]);
        var preview = matcher.Preview("graph-user-1", Settings("graph-user-1"), [message]);
        Assert.Equal(1, preview.MatchCount);
        Assert.Equal("id-1", Assert.Single(preview.Matches).MessageId);
        Assert.Equal(["From keyword", "High importance", "No To address", "On behalf of"],
            Assert.Single(preview.Matches).Reasons);
        Assert.DoesNotContain("junk.example", string.Join(" ", preview.Matches[0].Reasons));
        Assert.DoesNotContain("Private Sender", string.Join(" ", preview.Matches[0].Reasons));
    }

    [Fact]
    public void Missing_or_malformed_To_state_never_matches_no_To_rule()
    {
        var settings = Settings("graph-user-1") with
        {
            Keywords = [], Rules = new JunkRuleOptions(DeleteMissingTo: true)
        };
        var matcher = new MicrosoftJunkPreviewMatcher("graph-user-1");
        var unknown = Message("unknown", hasTo: null);
        var present = Message("present", hasTo: true);
        var absent = Message("absent", hasTo: false);
        var preview = matcher.Preview("graph-user-1", settings, [unknown, present, absent]);
        Assert.Equal(3, preview.ScannedCount);
        Assert.Equal("absent", Assert.Single(preview.Matches).MessageId);
        Assert.Equal(["No To address"], preview.Matches[0].Reasons);
    }

    [Fact]
    public void Unknown_importance_and_single_from_address_do_not_invent_optional_matches()
    {
        var settings = Settings("graph-user-1") with { Keywords = [] };
        var matcher = new MicrosoftJunkPreviewMatcher("graph-user-1");
        var message = Message("unknown", from: "author@example.net", importance: "unexpected");
        Assert.Empty(matcher.Preview("graph-user-1", settings, [message]).Matches);
    }

    [Fact]
    public void Rejects_duplicate_or_missing_ids_instead_of_ambiguous_preview()
    {
        var matcher = new MicrosoftJunkPreviewMatcher("graph-user-1");
        var settings = Settings("graph-user-1");
        Assert.Throws<ArgumentException>(() => matcher.Preview("graph-user-1", settings,
            [Message("same"), Message("same")]));
        Assert.Throws<ArgumentException>(() => matcher.Preview("graph-user-1", settings, [Message(" ")]));
    }

    private static JunkCleanerAccountSettings Settings(string accountId) => new()
    {
        AccountId = accountId, Enabled = true, Keywords = ["junk.example"], Rules = AllRules
    };

    private static GraphJunkMessage Message(string id, string? from = null, string? sender = null,
        string? importance = null, bool? hasTo = null, IReadOnlyList<GraphInternetHeader>? headers = null) =>
        new(id, "Private subject", from, sender, [], [], importance, headers ?? [], hasTo);
}
