namespace OpenOutlook.Desktop;

/// <summary>
/// Under WSL the Windows drives are mounted below /mnt (c, d, ..., and mapped network drives once mounted), but the GTK file dialog only lists "Computer /",
/// so the drives are hard to find. This adds them to the dialog's sidebar (the GTK bookmarks file) at start-up, without touching any other bookmark.
/// </summary>
public static class WslDriveBookmarks
{
    public static bool IsWsl()
    {
        try { return OperatingSystem.IsLinux() && File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The bookmark lines to add: one per mounted single-letter drive that is not bookmarked yet.</summary>
    public static IReadOnlyList<string> Missing(IEnumerable<string> existingLines, IEnumerable<string> driveLetters)
    {
        var known = existingLines.Select(l => l.Split(' ')[0].TrimEnd('/')).ToHashSet(StringComparer.Ordinal);
        return driveLetters.Select(letter => letter.ToLowerInvariant()).Distinct()
            .Where(letter => !known.Contains("file:///mnt/" + letter))
            .Select(letter => $"file:///mnt/{letter} {letter.ToUpperInvariant()}: (Windows drive)").ToList();
    }

    public static int Ensure(string? mountRoot = null, string? configRoot = null)
    {
        var added = 0;
        try
        {
            var root = mountRoot ?? "/mnt";
            var letters = Directory.EnumerateDirectories(root).Select(Path.GetFileName)
                .Where(n => n is { Length: 1 } && char.IsLetter(n[0])).Select(n => n!).ToList();
            var config = configRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            foreach (var gtk in new[] { "gtk-3.0", "gtk-4.0" })
            {
                var file = Path.Combine(config, gtk, "bookmarks");
                var existing = File.Exists(file) ? File.ReadAllLines(file) : [];
                var missing = Missing(existing, letters);
                if (missing.Count == 0) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllLines(file, missing);
                added += missing.Count;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the dialog still works without the shortcuts */ }
        return added;
    }
}
