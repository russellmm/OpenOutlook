using System.Net;
using OpenOutlook.Auth;

namespace OpenOutlook.Tests;

[CollectionDefinition("OAuth static state", DisableParallelization = true)]
public sealed class OAuthStaticStateCollection;

/// <summary>Sets the process-wide Google client secret, so it must not run alongside tests that check exact token requests.</summary>
[Collection("OAuth static state")]
public sealed class GoogleClientSecretTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public string? Form;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Form = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"access_token":"at","refresh_token":"rt","expires_in":3600,"token_type":"Bearer"}"""), RequestMessage = request };
        }
    }

    [Fact]
    public async Task Client_secret_goes_only_to_the_provider_it_belongs_to()
    {
        try
        {
            DesktopOAuth.SetClientSecret(OAuthProvider.Google, "g-secret");
            var capture = new Capture();
            using var http = new HttpClient(capture);
            await DesktopOAuth.RefreshAsync(OAuthProvider.Google, "gclient", "refresh-1", http);
            Assert.Contains("client_secret=g-secret", capture.Form);
            await DesktopOAuth.RefreshAsync(OAuthProvider.MicrosoftConsumers, "mclient", "refresh-2", http);
            Assert.DoesNotContain("client_secret", capture.Form);
            DesktopOAuth.SetClientSecret(OAuthProvider.Google, null);
            await DesktopOAuth.RefreshAsync(OAuthProvider.Google, "gclient", "refresh-3", http);
            Assert.DoesNotContain("client_secret", capture.Form);
            Assert.Throws<ArgumentException>(() => DesktopOAuth.SetClientSecret(OAuthProvider.Google, "bad\nsecret"));
        }
        finally { DesktopOAuth.SetClientSecret(OAuthProvider.Google, null); }
    }

    [Fact]
    public async Task Loading_the_sign_in_configuration_at_startup_sets_the_secret_for_token_refresh()
    {
        // Regression: the secret was only set when the Accounts window opened, so the first Gmail token refresh after a restart was rejected.
        var path = Path.Combine(Path.GetTempPath(), "oo-oauth-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"microsoftClientId":"m","googleClientId":"g.apps.googleusercontent.com","googleClientSecret":"loaded-secret"}""");
        try
        {
            DesktopOAuth.SetClientSecret(OAuthProvider.Google, null);
            OpenOutlook.Desktop.OAuthClientConfiguration.Load(path);
            var capture = new Capture();
            using var http = new HttpClient(capture);
            await DesktopOAuth.RefreshAsync(OAuthProvider.Google, "g.apps.googleusercontent.com", "refresh-1", http);
            Assert.Contains("client_secret=loaded-secret", capture.Form);
        }
        finally { File.Delete(path); DesktopOAuth.SetClientSecret(OAuthProvider.Google, null); }
    }
}
