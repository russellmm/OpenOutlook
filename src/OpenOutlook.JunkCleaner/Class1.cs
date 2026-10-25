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
    bool DeleteOnBehalfOf = false,
    bool DeleteFlagged = false,
    bool DeleteWithBcc = false,
    bool DeleteNotToMe = false,
    // Header and subject rules from the 2026-10-25 survey of the Hotmail Junk folder (JunkSignals).
    bool DeleteUnauthenticated = false,
    bool DeleteFakeSenderAddress = false,
    bool DeleteImpersonation = false,
    bool DeleteHiddenText = false,
    bool DeleteInvitations = false,
    bool DeleteCrowd = false,
    bool DeleteForeignScript = false,
    bool DeleteUsernameInSubject = false,
    bool DeleteEmojiSubject = false,
    bool DeleteBulk = false,
    bool DeleteFreeMail = false,
    bool DeleteRandomCode = false,
    int CrowdSize = 0,                 // 0 = JunkSignals.DefaultCrowdSize (a default(JunkRuleOptions) has zeros, not these defaults)
    int BulkLevel = 0)                 // 0 = JunkSignals.DefaultBulkLevel
{
    public int EffectiveCrowdSize => CrowdSize > 0 ? CrowdSize : JunkSignals.DefaultCrowdSize;
    public int EffectiveBulkLevel => BulkLevel is >= 1 and <= 9 ? BulkLevel : JunkSignals.DefaultBulkLevel;
}

public static class JunkMatcher
{
    public static string BuildHaystack(FromFields fields) => string.Join(" ", new[]
    {
        fields.SenderName, fields.SenderEmailAddress, fields.SenderSmtp,
        fields.RepresentingName, fields.RepresentingSmtp, fields.InternetFromLine
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public static IReadOnlyList<string> MatchReasons(
        FromFields fields, IReadOnlyList<string> keywords, int? importance,
        bool? hasToAddress, JunkRuleOptions options, bool? isFlagged = null, bool? hasBcc = null,
        IReadOnlyList<string>? toRecipients = null, string? ownAddress = null)
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
        if (options.DeleteFlagged && isFlagged == true)
            reasons.Add("Flagged");
        // A received message with anyone in Bcc is a mass mailing: a recipient never sees the Bcc list of real mail (spam that addresses its targets
        // through a visible "BCC:" header, 2026-10-16). Unknown is not a match.
        if (options.DeleteWithBcc && hasBcc == true)
            reasons.Add("Bcc recipients");
        // Junk not addressed to the account itself (owner 2026-10-17): the account's address is not on the To line (Cc and Bcc do not count). Unknown
        // To recipients or an unknown own address is not a match; an empty To line is (it is not addressed to the account either).
        if (options.DeleteNotToMe && !string.IsNullOrWhiteSpace(ownAddress) && toRecipients is not null && hasToAddress is not null &&
            !toRecipients.Any(r => string.Equals(r?.Trim(), ownAddress.Trim(), StringComparison.OrdinalIgnoreCase)))
            reasons.Add("Not addressed to you");
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
