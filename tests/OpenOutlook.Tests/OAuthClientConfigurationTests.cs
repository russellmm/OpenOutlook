using OpenOutlook.Auth;
using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class OAuthClientConfigurationTests
{
    [Fact]
    public void MissingConfigurationDoesNotInventApplicationIds()
    {
        var configuration = OAuthClientConfiguration.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        Assert.Null(configuration.GetClientId(OAuthProvider.MicrosoftConsumers));
        Assert.Null(configuration.GetClientId(OAuthProvider.Google));
    }

    [Fact]
    public void BuildOwnedIdsAreReadWithoutCredentials()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, """{"microsoftClientId":" microsoft-public-id ","googleClientId":"google-public-id"}""");
            var configuration = OAuthClientConfiguration.Load(path);
            Assert.Equal("microsoft-public-id", configuration.GetClientId(OAuthProvider.MicrosoftConsumers));
            Assert.Equal("google-public-id", configuration.GetClientId(OAuthProvider.Google));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"microsoftClientId\":45}")]
    [InlineData("{\"googleClientId\":\"bad\\nid\"}")]
    public void InvalidConfigurationIsRejected(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, content);
            Assert.Throws<InvalidDataException>(() => OAuthClientConfiguration.Load(path));
        }
        finally { File.Delete(path); }
    }
}
