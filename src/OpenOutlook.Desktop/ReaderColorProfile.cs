namespace OpenOutlook.Desktop;

/// <summary>
/// Whether the embedded reader shows message colours exactly as the sender wrote them (like Outlook and the rest of Windows) or
/// converts them to the monitor's colour profile. Chromium inside WebView2 colour-manages by default; with a monitor profile such
/// as the LG 32UHD144's, white came out cream (#FFFDEF), the grey #D9D9D6 beige (#E3D7C8) and American Express blue #006FCF
/// purple-blue (#4C6CBE), while Outlook showed the colours as written (measured with MailSmoke colorprobe, 2026-10-15).
/// Options > Mail > "Message colors"; WebView2 reads its browser arguments once per process, so a change applies after a restart.
/// </summary>
public static class ReaderColorProfile
{
    public const string EnvironmentVariable = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";
    public const string SrgbFlag = "--force-color-profile=srgb";

    /// <summary>The WebView2 browser arguments to use, given what is already set (by the user or a test) and the option.</summary>
    public static string? BrowserArguments(string? existing, bool colorsAsWritten)
    {
        var current = existing?.Trim() ?? "";
        if (!colorsAsWritten || current.Contains("--force-color-profile", StringComparison.OrdinalIgnoreCase))
            return current.Length == 0 ? null : current;           // the user's own colour-profile flag wins
        return current.Length == 0 ? SrgbFlag : current + " " + SrgbFlag;
    }

    /// <summary>Called at startup on Windows, before any web view exists.</summary>
    public static void Apply(bool colorsAsWritten)
    {
        if (!OperatingSystem.IsWindows()) return;
        var arguments = BrowserArguments(Environment.GetEnvironmentVariable(EnvironmentVariable), colorsAsWritten);
        Environment.SetEnvironmentVariable(EnvironmentVariable, arguments);
    }
}
