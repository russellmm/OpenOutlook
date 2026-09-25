using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class SystemAuthorizationBrowserTests
{
    [Theory]
    [InlineData("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?client_id=public")]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth?client_id=public")]
    public void OnlyProviderAuthorizationPagesAreAccepted(string address) =>
        SystemAuthorizationBrowser.Validate(new Uri(address));

    [Theory]
    [InlineData("http://accounts.google.com/o/oauth2/v2/auth")]
    [InlineData("https://accounts.google.com.evil.test/o/oauth2/v2/auth")]
    [InlineData("https://attacker@accounts.google.com/o/oauth2/v2/auth")]
    [InlineData("https://accounts.google.com:444/o/oauth2/v2/auth")]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth#fragment")]
    [InlineData("https://login.microsoftonline.com/common/oauth2/v2.0/authorize")]
    public void OtherPagesAreRejected(string address) =>
        Assert.Throws<ArgumentException>(() => SystemAuthorizationBrowser.Validate(new Uri(address)));
}
