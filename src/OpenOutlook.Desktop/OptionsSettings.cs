using System.Text.Json;

namespace OpenOutlook.Desktop;

/// <summary>
/// Persisted state for the classic File > Options pages (Mail, Reading Pane, Editor/Proofing).
/// The controls mirror Outlook 2024's dialog exactly; which settings actually change behaviour in
/// OpenOutlook grows with the features they belong to. Until then the values are stored and restored
/// faithfully so the dialog behaves like a real one -- OK keeps changes, Cancel discards them, and
/// everything survives a restart.
/// </summary>
public sealed record OptionsSettings
{
    // Data files (PST)
    /// <summary>Copy an archive to &lt;file&gt;.bak when it is opened for editing (off by default; edits are journaled and crash-safe).</summary>
    public bool PstBackupBeforeEditing { get; init; }

    // Compose messages
    public string ComposeFormat { get; init; } = "Rich Text";
    public bool CheckSpellingBeforeSending { get; init; } = true;
    public bool IgnoreOriginalInReply { get; init; } = true;

    // Message arrival
    public bool PlaySound { get; init; }
    public bool ChangeMousePointer { get; init; }
    public bool TaskbarEnvelopeIcon { get; init; } = true;
    public bool DesktopAlert { get; init; }

    // Conversation Clean Up
    public bool CleanUpRecreateHierarchy { get; init; }
    public bool CleanUpDontMoveUnread { get; init; }
    public bool CleanUpDontMoveCategorized { get; init; } = true;
    public bool CleanUpDontMoveFlagged { get; init; } = true;
    public bool CleanUpDontMoveSigned { get; init; } = true;
    public bool CleanUpReplyKeepsOriginal { get; init; } = true;

    // Replies and forwards
    public bool ShowSuggestedReplies { get; init; }
    public bool OpenRepliesInNewWindow { get; init; }
    public bool CloseOriginalWhenReplying { get; init; }
    public string WhenReplying { get; init; } = "Include original message text";
    public string WhenForwarding { get; init; } = "Include original message text";

    // Save messages
    public bool AutoSaveUnsent { get; init; } = true;
    public int AutoSaveMinutes { get; init; } = 3;
    public string UnsentSaveFolder { get; init; } = "Drafts";
    public bool ReplySavesToSameFolder { get; init; }
    public bool SaveForwardedMessages { get; init; } = true;
    public bool SaveSentCopies { get; init; } = true;
    public bool UseUnicodeFormat { get; init; } = true;

    // Send messages
    public string DefaultImportance { get; init; } = "Normal";
    public string DefaultSensitivity { get; init; } = "Normal";
    public bool MarkExpired { get; init; }
    public int ExpireAfterDays { get; init; }
    public bool AlwaysUseDefaultAccount { get; init; }
    public bool CommasSeparateRecipients { get; init; }
    public bool AutomaticNameChecking { get; init; } = true;
    public bool DeleteMeetingResponses { get; init; } = true;
    public bool CtrlEnterSends { get; init; } = true;
    public bool UseAutoCompleteList { get; init; } = true;
    public bool WarnMissingAttachment { get; init; } = true;
    public bool MentionSuggestions { get; init; } = true;

    // Tracking
    public bool RequestDeliveryReceipt { get; init; }
    public bool RequestReadReceipt { get; init; }
    public string ReadReceiptPolicy { get; init; } = "Never send a read receipt";
    public bool AutoProcessMeetings { get; init; } = true;
    public bool AutoUpdateSentItem { get; init; } = true;
    public bool UpdateThenDeleteResponses { get; init; }
    public bool MoveReceiptsAfterUpdate { get; init; }

    // Message format
    public bool UseCss { get; init; } = true;
    public bool ReduceMessageSize { get; init; } = true;
    public bool UuencodeAttachments { get; init; }
    public int WrapAtCharacter { get; init; } = 76;
    public bool RemoveExtraLineBreaks { get; init; } = true;
    public string RichTextToInternet { get; init; } = "Convert to HTML format";

