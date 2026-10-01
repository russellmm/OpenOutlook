using System.Text.Json;

namespace OpenOutlook.Desktop;

/// <summary>
/// Persisted folder-pane order: for each parent node, the display order of its children. Entries are
/// opaque stable keys chosen by the tree itself (archive path + folder id, account id + mail-folder id),
/// so a renamed or vanished folder simply reads as an unknown key and falls back to natural order at the
/// end of its siblings rather than moving the wrong row around.
///
/// A malformed or oversized file is discarded whole: losing an arrangement must never stop startup.
/// Writes are temp-file + atomic move, like every other settings store here.
/// </summary>
public sealed class FolderPaneOrderStore
{
    public const int MaximumFileBytes = 256 * 1024;
    public const int MaximumParents = 512;
    public const int MaximumChildrenPerParent = 2048;
    public const int MaximumKeyLength = 1024;

    public string Path { get; }

    public FolderPaneOrderStore(string? configRoot = null)
    {
        var root = configRoot ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(root))
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        Path = System.IO.Path.Combine(root, "OpenOutlook", "folder-order.json");
    }

    private sealed record FileFormat(Dictionary<string, List<string>> Orders);

    // Tolerant of hand-edited files: "orders" and "Orders" both read.
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public Dictionary<string, List<string>> Load()
    {
        try
        {
            if (!File.Exists(Path) || new FileInfo(Path).Length > MaximumFileBytes) return [];
            var parsed = JsonSerializer.Deserialize<FileFormat>(File.ReadAllText(Path), ReadOptions);
            if (parsed?.Orders is null) return [];
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var entry in parsed.Orders.Take(MaximumParents))
            {
                if (!IsUsableParentKey(entry.Key) || entry.Value is null) continue;
                var children = entry.Value.Where(IsUsableChildKey).Take(MaximumChildrenPerParent).ToList();
                if (children.Count > 0) result[entry.Key] = children;
            }
            return result;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        { return []; }
    }

    public void Save(IReadOnlyDictionary<string, List<string>> orders)
    {
        var trimmed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var entry in orders.Take(MaximumParents))
        {
            if (!IsUsableParentKey(entry.Key)) continue;
            var children = entry.Value.Where(IsUsableChildKey).Take(MaximumChildrenPerParent).ToList();
            if (children.Count > 0) trimmed[entry.Key] = children;
        }
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".folder-order-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new FileFormat(trimmed));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // The tree's own root has the empty string as its parent key, so parents may be empty; children may not.
    private static bool IsUsableParentKey(string key) =>
        key.Length <= MaximumKeyLength && !key.Any(char.IsControl);

    private static bool IsUsableChildKey(string key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaximumKeyLength && !key.Any(char.IsControl);

    /// <summary>Position of a key in a saved order; unknown keys rank last, keeping their natural order.</summary>
    public static int RankOf(IReadOnlyList<string>? savedOrder, string? key)
    {
        if (savedOrder is null || key is null) return int.MaxValue;
        for (var i = 0; i < savedOrder.Count; i++)
            if (string.Equals(savedOrder[i], key, StringComparison.Ordinal)) return i;
        return int.MaxValue;
    }

    /// <summary>Stable sort of keys by saved order; unknown keys keep their relative order at the end.</summary>
    public static List<string> SortKeys(IReadOnlyList<string> keys, IReadOnlyList<string>? savedOrder) =>
        [.. keys.OrderBy(key => RankOf(savedOrder, key))];
}
