using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenOutlook.Desktop;

/// <summary>
/// Under WSLg the program cannot read where its window is, so "reopen where it was closed" is not possible. What can be known is which monitor Windows calls the
/// primary one: WSLg writes the monitors it was given (in Windows coordinates, primary at 0,0) to its log. The X display starts at the left-most monitor, so the
/// primary monitor's top-left corner in X coordinates is its Windows position minus the smallest position of all monitors.
/// </summary>
public static class WslWindowPlacement
{
    private static readonly Regex Line = new(@"rdpMonitor\[(?<i>\d+)\]:\s*x:(?<x>-?\d+),\s*y:(?<y>-?\d+),\s*width:(?<w>\d+),\s*height:(?<h>\d+),\s*is_primary:(?<p>\d)", RegexOptions.Compiled);

    /// <summary>The top-left of the primary monitor in X coordinates, from the newest monitor list in the log; null when the log has none.</summary>
    public static (int X, int Y)? PrimaryOrigin(IEnumerable<string> logLines)
    {
        var block = new List<(int X, int Y, bool Primary)>();
        foreach (var text in logLines)
        {
            var m = Line.Match(text);
            if (!m.Success) continue;
            if (m.Groups["i"].Value == "0") block.Clear();                                    // a new list of monitors starts at index 0
            block.Add((int.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture), m.Groups["p"].Value == "1"));
        }
        if (block.Count == 0 || !block.Any(b => b.Primary)) return null;
        var primary = block.First(b => b.Primary);
        return (primary.X - block.Min(b => b.X), primary.Y - block.Min(b => b.Y));
    }

    public static (int X, int Y)? PrimaryOrigin(string logPath = "/mnt/wslg/weston.log")
    {
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int tail = 256 * 1024;                                                       // the newest list is at the end; the file can be large
            if (stream.Length > tail) stream.Seek(-tail, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            return PrimaryOrigin(reader.ReadToEnd().Split('\n'));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
