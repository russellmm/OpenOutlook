using System.Text.Json;

namespace OpenOutlook.JunkCleaner;

/// <summary>Persists portable settings in a caller-supplied application directory only.</summary>
public sealed class JunkCleanerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string ConfigPath { get; }

    public JunkCleanerSettingsStore(string applicationDirectory)
    {
        if (string.IsNullOrWhiteSpace(applicationDirectory))
            throw new ArgumentException("An explicit application directory is required.", nameof(applicationDirectory));
        ConfigPath = Path.Combine(Path.GetFullPath(applicationDirectory), "junk-cleaner.json");
    }

    /// <summary>Missing config is safe and disabled; invalid config fails rather than silently overwriting it.</summary>
    public JunkCleanerSettings Load()
    {
        if (!File.Exists(ConfigPath)) return new JunkCleanerSettings();
        using var stream = File.OpenRead(ConfigPath);
        return Normalize(JsonSerializer.Deserialize<JunkCleanerSettings>(stream, JsonOptions)
            ?? throw new JsonException("Settings must be a JSON object."));
    }

    public void Save(JunkCleanerSettings settings)
    {
        var normalized = Normalize(settings);
        var directory = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(directory);
        // A unique temporary file in the same directory allows a rename instead of a partial overwrite.
        var temporaryPath = Path.Combine(directory, $".junk-cleaner-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, normalized, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, ConfigPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <summary>Only an explicitly supplied legacy file is read; preview has no persistence side effects.</summary>
    public static LegacyJunkCleanerPreview PreviewLegacyConfig(string legacyConfigPath)
    {
        if (string.IsNullOrWhiteSpace(legacyConfigPath))
            throw new ArgumentException("An explicit legacy config path is required.", nameof(legacyConfigPath));
        using var stream = File.OpenRead(legacyConfigPath);
        var source = JsonSerializer.Deserialize<LegacyJunkCleanerDto>(stream, JsonOptions)
            ?? throw new JsonException("Legacy config must be a JSON object.");
        return new LegacyJunkCleanerPreview
        {
            Keywords = NormalizeKeywords(source.Keywords),
            AlwaysClean = source.AlwaysClean,
            IntervalMinutes = Math.Clamp(source.IntervalMinutes.GetValueOrDefault(5), 1, 60),
            Rules = new JunkRuleOptions(source.DeleteHighImportance, source.DeleteMissingTo,
                source.DeleteOnBehalfOf)
        };
    }

    public static JunkCleanerSettings Normalize(JunkCleanerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Accounts is null) throw new ArgumentException("Accounts cannot be null.", nameof(settings));
        var accounts = new List<JunkCleanerAccountSettings>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in settings.Accounts)
        {
            if (account is null) throw new ArgumentException("Accounts cannot contain null.", nameof(settings));
            var id = ValidateAccountId(account.AccountId);
            if (!ids.Add(id)) throw new ArgumentException("Duplicate account ID.", nameof(settings));
            accounts.Add(account with
            {
                AccountId = id,
                Keywords = NormalizeKeywords(account.Keywords),
                IntervalMinutes = ValidateInterval(account.IntervalMinutes)
            });
        }
        return new JunkCleanerSettings { Accounts = accounts.ToArray() };
    }

    public static string ValidateAccountId(string accountId) =>
        !string.IsNullOrWhiteSpace(accountId) && accountId == accountId.Trim()
            ? accountId
            : throw new ArgumentException("A nonempty, unpadded account ID is required.", nameof(accountId));

    public static int ValidateInterval(int intervalMinutes) =>
        intervalMinutes is >= 1 and <= 60
            ? intervalMinutes
            : throw new ArgumentOutOfRangeException(nameof(intervalMinutes), "Interval must be 1–60 minutes.");

    public static IReadOnlyList<string> NormalizeKeywords(IEnumerable<string>? keywords)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyword in keywords ?? Array.Empty<string>())
        {
            var term = keyword?.Trim();
            if (!string.IsNullOrEmpty(term) && seen.Add(term)) normalized.Add(term);
        }
        return normalized.ToArray();
    }
}
