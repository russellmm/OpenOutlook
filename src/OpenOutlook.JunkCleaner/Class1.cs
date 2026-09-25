namespace OpenOutlook.JunkCleaner;

// Portable matching logic adapted from the owner's OutlookJunkCleaner.Core.
// The Windows Outlook COM mailbox adapter is deliberately not used here.
public readonly record struct FromFields(
    string? SenderName,
    string? SenderEmailAddress,
    string? SenderSmtp,
    string? RepresentingName,
    string? RepresentingSmtp,
    string? InternetFromLine);

public readonly record struct JunkRuleOptions(
    bool DeleteHighImportance = false,
    bool DeleteMissingTo = false,
    bool DeleteOnBehalfOf = false);

public static class JunkMatcher
{
    public static string BuildHaystack(FromFields fields) => string.Join(" ", new[]
    {
        fields.SenderName, fields.SenderEmailAddress, fields.SenderSmtp,
        fields.RepresentingName, fields.RepresentingSmtp, fields.InternetFromLine
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public static IReadOnlyList<string> MatchReasons(
        FromFields fields, IReadOnlyList<string> keywords, int? importance,
        bool? hasToAddress, JunkRuleOptions options)
    {
        var reasons = new List<string>();
        var haystack = BuildHaystack(fields);
        foreach (var keyword in keywords)
        {
            var term = keyword.Trim();
            if (term.Length > 0 && haystack.Contains(term, StringComparison.OrdinalIgnoreCase))
                reasons.Add($"From contains: {term}");
        }
        if (options.DeleteHighImportance && importance == 2)
            reasons.Add("High importance");
        // A missing/unavailable provider field is unknown, not a positive match.
        if (options.DeleteMissingTo && hasToAddress == false)
            reasons.Add("No To address");
        if (options.DeleteOnBehalfOf && LooksLikeOnBehalfOf(fields))
            reasons.Add("On behalf of");
        return reasons;
    }

    public static bool LooksLikeOnBehalfOf(FromFields fields)
    {
        if (BuildHaystack(fields).Contains("on behalf of", StringComparison.OrdinalIgnoreCase))
            return true;
        var representing = First(fields.RepresentingName, fields.RepresentingSmtp);
        var sender = First(fields.SenderName, fields.SenderSmtp, fields.SenderEmailAddress);
        // Unknown sender cannot prove a difference.
        return representing is not null && sender is not null &&
               !string.Equals(representing, sender, StringComparison.OrdinalIgnoreCase);
    }

    private static string? First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
