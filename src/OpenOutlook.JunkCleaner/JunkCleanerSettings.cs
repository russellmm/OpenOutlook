namespace OpenOutlook.JunkCleaner;

/// <summary>Configuration only; account IDs must come from a caller's account provider.</summary>
public sealed record JunkCleanerAccountSettings
{
    public required string AccountId { get; init; }
    public bool Enabled { get; init; } // Each account must be explicitly opted in.
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public bool AutoClean { get; init; } // Scheduled silent cleaning; a manual "Clean now" works whenever Enabled is on.
    public int IntervalMinutes { get; init; } = 15;
    public JunkRuleOptions Rules { get; init; } = new();
    /// <summary>Company names for the "pretends to be a company" rule; null or empty = <see cref="JunkSignals.DefaultBrands"/>.</summary>
    public IReadOnlyList<string>? Brands { get; init; }
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
    public bool? DeleteFlagged { get; init; }     // null: the file does not say (an old OutlookJunkCleaner config)
    public IReadOnlyList<string>? Brands { get; init; }

    /// <summary>Apply portable fields to an account without opting it in to cleaning.</summary>
    public JunkCleanerAccountSettings ImportForAccount(string accountId) =>
        new()
        {
            AccountId = JunkCleanerSettingsStore.ValidateAccountId(accountId),
            Enabled = false,
            AutoClean = false,
            Keywords = JunkCleanerSettingsStore.NormalizeKeywords(Keywords),
            IntervalMinutes = JunkCleanerSettingsStore.ValidateInterval(IntervalMinutes),
            Rules = Rules,
            Brands = Brands
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
    public bool? DeleteFlagged { get; set; }      // only in files written by OpenOutlook's own export
    public bool? DeleteWithBcc { get; set; }      // only in files written by OpenOutlook's own export (2026-10-16 and later)
    public bool? DeleteNotToMe { get; set; }      // only in files written by OpenOutlook's own export (2026-10-17 and later)
    // The header and subject rules (2026-10-25 and later exports).
    public bool? DeleteUnauthenticated { get; set; }
    public bool? DeleteFakeSenderAddress { get; set; }
    public bool? DeleteImpersonation { get; set; }
    public bool? DeleteHiddenText { get; set; }
    public bool? DeleteInvitations { get; set; }
    public bool? DeleteCrowd { get; set; }
    public bool? DeleteForeignScript { get; set; }
    public bool? DeleteUsernameInSubject { get; set; }
    public bool? DeleteEmojiSubject { get; set; }
    public bool? DeleteBulk { get; set; }
    public bool? DeleteFreeMail { get; set; }
    public bool? DeleteRandomCode { get; set; }
    public int? CrowdSize { get; set; }
    public int? BulkLevel { get; set; }
    public List<string>? Brands { get; set; }

    // Windows-only startup/tray fields are intentionally not modeled or imported.
}
