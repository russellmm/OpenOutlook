namespace OpenOutlook.JunkCleaner;

/// <summary>Configuration only; account IDs must come from a caller's account provider.</summary>
public sealed record JunkCleanerAccountSettings
{
    public required string AccountId { get; init; }
    public bool Enabled { get; init; } // Each account must be explicitly opted in.
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public int IntervalMinutes { get; init; } = 15;
    public JunkRuleOptions Rules { get; init; } = new();
}

public sealed record JunkCleanerSettings
{
    public IReadOnlyList<JunkCleanerAccountSettings> Accounts { get; init; } = Array.Empty<JunkCleanerAccountSettings>();
}

/// <summary>A read-only preview of portable legacy fields; AlwaysClean is informational only.</summary>
public sealed record LegacyJunkCleanerPreview
{
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public bool AlwaysClean { get; init; }
    public int IntervalMinutes { get; init; } = 15;
    public JunkRuleOptions Rules { get; init; } = new();

    /// <summary>Apply portable fields to an account without opting it in to cleaning.</summary>
    public JunkCleanerAccountSettings ImportForAccount(string accountId) =>
        new()
        {
            AccountId = JunkCleanerSettingsStore.ValidateAccountId(accountId),
            Enabled = false,
            Keywords = JunkCleanerSettingsStore.NormalizeKeywords(Keywords),
            IntervalMinutes = JunkCleanerSettingsStore.ValidateInterval(IntervalMinutes),
            Rules = Rules
        };
}

internal sealed class LegacyJunkCleanerDto
{
    public List<string>? Keywords { get; set; }
    public bool AlwaysClean { get; set; }
    public int? IntervalMinutes { get; set; }
    public bool DeleteHighImportance { get; set; }
    public bool DeleteMissingTo { get; set; }
    public bool DeleteOnBehalfOf { get; set; }

    // Windows-only startup/tray fields are intentionally not modeled or imported.
}
