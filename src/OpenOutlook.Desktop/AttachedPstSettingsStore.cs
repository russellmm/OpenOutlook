using System.Text.Json;

namespace OpenOutlook.Desktop;

/// <summary>Paths to archives attached by this user. Archives themselves remain read-only.</summary>
public sealed class AttachedPstSettingsStore
{
    public string Path { get; }

    public AttachedPstSettingsStore(string? configRoot = null)
    {
        var root = configRoot ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(root))
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        Path = System.IO.Path.Combine(root, "OpenOutlook", "attached-psts.json");
    }

    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(Path)) return [];
        if (new FileInfo(Path).Length > 64 * 1024)
            throw new InvalidDataException("Attached PST settings are too large.");
        try
        {
            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path)) ??
                throw new InvalidDataException("Attached PST settings are invalid.");
            Validate(paths);
            return paths;
        }
        catch (JsonException) { throw new InvalidDataException("Attached PST settings are invalid."); }
    }

    public void Add(string path)
    {
        ValidatePath(path);
        var paths = Load().ToList();
        if (paths.Contains(path, StringComparer.Ordinal)) return;
        paths.Add(path);
        Save(paths);
    }

    public void Remove(string path)
    {
        ValidatePath(path);
        var paths = Load();
        if (!paths.Contains(path, StringComparer.Ordinal)) return;
        Save(paths.Where(existing => !string.Equals(existing, path, StringComparison.Ordinal)).ToArray());
    }

    public void Save(IReadOnlyList<string> paths)
    {
        Validate(paths);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = System.IO.Path.Combine(directory, $".attached-psts-{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
            };
            if (OperatingSystem.IsLinux())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, paths);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count > 64 || paths.Distinct(StringComparer.Ordinal).Count() != paths.Count)
            throw new InvalidDataException("Attached PST settings exceed the supported archive limit.");
        foreach (var path in paths) ValidatePath(path);
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 ||
            path.Any(char.IsControl) || !System.IO.Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Attached PST settings contain an invalid path.");
    }
}
