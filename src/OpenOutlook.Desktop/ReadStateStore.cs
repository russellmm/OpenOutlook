using System.Text.Json;

namespace OpenOutlook.Desktop;

/// <summary>
/// Where the Reading Pane (and any explicit mark-read/unread action) records that a message's read
/// state changed locally. PST archives are opened read-only by policy, so the change is remembered
/// in this sidecar instead of written into the archive; connected mailboxes keep their real flags
/// and this only overlays what OpenOutlook decided since the last server sync could carry it.
/// Keys match the folder-order scheme: "pst:&lt;archive path&gt;|&lt;NID hex&gt;" for archive
/// messages, "msg:&lt;account id&gt;/&lt;message id&gt;" for mailbox messages. A missing key means
/// "use whatever the store or server reports".
/// </summary>
public sealed class ReadStateStore
{
    public const int MaximumFileBytes = 1024 * 1024;
    public const int MaximumEntries = 200_000;
    public const int MaximumKeyLength = 2048;

    public string Path { get; }

    public ReadStateStore(string? configRoot = null)
    {
        var root = configRoot ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(root))
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        Path = System.IO.Path.Combine(root, "OpenOutlook", "read-state.json");
    }

    private sealed record FileFormat(Dictionary<string, bool> Overrides);

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads the override map. A corrupt, oversized or unreadable file yields an empty map:
    /// a broken sidecar must never block startup or change what the archives themselves say.</summary>
    public Dictionary<string, bool> Load()
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(Path) || new FileInfo(Path).Length > MaximumFileBytes) return result;
            var loaded = JsonSerializer.Deserialize<FileFormat>(File.ReadAllText(Path), ReadOptions);
            if (loaded?.Overrides is null) return result;
            foreach (var entry in loaded.Overrides)
            {
                if (result.Count >= MaximumEntries) break;
                if (string.IsNullOrEmpty(entry.Key) || entry.Key.Length > MaximumKeyLength) continue;
                if (entry.Key.Any(char.IsControl)) continue;
                result[entry.Key] = entry.Value;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        { return new Dictionary<string, bool>(StringComparer.Ordinal); }
        return result;
    }

    public void Save(IReadOnlyDictionary<string, bool> overrides)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var payload = new FileFormat(new Dictionary<string, bool>(StringComparer.Ordinal));
        foreach (var entry in overrides)
        {
            if (payload.Overrides.Count >= MaximumEntries) break;
            if (string.IsNullOrEmpty(entry.Key) || entry.Key.Length > MaximumKeyLength) continue;
            if (entry.Key.Any(char.IsControl)) continue;
            payload.Overrides[entry.Key] = entry.Value;
        }
        var temporary = System.IO.Path.Combine(directory, $".read-state-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
