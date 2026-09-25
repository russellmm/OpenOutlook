using System.Text.Json;

namespace OpenOutlook.Desktop;

/// <summary>Local window layout only; never stores archive paths or message content.</summary>
public sealed record ViewLayoutSettings
{
    public double FolderPaneWeight { get; init; } = 2;
    public double MessagePaneWeight { get; init; } = 5;
    public double ReaderPaneWeight { get; init; } = 4;
    public IReadOnlyList<MessageColumnLayout> Columns { get; init; } = [];
}

public sealed record MessageColumnLayout(int ColumnIndex, int DisplayIndex, double Width, bool IsStar);

public sealed class ViewLayoutSettingsStore
{
    public string Path { get; }

    public ViewLayoutSettingsStore(string? configRoot = null)
    {
        var root = configRoot ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(root))
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        Path = System.IO.Path.Combine(root, "OpenOutlook", "view-layout.json");
    }

    public ViewLayoutSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new ViewLayoutSettings();
            return Validate(JsonSerializer.Deserialize<ViewLayoutSettings>(File.ReadAllText(Path)) ?? new());
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        { return new ViewLayoutSettings(); }
    }

    public void Save(ViewLayoutSettings settings)
    {
        settings = Validate(settings);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".view-layout-{Guid.NewGuid():N}.tmp");
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

    public static ViewLayoutSettings Validate(ViewLayoutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        static bool ValidWeight(double value) => double.IsFinite(value) && value >= 0.2 && value <= 100;
        var weightsValid = ValidWeight(settings.FolderPaneWeight) &&
                           ValidWeight(settings.MessagePaneWeight) && ValidWeight(settings.ReaderPaneWeight);
        var columns = settings.Columns?.ToArray() ?? [];
        var columnsValid = columns.Length == 5 &&
                           columns.Select(column => column.ColumnIndex).Order().SequenceEqual(Enumerable.Range(0, 5)) &&
                           columns.Select(column => column.DisplayIndex).Order().SequenceEqual(Enumerable.Range(0, 5)) &&
                           columns.All(column => double.IsFinite(column.Width) &&
                               (column.IsStar ? column.Width is >= 0.2 and <= 10 : column.Width is >= 30 and <= 1000));
        return new ViewLayoutSettings
        {
            FolderPaneWeight = weightsValid ? settings.FolderPaneWeight : 2,
            MessagePaneWeight = weightsValid ? settings.MessagePaneWeight : 5,
            ReaderPaneWeight = weightsValid ? settings.ReaderPaneWeight : 4,
            Columns = columnsValid ? columns : []
        };
    }
}
