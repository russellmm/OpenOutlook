using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenOutlook.JunkCleaner;

/// <summary>What the header and subject rules look at; a null field is unknown and never a match.</summary>
public sealed record JunkMessageFacts(
    string? Subject,
    string? FromAddress,
    string? FromHeader,                                         // the raw From: line, for the display name
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    int? RecipientCount,                                        // To + Cc
    string? OwnAddress);

/// <summary>
/// The header and subject rules added after the owner's Junk folder survey (2026-10-25, 63 messages the first six rules missed): unauthenticated
/// senders, impossible sender domains, company names in the display name, hidden subject characters, calendar invitations, crowds of recipients,
/// other alphabets, the mailbox name in the subject, emoji subjects, Microsoft's bulk rating, free mail services and random codes.
/// Mail from Safe Senders (and trusted contacts) never reaches these rules: the runner drops it first.
/// </summary>
public static class JunkSignals
{
    public const int DefaultCrowdSize = 10;
    public const int DefaultBulkLevel = 5;

    /// <summary>Companies whose names spammers put in the display name. Matched as whole words; the sender's domain containing the name clears it.</summary>
    public static readonly IReadOnlyList<string> DefaultBrands =
    [
        "Amazon", "Apple", "AARP", "AT&T", "Bank of America", "Best Buy", "Chase", "Costco", "DHL", "FedEx", "Geek Squad", "Home Depot", "Kroger",
        "Lowe's", "Macy's", "McAfee", "Medicare", "Microsoft", "Netflix", "Norton", "Omaha Steaks", "PayPal", "Sam's Club", "SiriusXM", "Target",
        "T-Mobile", "UPS", "USPS", "Verizon", "Walmart", "Wells Fargo", "Xfinity"
    ];

    private static readonly string[] FreeMailDomains =
    [
        "gmail.com", "googlemail.com", "yahoo.com", "ymail.com", "rocketmail.com", "outlook.com", "hotmail.com", "live.com", "msn.com", "aol.com",
        "icloud.com", "me.com", "mac.com", "mail.com", "gmx.com", "gmx.net", "proton.me", "protonmail.com", "yandex.com", "yandex.ru", "zoho.com", "mail.ru"
    ];

    // "Invitation:" in the languages the survey and Google / Outlook calendars use.
    private static readonly string[] InvitationPrefixes =
    [
        "invitation:", "updated invitation:", "invitation mise à jour:", "einladung:", "invitación:", "convite:", "invito:", "uitnodiging:",
        "초대장:", "招待:", "邀请:", "邀請:", "приглашение:"
    ];

