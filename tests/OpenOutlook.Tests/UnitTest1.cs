using OpenOutlook.JunkCleaner;

namespace OpenOutlook.Tests;

public sealed class JunkMatcherTests
{
    [Fact]
    public void Keyword_matches_visible_sender_components_case_insensitively()
    {
        var fields = new FromFields("Safe Display", null, "offers@bad.example", null, null, "From: Someone");
        Assert.Contains("From contains: BAD.EXAMPLE", JunkMatcher.MatchReasons(
            fields, ["BAD.EXAMPLE"], 1, true, new JunkRuleOptions()));
    }

    [Fact]
    public void Unknown_to_and_unknown_sender_do_not_trigger_optional_rules()
    {
        var fields = new FromFields(null, null, null, "Forwarded Identity", null, null);
        Assert.Empty(JunkMatcher.MatchReasons(fields, [], null, null,
            new JunkRuleOptions(DeleteMissingTo: true, DeleteOnBehalfOf: true, DeleteHighImportance: true)));
    }

    [Fact]
    public void Explicit_optional_rules_report_reasons()
    {
        var fields = new FromFields("Sender", null, null, "Representing", null, null);
        var reasons = JunkMatcher.MatchReasons(fields, [], 2, false,
            new JunkRuleOptions(DeleteHighImportance: true, DeleteMissingTo: true, DeleteOnBehalfOf: true));
        Assert.Equal(3, reasons.Count);
    }

    [Fact]
    public void Empty_keywords_do_not_match()
    {
        Assert.Empty(JunkMatcher.MatchReasons(new FromFields("Acme", null, null, null, null, null),
            ["", "  "], 1, true, new JunkRuleOptions()));
    }
}
