using System.Text.Json;

namespace OpenOutlook.Desktop;

/// <summary>Presentation preferences only; never stores accounts, tokens or message data.</summary>
public sealed record AppearanceSettings
{
    public bool DarkMode { get; init; }
    public string Accent { get; init; } = "Blue";
    public int TextSize { get; init; } = 15;
}

public sealed class AppearanceSettingsStore
{
    public string Path { get; }

    public AppearanceSettingsStore(string? configRoot = null)
    {
        var root = configRoot ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(root))
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        Path = System.IO.Path.Combine(root, "OpenOutlook", "appearance.json");
    }

    public AppearanceSettings Load()
    {
        if (!File.Exists(Path)) return new AppearanceSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppearanceSettings>(File.ReadAllText(Path));
            return Validate(settings ?? new AppearanceSettings());
        }
        catch (JsonException) { return new AppearanceSettings(); }
        catch (IOException) { return new AppearanceSettings(); }
        catch (UnauthorizedAccessException) { return new AppearanceSettings(); }
    }

    public void Save(AppearanceSettings settings)
    {
        settings = Validate(settings);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".appearance-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static AppearanceSettings Validate(AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            Accent = settings.Accent is "Blue" or "Green" or "Purple" or "Orange" ? settings.Accent : "Blue",
            TextSize = Math.Clamp(settings.TextSize, 12, 23)
        };
    }
}
