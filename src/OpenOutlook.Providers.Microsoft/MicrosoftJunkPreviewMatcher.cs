using OpenOutlook.JunkCleaner;

namespace OpenOutlook.Providers.Microsoft;

/// <summary>A read-only match with reason labels, not a command to move or delete mail.</summary>
public sealed record MicrosoftJunkPreviewMatch(string MessageId, IReadOnlyList<string> Reasons);

/// <summary>Only matched IDs and reason labels are returned; message text and headers are never copied into the preview.</summary>
public sealed record MicrosoftJunkPreview(string AccountId, int ScannedCount,
    IReadOnlyList<MicrosoftJunkPreviewMatch> Matches)
{
    public int MatchCount => Matches.Count;
}

/// <summary>
/// Pure Graph-to-portable-matcher adapter. The caller must bind this to the same verified Graph /me ID
/// used by GraphJunkMailReader and supply only messages read from that account's Junk folder.
/// This type never contacts Graph and has no mutating mail operations.
/// </summary>
public sealed class MicrosoftJunkPreviewMatcher
{
    private readonly string _boundAccountId;

    public MicrosoftJunkPreviewMatcher(string boundAccountId) =>
        _boundAccountId = JunkCleanerSettingsStore.ValidateAccountId(boundAccountId);

    /// <summary>Match only an opted-in account whose scan ID and settings ID both equal the bound Graph user ID.</summary>
    public MicrosoftJunkPreview Preview(string scannedAccountId, JunkCleanerAccountSettings settings,
        IReadOnlyList<GraphJunkMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(messages);
        if (!string.Equals(JunkCleanerSettingsStore.ValidateAccountId(scannedAccountId), _boundAccountId,
                StringComparison.Ordinal) ||
            !string.Equals(JunkCleanerSettingsStore.ValidateAccountId(settings.AccountId), _boundAccountId,
                StringComparison.Ordinal))
            throw new ArgumentException("Preview account does not match the bound Graph account.");

        // An absent opt-in yields no matches even when rules/keywords have already been configured.
        if (!settings.Enabled)
            return new MicrosoftJunkPreview(_boundAccountId, messages.Count, Array.Empty<MicrosoftJunkPreviewMatch>());

        var keywords = JunkCleanerSettingsStore.NormalizeKeywords(settings.Keywords);
        var matches = new List<MicrosoftJunkPreviewMatch>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Id) || !seen.Add(message.Id))
                throw new ArgumentException("Preview requires messages with unique nonempty IDs.", nameof(messages));

            // Graph 'sender' is the actual sender; 'from' is the represented author. If sender is
            // unavailable, do not invent an on-behalf-of difference from a single address.
            var sender = !string.IsNullOrWhiteSpace(message.Sender) ? message.Sender : message.From;
            var represented = !string.IsNullOrWhiteSpace(message.Sender) ? message.From : null;
            var fromHeader = message.InternetMessageHeaders?.FirstOrDefault(header =>
                string.Equals(header?.Name, "From", StringComparison.OrdinalIgnoreCase))?.Value;
            var fields = new FromFields(null, sender, sender, null, represented, fromHeader);
            var importance = message.Importance?.ToLowerInvariant() switch
            {
                "high" => 2,
                "normal" => 1,
                "low" => 0,
                _ => (int?)null
            };
            // HasToRecipients is null if the Graph field was absent or malformed. Never infer
            // 'no To' from ToRecipients.Count: the old reader collapsed missing and empty lists.
            var reasons = JunkMatcher.MatchReasons(fields, keywords, importance,
                message.HasToRecipients, settings.Rules);
            if (reasons.Count != 0)
            {
                // The portable matcher embeds the matching keyword in its reason; preview
                // exposes only a category so it never echoes configured terms or raw mail text.
                var safeReasons = reasons.Select(reason => reason.StartsWith("From contains: ", StringComparison.Ordinal)
                        ? "From keyword" : reason).Distinct(StringComparer.Ordinal).ToArray();
                matches.Add(new MicrosoftJunkPreviewMatch(message.Id, safeReasons));
            }
        }
        return new MicrosoftJunkPreview(_boundAccountId, messages.Count, matches.ToArray());
    }
}