    private static readonly Regex RandomCode = new(@"[A-Za-z0-9_#\-]{16,}", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Reasons(JunkMessageFacts m, JunkRuleOptions o, IReadOnlyList<string>? brands = null)
    {
        var reasons = new List<string>();
        if (o.DeleteUnauthenticated && IsUnauthenticated(m.Headers)) reasons.Add("Sender not verified");
        if (o.DeleteFakeSenderAddress && IsFakeAddress(m.FromAddress)) reasons.Add("Fake sender address");
        if (o.DeleteImpersonation && ImpersonatedBrand(m.FromHeader, m.FromAddress, brands is { Count: > 0 } ? brands : DefaultBrands) is { } brand)
            reasons.Add($"Pretends to be {brand}");
        if (o.DeleteHiddenText && HasHiddenCharacters(m.Subject)) reasons.Add("Hidden characters in subject");
        if (o.DeleteInvitations && IsInvitation(m.Subject, m.Headers)) reasons.Add("Calendar invitation");
        if (o.DeleteCrowd && m.RecipientCount is { } n && n >= o.EffectiveCrowdSize) reasons.Add($"Sent to {n} people");
        if (o.DeleteForeignScript && HasOtherAlphabet(m.Subject)) reasons.Add("Subject in another alphabet");
        if (o.DeleteUsernameInSubject && UsesUsername(m.Subject, m.FromHeader, m.OwnAddress)) reasons.Add("Uses your username");
        if (o.DeleteEmojiSubject && StartsWithEmoji(m.Subject)) reasons.Add("Emoji subject");
        if (o.DeleteBulk && BulkLevel(m.Headers) is { } bcl && bcl >= o.EffectiveBulkLevel) reasons.Add($"Bulk rating {bcl}");
        if (o.DeleteFreeMail && IsFreeMail(m.FromAddress)) reasons.Add("Free email address");
        if (o.DeleteRandomCode && HasRandomCode(m.Subject)) reasons.Add("Random code in subject");
        return reasons;
    }

    private static IEnumerable<string> Values(IReadOnlyList<KeyValuePair<string, string>> headers, string name) =>
        headers.Where(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value ?? "");

    /// <summary>
    /// Microsoft's composite verdict (compauth) when the receiving server wrote one, else: DMARC failed, or neither SPF nor DKIM passed.
    /// No Authentication-Results header is unknown, not a match.
    /// </summary>
    public static bool IsUnauthenticated(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var results = Values(headers, "Authentication-Results").ToList();
        if (results.Count == 0) return false;
        var withCompauth = results.FirstOrDefault(r => r.Contains("compauth=", StringComparison.OrdinalIgnoreCase));
        if (withCompauth is not null)
            return Result(withCompauth, "compauth") is not ("pass" or "softpass");
        var all = string.Join(";", results);
        if (Result(all, "dmarc") == "fail") return true;
        return Result(all, "spf") != "pass" && Result(all, "dkim") != "pass";
    }

    private static string? Result(string text, string method)
    {
        var match = Regex.Match(text, @"(?:^|[\s;])" + method + @"=([a-z]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>A domain no real mail server can have: an empty label, a label starting or ending with "-", or an ending that is not letters (or an xn-- name).</summary>
    public static bool IsFakeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var at = address.Trim().LastIndexOf('@');
        if (at <= 0 || at == address.Trim().Length - 1) return true;
        var labels = address.Trim()[(at + 1)..].TrimEnd('.').Split('.');
        if (labels.Length < 2 || labels.Any(l => l.Length == 0 || l.StartsWith('-') || l.EndsWith('-'))) return true;
        var tld = labels[^1].ToLowerInvariant();
        return !(Regex.IsMatch(tld, "^[a-z]{2,24}$") || Regex.IsMatch(tld, "^xn--[a-z0-9-]{2,59}$"));
    }

    /// <summary>The company a display name claims when the address is not that company's (its domain does not contain the name).</summary>
    public static string? ImpersonatedBrand(string? fromHeader, string? fromAddress, IReadOnlyList<string> brands)
    {
        var name = DisplayName(fromHeader);
        if (string.IsNullOrWhiteSpace(name)) return null;
        var domain = Compact(fromAddress?[(fromAddress.LastIndexOf('@') + 1)..] ?? "");
        foreach (var brand in brands)
        {
            var b = brand.Trim();
            if (b.Length < 2) continue;
            var pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(b).Replace("'", "['’]") + @"(?![\p{L}\p{N}])";
            if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) && !domain.Contains(Compact(b), StringComparison.Ordinal))
                return b;
        }
        return null;
    }

    /// <summary>The display name of a From line: the text before "&lt;", without quotes; a bare address has none.</summary>
    public static string? DisplayName(string? fromHeader)
    {
        if (string.IsNullOrWhiteSpace(fromHeader)) return null;
        var lt = fromHeader.IndexOf('<');
        if (lt <= 0) return null;
        var name = fromHeader[..lt].Trim().Trim('"').Trim();
        return name.Length == 0 ? null : name;
    }

