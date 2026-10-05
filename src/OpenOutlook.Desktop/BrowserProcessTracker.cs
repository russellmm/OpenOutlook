using System.Diagnostics;
using PuppeteerSharp;

namespace OpenOutlook.Desktop;

/// <summary>
/// The reading pane lays HTML out with a headless Edge / Chrome that Puppeteer starts for each message. When OpenOutlook is closed or killed while one is
/// running (or its start-up times out) that browser, and its helper processes, can be left behind; they pile up and make the next launches time out
/// ("Failed to launch browser"), so the layout falls back to a basic preview. Every launched browser is written to a small file; the next start-up
/// closes the ones that are still alive (same process, same start time) and forgets the rest.
/// </summary>
public static class BrowserProcessTracker
{
    private static readonly object Gate = new();
    private const int MaximumEntries = 500;

    private static string FilePath => Path.Combine(Path.GetTempPath(), "openoutlook-reader", "browser-pids.txt");

    private static string? _lastGood;

    /// <summary>
    /// Starts a headless browser for rendering and remembers it. The browser that worked last is tried first, then every other installed one (Edge 154 stopped
    /// starting headless on the owner's PC while Chrome still does), each twice because a first start can time out on a busy machine.
    /// </summary>
    public static async Task<IBrowser> LaunchAsync(string preferredExecutable)
    {
        var order = new List<string>();
        if (_lastGood is not null && File.Exists(_lastGood)) order.Add(_lastGood);
        order.Add(preferredExecutable);
        order.AddRange(BrowserHtmlRenderer.ExistingBrowsers());
        Exception? last = null;
        foreach (var executable in order.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var browser = await Puppeteer.LaunchAsync(new LaunchOptions
                    {
                        ExecutablePath = executable, Headless = true, Timeout = 15_000,
                        Args = ["--disable-background-networking", "--disable-extensions"]
                    }).ConfigureAwait(false);
                    _lastGood = executable;
                    Remember(browser.Process);
                    return browser;
                }
                catch (ProcessException e)
                {
                    last = e;
                    AppLog.Note("reader", $"{Path.GetFileName(executable)} did not start (attempt {attempt + 1})");
                    if (attempt == 0) await Task.Delay(300).ConfigureAwait(false);
                }
            }
        }
        throw last ?? new ProcessException("No browser could be started.");
    }

    private static void Remember(Process? process)
    {
        if (process is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{process.Id}|{process.StartTime.ToUniversalTime().Ticks}\n");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* tracking is best effort */ }
    }

    /// <summary>Closes browsers that earlier runs left behind. Call once at start-up (OpenOutlook runs once per user, so none of them belongs to a live reading pane).</summary>
    public static int KillLeftovers()
    {
        var killed = 0;
        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath)) return 0;
                var lines = File.ReadAllLines(FilePath).TakeLast(MaximumEntries).ToList();
                foreach (var line in lines)
                {
                    var parts = line.Split('|');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out var pid) || !long.TryParse(parts[1], out var ticks)) continue;
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        var name = p.ProcessName.ToLowerInvariant();
                        if (!(name.Contains("edge") || name.Contains("chrome") || name.Contains("chromium") || name.Contains("brave"))) continue;
                        if (Math.Abs(p.StartTime.ToUniversalTime().Ticks - ticks) > TimeSpan.TicksPerSecond * 2) continue;       // another process reuses the number
                        p.Kill(entireProcessTree: true);
                        killed++;
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* already gone */ }
                }
                File.Delete(FilePath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* nothing to clean */ }
        return killed;
    }
}
