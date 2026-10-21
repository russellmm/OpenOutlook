using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// What the Junk Cleaner moved, one line per message, so a wrong keyword can be traced and the mail found again in Deleted Items. It never grows
/// without limit: every write keeps at most the newest <see cref="MaxEntries"/> lines and <see cref="MaxBytes"/> bytes, the oldest lines going first.
/// </summary>
public sealed class JunkCleanerLog(string path)
{
    public const int MaxBytes = 256 * 1024;
    public const int MaxEntries = 1500;
    public string Path { get; } = path;

    public void Append(string address, JunkCleanerRunResult result, bool scheduled)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var lines = result.Moved.Select(h => $"{now}  {address}  moved \"{Clip(h.Subject, 70)}\" from {Clip(h.Sender, 60)} [{string.Join(", ", h.Reasons)}]").ToList();
            lines.AddRange(result.Failures.Select(f => $"{now}  {address}  problem: {f}"));
            if (lines.Count == 0) lines.Add($"{now}  {address}  {(scheduled ? "scheduled" : "manual")} run: nothing to remove ({result.Scanned} in Junk)");
            var all = (File.Exists(Path) ? File.ReadAllLines(Path) : []).Concat(lines).TakeLast(MaxEntries).ToList();
            var bytes = all.Sum(l => System.Text.Encoding.UTF8.GetByteCount(l) + Environment.NewLine.Length);
            var drop = 0;
            while (bytes > MaxBytes && drop < all.Count - 1) bytes -= System.Text.Encoding.UTF8.GetByteCount(all[drop++]) + Environment.NewLine.Length;
            File.WriteAllLines(Path, all.Skip(drop));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }   // the history must never fail a run whose messages have already moved
    }

    public IReadOnlyList<string> Recent(int count = 200)
    {
        try { return File.Exists(Path) ? File.ReadAllLines(Path).TakeLast(count).Reverse().ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Shortens without splitting an emoji (a surrogate pair): half of one cannot be written as UTF-8 and made the whole write fail.</summary>
    internal static string Clip(string s, int max)
    {
        if (s.Length > max) s = s[..(char.IsHighSurrogate(s[max - 1]) ? max - 1 : max)] + "…";
        var b = new System.Text.StringBuilder(s.Length);       // a lone surrogate from the server itself becomes U+FFFD
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { b.Append(s, i++, 2); continue; }
            b.Append(char.IsSurrogate(s[i]) ? '\uFFFD' : s[i]);
        }
        return b.ToString();
    }
}
