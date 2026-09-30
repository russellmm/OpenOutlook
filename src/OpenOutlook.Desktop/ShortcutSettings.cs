using System.Text.Json;
using Avalonia.Input;

namespace OpenOutlook.Desktop;

public sealed record MailShortcut(string Action, Key Key, KeyModifiers Modifiers);

public sealed record ShortcutSettings
{
    public IReadOnlyList<MailShortcut> Bindings { get; init; } =
        [new("delete", Key.Delete, KeyModifiers.None)];
}

public sealed class ShortcutSettingsStore
{
    public static readonly IReadOnlyList<(string Action, string Label)> AvailableActions =
    [
        ("delete", "Delete selected messages"), ("archive", "Archive selected message"),
        ("read", "Mark as read"), ("unread", "Mark as unread"),
        ("flag", "Flag message"), ("unflag", "Clear flag"),
        ("new", "New email"), ("reply", "Reply"),
        ("replyAll", "Reply all"), ("forward", "Forward")
    ];

    public string Path { get; }

    public ShortcutSettingsStore(string? configRoot = null)
    {
        var root = configRoot ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(root))
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        Path = System.IO.Path.Combine(root, "OpenOutlook", "shortcuts.json");
    }

    public ShortcutSettings Load()
    {
        if (!File.Exists(Path)) return new ShortcutSettings();
        try { return Validate(JsonSerializer.Deserialize<ShortcutSettings>(File.ReadAllText(Path)) ?? new()); }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { return new ShortcutSettings(); }
    }

    public void Save(ShortcutSettings settings)
    {
        var valid = Validate(settings);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".shortcuts-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, valid);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static ShortcutSettings Validate(ShortcutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var actions = AvailableActions.Select(item => item.Action).ToHashSet(StringComparer.Ordinal);
        var usedActions = new HashSet<string>(StringComparer.Ordinal);
        var usedKeys = new HashSet<(Key, KeyModifiers)>();
        var valid = new List<MailShortcut>();
        foreach (var binding in settings.Bindings ?? [])
        {
            var modifiers = binding.Modifiers &
                (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta);
            if (!actions.Contains(binding.Action) || !Enum.IsDefined(binding.Key) ||
                binding.Key == Key.None || modifiers != binding.Modifiers ||
                !usedActions.Add(binding.Action) || !usedKeys.Add((binding.Key, binding.Modifiers))) continue;
            valid.Add(binding);
        }
        return new ShortcutSettings { Bindings = valid };
    }

    public static string Display(MailShortcut? shortcut)
    {
        if (shortcut is null) return "Not assigned";
        var parts = new List<string>();
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Meta");
        parts.Add(shortcut.Key == Key.Delete ? "Del" : shortcut.Key.ToString());
        return string.Join("+", parts);
    }
}
