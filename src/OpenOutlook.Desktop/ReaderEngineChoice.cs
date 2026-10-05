namespace OpenOutlook.Desktop;

/// <summary>
/// Which reading-pane renderer a message starts on: the embedded web view (real HTML in the pane - selectable text, links, zoom, no
/// separate process; what the GTK client did with WebKit) or the browser snapshot (a headless Chromium picture with an invisible text
/// layer). The embedded view paints correctly on Windows, macOS and X11. Under Wayland it loads the page but paints nothing, so
/// Wayland sessions start on the snapshot. OPENOUTLOOK_READER=embedded|snapshot forces the choice. If the embedded view cannot show
/// a message (probe fails, message too large) the pane falls back to the snapshot on its own.
/// </summary>
public static class ReaderEngineChoice
{
    public static bool PreferEmbedded() => PreferEmbedded(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(),
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"), Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
        Environment.GetEnvironmentVariable("OPENOUTLOOK_READER"));

    public static bool PreferEmbedded(bool windows, bool mac, string? waylandDisplay, string? sessionType, string? forced)
    {
        if (string.Equals(forced, "embedded", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(forced, "snapshot", StringComparison.OrdinalIgnoreCase)) return false;
        if (windows || mac) return true;
        var wayland = !string.IsNullOrEmpty(waylandDisplay) ||
                      string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase);
        return !wayland;
    }
}
