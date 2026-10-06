using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>What the Junk Cleaner moved, one line per message, so a wrong keyword can be traced and the mail found again in Deleted Items.</summary>
public sealed class JunkCleanerLog(string path)
{
    private const int MaxBytes = 512 * 1024;
    public string Path { get; } = path;

    public void Append(string address, JunkCleanerRunResult result, bool scheduled)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                File.WriteAllLines(Path, File.ReadAllLines(Path).TakeLast(500));         // keep the newest lines
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var lines = result.Moved.Select(h => $"{now}  {address}  moved \"{Clip(h.Subject, 70)}\" from {Clip(h.Sender, 60)} [{string.Join(", ", h.Reasons)}]").ToList();
            lines.AddRange(result.Failures.Select(f => $"{now}  {address}  problem: {f}"));
            if (lines.Count == 0) lines.Add($"{now}  {address}  {(scheduled ? "scheduled" : "manual")} run: nothing to remove ({result.Scanned} in Junk)");
            File.AppendAllLines(Path, lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public IReadOnlyList<string> Recent(int count = 200)
    {
        try { return File.Exists(Path) ? File.ReadAllLines(Path).TakeLast(count).Reverse().ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