    private static string Compact(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Invisible format characters (right-to-left overrides, zero-width spaces) in the subject; the joiner and tag characters emoji use are allowed.</summary>
    public static bool HasHiddenCharacters(string? subject) =>
        subject is not null && subject.EnumerateRunes().Any(r =>
            Rune.GetUnicodeCategory(r) == UnicodeCategory.Format && r.Value != 0x200D && r.Value is not (>= 0xE0020 and <= 0xE007F));

    /// <summary>A calendar invitation: "Invitation:" (or its translations) at the start of the subject, or a text/calendar body.</summary>
    public static bool IsInvitation(string? subject, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var s = subject?.TrimStart() ?? "";
        return InvitationPrefixes.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
               Values(headers, "Content-Type").Any(v => v.Contains("text/calendar", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>At least three letters of a non-Latin alphabet (Cyrillic, Greek, Hebrew, Arabic, Indic, Thai, Hangul, Chinese, Japanese...).</summary>
    public static bool HasOtherAlphabet(string? subject) =>
        subject is not null && subject.EnumerateRunes().Count(r => Rune.IsLetter(r) && IsOtherAlphabet(r.Value)) >= 3;

    private static bool IsOtherAlphabet(int c) =>
        c is (>= 0x0370 and <= 0x052F)      // Greek, Cyrillic
          or (>= 0x0530 and <= 0x08FF)      // Armenian, Hebrew, Arabic, Syriac...
          or (>= 0x0900 and <= 0x0DFF)      // Indic
          or (>= 0x0E00 and <= 0x0FFF)      // Thai, Lao, Tibetan
          or (>= 0x10A0 and <= 0x11FF)      // Georgian, Hangul Jamo
          or (>= 0x2E80 and <= 0xA4CF)      // CJK, kana, Hangul compatibility, Yi
          or (>= 0xAC00 and <= 0xD7AF)      // Hangul syllables
          or (>= 0x20000 and <= 0x3FFFF);   // CJK extensions

    /// <summary>The mailbox name (the part of the account address before "@", 4 letters or more) in the subject or the sender's display name.</summary>
    public static bool UsesUsername(string? subject, string? fromHeader, string? ownAddress)
    {
        var at = ownAddress?.IndexOf('@') ?? -1;
        if (at < 4) return false;
        var user = ownAddress![..at].Trim();
        return (subject?.Contains(user, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (DisplayName(fromHeader)?.Contains(user, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>The subject's first visible character is a pictograph (an emoji or symbol such as ⚡ ⏰ 🎰), not a letter, digit or punctuation.</summary>
    public static bool StartsWithEmoji(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        foreach (var r in subject.TrimStart().EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(r) == UnicodeCategory.Format) continue;          // a leading hidden character
            return Rune.GetUnicodeCategory(r) == UnicodeCategory.OtherSymbol || r.Value >= 0x1F000 && r.Value <= 0x1FAFF;
        }
        return false;
    }

    /// <summary>Microsoft's Bulk Complaint Level (BCL 0-9 in X-Microsoft-Antispam); none written is unknown.</summary>
    public static int? BulkLevel(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        foreach (var value in Values(headers, "X-Microsoft-Antispam"))
        {
            var match = Regex.Match(value, @"(?:^|;)\s*BCL:(\d)", RegexOptions.CultureInvariant);
            if (match.Success) return match.Groups[1].Value[0] - '0';
        }
        return null;
    }

    public static bool IsFreeMail(string? address)
    {
        var at = address?.LastIndexOf('@') ?? -1;
        if (at < 0) return false;
        var domain = address![(at + 1)..].Trim().ToLowerInvariant();
        return FreeMailDomains.Contains(domain) || Regex.IsMatch(domain, @"^(yahoo|hotmail|live|outlook)\.[a-z]{2,3}(\.[a-z]{2})?$");
    }

    /// <summary>A run of 16+ letters, digits, "_", "#" or "-" with at least 4 letters and 4 digits (tracking numbers are mostly digits and pass).</summary>
    public static bool HasRandomCode(string? subject) =>
        subject is not null && RandomCode.Matches(subject).Any(m => m.Value.Count(char.IsDigit) >= 4 && m.Value.Count(char.IsLetter) >= 4 &&
                                                                    m.Value.Any(char.IsUpper) && !m.Value.All(c => char.IsLetter(c)));
}
