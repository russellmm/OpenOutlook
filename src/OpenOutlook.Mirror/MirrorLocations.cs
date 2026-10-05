using System.Text;
using System.Text.Json;

namespace OpenOutlook.Mirror;

/// <summary>Per-account mirror choices. Defaults follow the owner's decisions: mirroring on, last 12 months, attachments up to 25 MB.</summary>
public sealed record MirrorAccountSettings(
    bool Enabled = true,
    string? FolderOverride = null,
    int KeepMonths = 12,                       // 0 = everything
    long MaxAttachmentBytes = 25L * 1024 * 1024);

public sealed record MirrorSettings(string? DefaultFolder, Dictionary<string, MirrorAccountSettings> Accounts)
{
    public static MirrorSettings Empty => new(null, new Dictionary<string, MirrorAccountSettings>(StringComparer.Ordinal));
    public MirrorAccountSettings For(string accountId) => Accounts.TryGetValue(accountId, out var s) ? s : new MirrorAccountSettings();
}

/// <summary>Reads and writes <c>mirror-settings.json</c> next to the app's other settings (atomic replace, bounded size, corrupt file = defaults).</summary>
public sealed class MirrorSettingsStore
{
    private const long MaximumFileBytes = 256 * 1024;
    public string Path { get; }

    public MirrorSettingsStore(string? configRoot = null)
    {
        var root = configRoot ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(root))
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        Path = System.IO.Path.Combine(root, "OpenOutlook", "mirror-settings.json");
    }

    public MirrorSettings Load()
    {
        try
        {
            if (!File.Exists(Path) || new FileInfo(Path).Length > MaximumFileBytes) return MirrorSettings.Empty;
            var loaded = JsonSerializer.Deserialize<MirrorSettings>(File.ReadAllText(Path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (loaded is null) return MirrorSettings.Empty;
            var accounts = new Dictionary<string, MirrorAccountSettings>(StringComparer.Ordinal);
            foreach (var (id, s) in loaded.Accounts ?? [])
                if (!string.IsNullOrEmpty(id) && id.Length <= 256)
                    accounts[id] = s with { KeepMonths = Math.Clamp(s.KeepMonths, 0, 600), MaxAttachmentBytes = Math.Clamp(s.MaxAttachmentBytes, 0, 1L << 31) };
            return new MirrorSettings(string.IsNullOrWhiteSpace(loaded.DefaultFolder) ? null : loaded.DefaultFolder, accounts);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return MirrorSettings.Empty; }
    }

    public void Save(MirrorSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
    }
}

public enum MirrorFolderProblem { None, NotAbsolute, NotWritable, NotEnoughSpace, Missing }

/// <summary>Result of checking a folder the user picked: problems block the choice, warnings are shown but allowed.</summary>
public sealed record MirrorFolderCheck(MirrorFolderProblem Problem, IReadOnlyList<string> Warnings)
{
    public bool Ok => Problem == MirrorFolderProblem.None;
}

/// <summary>Where mirror PST files live: OS-specific default, user-chosen default folder, per-account override, file-name safety, folder validation.</summary>
public static class MirrorLocations
{
    private static readonly string[] CloudFolderMarkers = ["OneDrive", "Dropbox", "Google Drive", "GoogleDrive", "iCloud", "Nextcloud", "ownCloud", "pCloud", "Sync", "MEGA", "Box"];
    private static readonly string[] ReservedNames = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>Windows: %LOCALAPPDATA%\OpenOutlook\Mail. Linux and macOS: $XDG_DATA_HOME/openoutlook/mail (~/.local/share/openoutlook/mail).</summary>
    public static string DefaultRoot(string? dataHomeOverride = null)
    {
        if (OperatingSystem.IsWindows() && dataHomeOverride is null)
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenOutlook", "Mail");
        var home = dataHomeOverride ?? Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return System.IO.Path.Combine(home, "openoutlook", "mail");
    }

    /// <summary>The folder holding an account's mirror: its override, else the chosen default folder, else the OS default.</summary>
    public static string FolderFor(MirrorSettings settings, string accountId, string? dataHomeOverride = null)
    {
        var account = settings.For(accountId);
        if (!string.IsNullOrWhiteSpace(account.FolderOverride)) return account.FolderOverride;
        return !string.IsNullOrWhiteSpace(settings.DefaultFolder) ? settings.DefaultFolder : DefaultRoot(dataHomeOverride);
    }

    public static string PstPathFor(MirrorSettings settings, string accountId, string address, string? dataHomeOverride = null) =>
        System.IO.Path.Combine(FolderFor(settings, accountId, dataHomeOverride), SafeFileName(address) + ".pst");

    /// <summary>An account address as a file name that is valid on Windows and Linux (no path characters, no reserved device names, no trailing dots or spaces).</summary>
    public static string SafeFileName(string address)
    {
        var sb = new StringBuilder();
        foreach (var c in address.Trim())
            sb.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(c) ? '_' : c);
        var name = sb.ToString().TrimEnd('.', ' ');
        while (name.StartsWith('.')) name = name[1..];
        if (name.Length == 0) name = "account";
        if (name.Length > 120) name = name[..120];
        if (ReservedNames.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase)) name = "_" + name;
        return name;
    }

    /// <summary>Checks a folder for use as a mirror location. <paramref name="needBytes"/> is the expected size (0 = unknown).</summary>
    public static MirrorFolderCheck Check(string folder, long needBytes = 0)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(folder) || !System.IO.Path.IsPathFullyQualified(folder))
            return new MirrorFolderCheck(MirrorFolderProblem.NotAbsolute, warnings);
        string full;
        try { full = System.IO.Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new MirrorFolderCheck(MirrorFolderProblem.NotAbsolute, warnings); }
        try
        {
            Directory.CreateDirectory(full);
            var probe = System.IO.Path.Combine(full, ".openoutlook-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        { return new MirrorFolderCheck(MirrorFolderProblem.NotWritable, warnings); }

        try
        {
            var root = System.IO.Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.DriveType == DriveType.Network || full.StartsWith(@"\\", StringComparison.Ordinal))
                    warnings.Add("This folder is on a network share. A mailbox copy is faster and safer on a local disk.");
                if (drive.DriveType == DriveType.Removable)
                    warnings.Add("This folder is on a removable drive. Mail will not be available while the drive is disconnected.");
                var need = Math.Max(needBytes, 0) + Math.Max(needBytes, 0) / 5;
                if (needBytes > 0 && drive.AvailableFreeSpace < need)
                    return new MirrorFolderCheck(MirrorFolderProblem.NotEnoughSpace, warnings);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { /* free space unknown: allow */ }

        var segments = full.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (segments.Any(s => CloudFolderMarkers.Any(m => s.StartsWith(m, StringComparison.OrdinalIgnoreCase))))
            warnings.Add("This folder looks like it is synchronised by a cloud service. Another program syncing a mailbox file while OpenOutlook uses it can corrupt it.");
        return new MirrorFolderCheck(MirrorFolderProblem.None, warnings);
    }
}
