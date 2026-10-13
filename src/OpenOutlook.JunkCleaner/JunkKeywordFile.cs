using System.Text.Json;

namespace OpenOutlook.JunkCleaner;

/// <summary>
/// The keyword list of the Junk Cleaner as a small file the user can edit with any text editor: <c>junk-keywords.json</c> in the settings folder, holding
/// one comma-separated line, <c>{ "keywords": "casino, aarp, free spins" }</c>. A JSON array of strings is read too. The list is shared by every account.
/// </summary>
public sealed class JunkKeywordFile
{
    public const string FileName = "junk-keywords.json";
    private const long MaximumBytes = 1024 * 1024;

    public string Path { get; }

    public JunkKeywordFile(string settingsDirectory)
    {
        if (string.IsNullOrWhiteSpace(settingsDirectory)) throw new ArgumentException("A settings directory is required.", nameof(settingsDirectory));
        Path = System.IO.Path.Combine(System.IO.Path.GetFullPath(settingsDirectory), FileName);
    }

    public bool Exists => File.Exists(Path);

    /// <summary>The keywords in the file (trimmed, without duplicates, empty entries dropped). A missing file is an empty list; a file that is not valid JSON throws <see cref="JsonException"/>.</summary>
    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(Path)) return [];
        if (new FileInfo(Path).Length > MaximumBytes) throw new JsonException("The keyword file is too large.");
        using var document = JsonDocument.Parse(File.ReadAllText(Path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        var value = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("keywords", out var k) ? k : root;
        var raw = new List<string>();
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                raw.AddRange((value.GetString() ?? "").Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries));
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) raw.AddRange((item.GetString() ?? "").Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries));
                break;
            default:
                throw new JsonException("The keyword file must hold {\"keywords\": \"word, word, ...\"}.");
        }
        return JunkCleanerSettingsStore.NormalizeKeywords(raw);
    }

    /// <summary>Writes the list as one comma-separated line (atomic replace). Commas inside a keyword cannot be represented and are removed.</summary>
    public void Save(IEnumerable<string> keywords)
    {
        var cleaned = JunkCleanerSettingsStore.NormalizeKeywords(keywords.Select(k => (k ?? "").Replace(",", " ").Replace("\r", " ").Replace("\n", " ")));
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["keywords"] = string.Join(", ", cleaned) },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var temporary = System.IO.Path.Combine(directory, $".junk-keywords-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, json + Environment.NewLine);
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
