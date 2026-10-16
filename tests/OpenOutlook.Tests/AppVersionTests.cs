using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("0.1.41+1a2b3c4", "1.0.0", "0.1.41 (commit 1a2b3c4)")]
    [InlineData("0.1.41+1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b", "1.0.0", "0.1.41 (commit 1a2b3c4)")]        // the SDK's full hash is shortened
    [InlineData("0.1.41+1a2b3c4-modified", "1.0.0", "0.1.41 (commit 1a2b3c4 + changes)")]                  // built with uncommitted changes
    [InlineData("0.1.41", "1.0.0", "0.1.41")]
    [InlineData(null, "1.0.0", "1.0.0")]                                                                     // an unstamped developer build
    [InlineData(null, null, "unknown")]
    public void The_version_shown_names_the_package_version_and_the_commit(string? informational, string? assembly, string expected) =>
        Assert.Equal(expected, AppVersion.Describe(informational, assembly));
}