    // Other
    public bool ShowPasteOptions { get; init; } = true;
    public bool ShowNextPreviousLinks { get; init; } = true;
    public bool DontAutoExpandConversations { get; init; }
    public string AfterMoveOrDelete { get; init; } = "return to the current folder";
    public bool MarkReadWhenDeleted { get; init; }
    public bool HighlightFlaggedItems { get; init; } = true;

    // Reading Pane dialog
    public bool ReadPaneMarkOnView { get; init; } = true;
    public int ReadPaneWaitSeconds { get; init; } = 2;
    public bool ReadPaneMarkOnSelectionChange { get; init; }
    public bool ReadPaneSingleKeyReading { get; init; } = true;
    public bool ReadPaneFullScreenPortrait { get; init; } = true;
    public bool ReadPaneAlwaysPreview { get; init; }

    // Editor Options (Proofing)
    public bool SpellIgnoreUppercase { get; init; } = true;
    public bool SpellIgnoreNumbers { get; init; } = true;
    public bool SpellIgnoreInternetAddresses { get; init; } = true;
    public bool SpellFlagRepeatedWords { get; init; } = true;
    public bool SpellFrenchAccentedUppercase { get; init; }
    public bool SpellMainDictionaryOnly { get; init; }
    public string FrenchMode { get; init; } = "Traditional and new spellings";
    public string SpanishMode { get; init; } = "Tuteo verb forms only";
    public bool SpellCheckAsYouType { get; init; } = true;
    public bool SpellMarkGrammarErrors { get; init; } = true;
    public bool SpellFrequentlyConfusedWords { get; init; } = true;
    public bool SpellGrammarWithSpelling { get; init; }
}

/// <summary>options.json under the OpenOutlook XDG configuration folder, written atomically.
/// A corrupt file yields defaults: a broken settings file must never stop startup.</summary>
public sealed class OptionsSettingsStore
{
    public const int MaximumFileBytes = 256 * 1024;

    public string Path { get; }

    public OptionsSettingsStore(string? configRoot = null)
    {
        var root = configRoot ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(root))
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        Path = System.IO.Path.Combine(root, "OpenOutlook", "options.json");
    }

    public OptionsSettings Load()
    {
        try
        {
            if (!File.Exists(Path) || new FileInfo(Path).Length > MaximumFileBytes) return new OptionsSettings();
            var loaded = JsonSerializer.Deserialize<OptionsSettings>(File.ReadAllText(Path));
            return loaded is null ? new OptionsSettings() : Sanitized(loaded);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        { return new OptionsSettings(); }
    }

    public void Save(OptionsSettings settings)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".options-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, Sanitized(settings));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Numeric fields are clamped to the ranges the dialog offers; strings are length-capped.
    private static OptionsSettings Sanitized(OptionsSettings s) => s with
    {
        ComposeFormat = Cap(s.ComposeFormat, "Rich Text"),
        AutoSaveMinutes = Math.Clamp(s.AutoSaveMinutes, 1, 99),
        UnsentSaveFolder = Cap(s.UnsentSaveFolder, "Drafts"),
        DefaultImportance = Cap(s.DefaultImportance, "Normal"),
        DefaultSensitivity = Cap(s.DefaultSensitivity, "Normal"),
        ExpireAfterDays = Math.Clamp(s.ExpireAfterDays, 0, 3650),
        WhenReplying = Cap(s.WhenReplying, "Include original message text"),
        WhenForwarding = Cap(s.WhenForwarding, "Include original message text"),
        ReadReceiptPolicy = Cap(s.ReadReceiptPolicy, "Never send a read receipt"),
        WrapAtCharacter = Math.Clamp(s.WrapAtCharacter, 25, 700),
        RichTextToInternet = Cap(s.RichTextToInternet, "Convert to HTML format"),
        AfterMoveOrDelete = Cap(s.AfterMoveOrDelete, "return to the current folder"),
        ReadPaneWaitSeconds = Math.Clamp(s.ReadPaneWaitSeconds, 0, 300),
        FrenchMode = Cap(s.FrenchMode, "Traditional and new spellings"),
        SpanishMode = Cap(s.SpanishMode, "Tuteo verb forms only"),
    };

    private static string Cap(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 120 || value.Any(char.IsControl) ? fallback : value;
}
