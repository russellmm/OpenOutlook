using OpenOutlook.Desktop;
using Xunit;

namespace OpenOutlook.Tests;

public sealed class BundledBrowserTests
{
    [Fact]
    public void The_bundled_browser_is_looked_up_beside_the_application_and_listed_first()
    {
        var bundled = BrowserHtmlRenderer.BundledBrowsers().ToList();
        Assert.NotEmpty(bundled);
        var expectedName = OperatingSystem.IsWindows() ? "chrome-headless-shell.exe" : "chrome-headless-shell";
        Assert.All(bundled, p => Assert.Equal(Path.Combine("chromium", expectedName), p[(Path.GetDirectoryName(Path.GetDirectoryName(p))!.Length + 1)..]));
        var existing = BrowserHtmlRenderer.ExistingBrowsers();
        var first = bundled.FirstOrDefault(File.Exists);
        if (first is not null) Assert.Equal(first, existing[0], ignoreCase: true);       // a build that carries the browser prefers it
    }

    [Fact]
    public void Old_browser_profile_folders_are_removed_and_other_folders_are_left_alone()
    {
        var root = Path.Combine(Path.GetTempPath(), "oo-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var stale = Directory.CreateDirectory(Path.Combine(root, "abcd1234.xyz"));       // looks like a Puppeteer profile and is old
            File.WriteAllText(Path.Combine(stale.FullName, "Local State"), "{}");
            stale.LastWriteTimeUtc = DateTime.UtcNow.AddHours(-3);
            var fresh = Directory.CreateDirectory(Path.Combine(root, "efgh5678.abc"));       // same name pattern but just written: a browser may be using it
            File.WriteAllText(Path.Combine(fresh.FullName, "Local State"), "{}");
            var other = Directory.CreateDirectory(Path.Combine(root, "mydata.old"));         // not a profile name
            File.WriteAllText(Path.Combine(other.FullName, "Local State"), "{}");
            other.LastWriteTimeUtc = DateTime.UtcNow.AddHours(-3);
            var notBrowser = Directory.CreateDirectory(Path.Combine(root, "ijkl9012.def"));  // right name, no browser profile inside
            notBrowser.LastWriteTimeUtc = DateTime.UtcNow.AddHours(-3);
            Assert.Equal(1, OpenOutlook.Desktop.BrowserProcessTracker.DeleteStaleProfiles(root));
            Assert.False(Directory.Exists(stale.FullName));
            Assert.True(Directory.Exists(fresh.FullName));
            Assert.True(Directory.Exists(other.FullName));
            Assert.True(Directory.Exists(notBrowser.FullName));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WSL_drives_are_added_to_the_file_dialog_sidebar_once_and_other_bookmarks_stay()
    {
        var root = Path.Combine(Path.GetTempPath(), "oo-bookmarks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mnt = Directory.CreateDirectory(Path.Combine(root, "mnt")).FullName;
            foreach (var name in new[] { "c", "x", "wsl", "wslg" }) Directory.CreateDirectory(Path.Combine(mnt, name));
            var cfg = Path.Combine(root, "config");
            Directory.CreateDirectory(Path.Combine(cfg, "gtk-3.0"));
            File.WriteAllText(Path.Combine(cfg, "gtk-3.0", "bookmarks"), string.Join((char)10, "file:///home/me/Documents Docs", "file:///mnt/c My C") + (char)10);
            Assert.Equal(3, OpenOutlook.Desktop.WslDriveBookmarks.Ensure(mnt, cfg));            // x for gtk-3.0, c and x for gtk-4.0
            var lines = File.ReadAllLines(Path.Combine(cfg, "gtk-3.0", "bookmarks"));
            Assert.Contains("file:///home/me/Documents Docs", lines);
            Assert.Contains("file:///mnt/c My C", lines);                                      // the owner's own name is kept
            Assert.Single(lines, l => l.StartsWith("file:///mnt/x"));
            Assert.DoesNotContain(lines, l => l.Contains("/mnt/wsl"));                         // only single-letter drives
            Assert.Equal(0, OpenOutlook.Desktop.WslDriveBookmarks.Ensure(mnt, cfg));            // a second start adds nothing
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void The_primary_monitor_origin_comes_from_the_newest_monitor_list_of_the_WSLg_log()
    {
        string[] log =
        [
            "[22:38:47.859] 	rdpMonitor[0]: x:0, y:0, width:3840, height:2160, is_primary:1",          // an older session with one monitor
            "[22:42:47.124] 	rdpMonitor[0]: x:-3840, y:0, width:3840, height:2160, is_primary:0",
            "[22:42:47.124] 	rdpMonitor[1]: x:0, y:0, width:3840, height:2160, is_primary:1",
            "[22:42:47.207] 	app_list_monitor_thread: something else"
        ];
        Assert.Equal((3840, 0), OpenOutlook.Desktop.WslWindowPlacement.PrimaryOrigin(log));            // the right-hand primary starts 3840 pixels into the X display
        Assert.Equal((0, 0), OpenOutlook.Desktop.WslWindowPlacement.PrimaryOrigin(new[] { log[0] }));
        Assert.Null(OpenOutlook.Desktop.WslWindowPlacement.PrimaryOrigin(new[] { "nothing here" }));
    }
}
