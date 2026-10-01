using System.Globalization;
using System.Text;

namespace OpenOutlook.Desktop;

/// <summary>
/// A small append-only log, written to the user's data directory.
///
/// The app previously had no logging at all, so when it vanished there was nothing to read afterwards:
/// no stack trace, no indication of which action failed. That made both real crashes and remote
/// diagnosis impossible. This exists to guarantee a last word, not to be a logging framework.
///
/// Writing a log must never itself become the failure, so every operation here swallows its own
/// errors -- a machine whose disk is full or whose home directory is read-only still needs the mail
/// client to keep working.
/// </summary>
public static class AppLog
{
    private const long MaximumFileBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _disabled;

    /// <summary>Directory the log lives in. Separate from tests so they can point it elsewhere.</summary>
    public static string LogDirectory =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "OpenOutlook", "logs");

    public static string? CurrentPath => _path;

    /// <summary>Creates the log file and writes a start marker. Never throws.</summary>
    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            _path = Path.Combine(LogDirectory, "openoutlook.log");
            RotateIfNeeded();
            Append("startup", $"OpenOutlook {Version()} starting, log at {_path}");
        }
        catch (Exception)
        {
            _disabled = true;   // No usable log directory: carry on without one.
        }
    }

    /// <summary>Records something notable, such as a long-running operation finishing.</summary>
    public static void Note(string area, string message) => Append(area, message);

    /// <summary>
    /// Records an exception with its type, message and stack. The first line is kept short so the tail
    /// of the file stays readable while the full detail is still present.
    /// </summary>
    public static void Error(string area, Exception? exception, string? detail = null)
    {
        if (exception is null)
        {
            Append(area, detail ?? "error without an exception object");
            return;
        }
        var builder = new StringBuilder();
        builder.Append(detail is { Length: > 0 } ? detail + ": " : "")
            .Append(exception.GetType().FullName).Append(": ").Append(exception.Message);
        if (exception.InnerException is { } inner)
            builder.Append(" | inner: ").Append(inner.GetType().FullName).Append(": ").Append(inner.Message);
        if (!string.IsNullOrEmpty(exception.StackTrace))
            builder.Append('\n').Append(exception.StackTrace);
        Append(area, builder.ToString());
    }

    private static void Append(string area, string message)
    {
        if (_disabled || _path is null) return;
        try
        {
            lock (Gate)
            {
                RotateIfNeeded();
                File.AppendAllText(_path,
                    $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} " +
                    $"[{area}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { /* Logging must never take the application down. */ }
    }

    private static void RotateIfNeeded()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            if (new FileInfo(_path).Length < MaximumFileBytes) return;
            var previous = _path + ".1";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(_path, previous);
        }
        catch (Exception) { /* Keep appending to the same file rather than losing the log. */ }
    }

    private static string Version()
    {
        try { return typeof(AppLog).Assembly.GetName().Version?.ToString() ?? "unknown"; }
        catch (Exception) { return "unknown"; }
    }
}
