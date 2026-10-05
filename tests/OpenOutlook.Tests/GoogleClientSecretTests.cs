using System.Net;
using OpenOutlook.Auth;

namespace OpenOutlook.Tests;

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
}
