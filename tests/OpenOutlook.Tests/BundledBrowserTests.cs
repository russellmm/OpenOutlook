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
}
