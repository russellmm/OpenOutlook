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
}
