using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace OpenOutlook.Desktop;

/// <summary>
/// Reads the program's log for the viewer: the current file and the one it rotated to (<c>openoutlook.log.1</c>), as entries (an entry starts with
/// "yyyy-MM-dd HH:mm:ss [area]"; stack-trace lines belong to the entry above them), newest first, optionally filtered. Kept free of controls so it
/// can be tested without a window.
/// </summary>
public static class AppLogReader
{
    /// <summary>Words that mark an entry as a problem for the "Only problems" filter.</summary>
    private static readonly string[] ProblemWords = ["exception", "error", "failed", "could not", "not loaded", "not shown", "refused", "problem"];

    public sealed record Result(IReadOnlyList<string> Entries, int Total);

    public static Result Read(string logPath, string? filter = null, bool problemsOnly = false, int max = 2000)
    {
        var lines = new List<string>();
        foreach (var path in new[] { logPath + ".1", logPath })                       // oldest file first, so the order is chronological before reversing
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line) lines.Add(line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var entries = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (IsEntryStart(line) && current.Length > 0) { entries.Add(current.ToString()); current.Clear(); }
            if (current.Length > 0) current.Append(Environment.NewLine);
            current.Append(line);
        }
        if (current.Length > 0) entries.Add(current.ToString());
        var term = filter?.Trim();
        var matching = entries.Where(e =>
            (string.IsNullOrEmpty(term) || e.Contains(term, StringComparison.OrdinalIgnoreCase)) &&
            (!problemsOnly || ProblemWords.Any(w => e.Contains(w, StringComparison.OrdinalIgnoreCase)))).ToList();
        matching.Reverse();
        return new Result(matching.Take(max).ToList(), matching.Count);
    }

    /// <summary>"2026-10-16 13:22:49 [reader] ..."</summary>
    private static bool IsEntryStart(string line) =>
        line.Length >= 21 && char.IsDigit(line[0]) && line[4] == '-' && line[7] == '-' && line[10] == ' ' && line[13] == ':' && line[16] == ':' && line[19] == ' ';
}

/// <summary>File > Account > View log: the program's log, newest first, with a search box and an "Only problems" filter.</summary>
internal sealed class LogViewerWindow : Window
{
    private readonly string _path;
    private readonly Action<string> _openFolder;
    private readonly TextBox _text = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        FontFamily = new Avalonia.Media.FontFamily("Consolas,Menlo,monospace"), FontSize = 12.5
    };
    private readonly TextBox _find = new() { Watermark = "Find in the log", MinWidth = 260 };
    private readonly CheckBox _problems = new() { Content = "Only problems", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };

    public LogViewerWindow(string logPath, Action<string> openFolder)
    {
        _path = logPath;
        _openFolder = openFolder;
        Title = "OpenOutlook log";
        Width = 900; Height = 600; MinWidth = 520; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var refresh = new Button { Content = "Refresh", MinWidth = 90 };
        var copy = new Button { Content = "Copy", MinWidth = 90 };
        var folder = new Button { Content = "Open log folder", MinWidth = 120 };
        var close = new Button { Content = "Close", MinWidth = 90 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 10), Children = { _find, _problems } };
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { refresh, copy, folder, close } };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);
        var panel = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        panel.Children.Add(top);
        panel.Children.Add(bottom);
        panel.Children.Add(_text);
        Content = panel;
        _find.TextChanged += (_, _) => Load();
        _problems.IsCheckedChanged += (_, _) => Load();
        refresh.Click += (_, _) => Load();
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard && !string.IsNullOrEmpty(_text.Text)) { await clipboard.SetTextAsync(_text.Text); _status.Text = "Copied to the clipboard."; }
        };
        folder.Click += (_, _) => _openFolder(_path);
        close.Click += (_, _) => Close();
        Opened += (_, _) => Load();
    }

    /// <summary>The text shown (for tests).</summary>
    internal string ShownText => _text.Text ?? "";

    internal void Load()
    {
        var result = AppLogReader.Read(_path, _find.Text, _problems.IsChecked == true);
        _text.Text = result.Entries.Count == 0
            ? (File.Exists(_path) ? "Nothing in the log matches." : "There is no log yet.")
            : string.Join(Environment.NewLine, result.Entries);
        _text.CaretIndex = 0;
        _status.Text = result.Total == result.Entries.Count
            ? $"{result.Total:N0} entr{(result.Total == 1 ? "y" : "ies")}, newest first - {_path}"
            : $"Newest {result.Entries.Count:N0} of {result.Total:N0} entries - {_path}";
    }
}
