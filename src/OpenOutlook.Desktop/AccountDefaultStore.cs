using System.Text.Json;

namespace OpenOutlook.Desktop;

/// <summary>Which connected account is the default (Account Settings > Email > Set as Default): the default sender of new messages.</summary>
public sealed class AccountDefaultStore
{
    public string Path { get; }

    public AccountDefaultStore(string? configRoot = null)
    {
        var root = configRoot ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(root))
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        Path = System.IO.Path.Combine(root, "OpenOutlook", "default-account.json");
    }

    private sealed record Doc(string? AccountId);

    public string? Load()
    {
        try
        {
            if (!File.Exists(Path) || new FileInfo(Path).Length > 4096) return null;
            var id = JsonSerializer.Deserialize<Doc>(File.ReadAllText(Path))?.AccountId;
            return string.IsNullOrWhiteSpace(id) || id.Length > 256 ? null : id;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Save(string? accountId)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Doc(accountId)));
        File.Move(temp, Path, overwrite: true);
    }
}
