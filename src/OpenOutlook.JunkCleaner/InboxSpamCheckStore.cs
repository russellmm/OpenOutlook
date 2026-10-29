using System.Text.Json;

namespace OpenOutlook.JunkCleaner;

/// <summary>
/// One account's Inbox spam check (owner 2026-10-29): which rules mark Inbox mail as suspected spam, and the messages the user unticked
/// ("not spam"), so they are not marked again. The rules are separate from the Junk Cleaner's: rules such as Flagged or High importance
/// suit the Junk folder, not the Inbox.
/// </summary>
public sealed record InboxSpamCheckAccount
{
    public required string AccountId { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Also mark mail whose sender matches a Junk Cleaner keyword (junk-keywords.json).</summary>
    public bool UseKeywords { get; init; } = true;
    public JunkRuleOptions Rules { get; init; } = InboxSpamCheckStore.DefaultRules;
    /// <summary>Message ids the user unticked, oldest first, at most <see cref="InboxSpamCheckStore.MaxNotSpam"/>.</summary>
    public IReadOnlyList<string> NotSpam { get; init; } = [];
    /// <summary>Junk Email messages the user unticked (2026-10-29): the Junk Cleaner leaves them; oldest first, at most <see cref="InboxSpamCheckStore.MaxNotSpam"/>.</summary>
    public IReadOnlyList<string> KeepInJunk { get; init; } = [];
}

public sealed record InboxSpamCheckSettings
{
    public IReadOnlyList<InboxSpamCheckAccount> Accounts { get; init; } = [];
}

/// <summary><c>inbox-spam-check.json</c> next to <c>junk-cleaner.json</c>.</summary>
public sealed class InboxSpamCheckStore
{
    public const int MaxNotSpam = 2000;

    /// <summary>
    /// The rules a new account starts with: signs of forged or disguised mail that real Inbox mail rarely shows. No To address and Bcc recipients
    /// catch mass mailings from hijacked real accounts, which pass every sender check (owner's "mailbox cancellation" message, 2026-10-29).
    /// </summary>
    public static readonly JunkRuleOptions DefaultRules = new(
        DeleteMissingTo: true, DeleteWithBcc: true, DeleteUnauthenticated: true, DeleteFakeSenderAddress: true, DeleteImpersonation: true,
        DeleteHiddenText: true, DeleteUsernameInSubject: true, DeleteRandomCode: true);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string ConfigPath { get; }

    public InboxSpamCheckStore(string applicationDirectory)
    {
        if (string.IsNullOrWhiteSpace(applicationDirectory))
            throw new ArgumentException("An explicit application directory is required.", nameof(applicationDirectory));
        ConfigPath = Path.Combine(Path.GetFullPath(applicationDirectory), "inbox-spam-check.json");
    }

    /// <summary>The account's settings; a missing or unreadable file gives the defaults (the check is a suggestion, never an action).</summary>
    public InboxSpamCheckAccount For(string accountId)
    {
        try { return Load().Accounts.FirstOrDefault(a => a.AccountId == accountId) ?? new InboxSpamCheckAccount { AccountId = accountId }; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return new InboxSpamCheckAccount { AccountId = accountId }; }
    }

    public InboxSpamCheckSettings Load()
    {
        if (!File.Exists(ConfigPath)) return new InboxSpamCheckSettings();
        using var stream = File.OpenRead(ConfigPath);
        return JsonSerializer.Deserialize<InboxSpamCheckSettings>(stream, JsonOptions) ?? new InboxSpamCheckSettings();
    }

    public void Save(InboxSpamCheckAccount account)
    {
        var trimmed = account with { NotSpam = Newest(account.NotSpam), KeepInJunk = Newest(account.KeepInJunk) };
        InboxSpamCheckSettings current;
        try { current = Load(); }
        catch (JsonException) { current = new InboxSpamCheckSettings(); }               // a damaged file is replaced: it holds no mail, only choices
        var all = current.Accounts.Where(a => a.AccountId != account.AccountId).Append(trimmed).ToArray();
        var directory = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".inbox-spam-check-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new InboxSpamCheckSettings { Accounts = all }, JsonOptions));
            File.Move(temporaryPath, ConfigPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static IReadOnlyList<string> Newest(IReadOnlyList<string> ids) => ids.Count > MaxNotSpam ? ids.Skip(ids.Count - MaxNotSpam).ToArray() : ids;

    /// <summary>Ticked again = no longer "not spam" (or, in Junk Email, no longer kept); unticked = remembered (newest last).</summary>
    public static InboxSpamCheckAccount WithNotSpam(InboxSpamCheckAccount account, string messageId, bool notSpam, bool junkFolder = false)
    {
        var list = (junkFolder ? account.KeepInJunk : account.NotSpam).Where(id => id != messageId).ToList();
        if (notSpam) list.Add(messageId);
        return junkFolder ? account with { KeepInJunk = list } : account with { NotSpam = list };
    }
}
