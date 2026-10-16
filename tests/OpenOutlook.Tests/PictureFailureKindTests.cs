using System.Net;
using System.Net.Sockets;
using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

/// <summary>
/// Which picture failures deserve the reading pane's "could not be loaded" warning (2026-10-16: Ulta Lab Tests icons that answer 403 to everyone,
/// Slickdeals ad slots whose ad server an AdGuard DNS filter answers with 0.0.0.0). Only failures a retry might fix are "failed".
/// </summary>
public sealed class PictureFailureKindTests
{
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "gone")]
    [InlineData(HttpStatusCode.NotFound, "gone")]
    [InlineData(HttpStatusCode.Gone, "gone")]
    [InlineData(HttpStatusCode.TooManyRequests, "failed")]
    [InlineData(HttpStatusCode.RequestTimeout, "failed")]
    [InlineData(HttpStatusCode.InternalServerError, "failed")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "failed")]
    public async Task A_server_refusal_is_permanent_only_for_client_errors(HttpStatusCode status, string kind)
    {
        using var client = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(status)));
        var error = await Assert.ThrowsAsync<RemoteImageRefusedException>(() =>
            SafeRemoteImageLoader.FetchAsync("https://dk9oxzp8ogjut.cloudfront.net/static/images/fb.png", client));
        Assert.Equal(status, error.Status);
        Assert.Equal(kind, SafeRemoteImageLoader.FailureKind(error));
    }

    [Fact]
    public void A_host_a_network_filter_blocks_or_that_does_not_resolve_is_permanent()
    {
        Assert.Equal("blocked", SafeRemoteImageLoader.FailureKind(new RemoteImageBlockedException()));
        Assert.Equal("blocked", SafeRemoteImageLoader.FailureKind(new HttpRequestException("connect", new RemoteImageBlockedException())));
        Assert.Equal("blocked", SafeRemoteImageLoader.FailureKind(new HttpRequestException("p.liadm.com:443", new SocketException((int)SocketError.NoData))));
        Assert.Equal("blocked", SafeRemoteImageLoader.FailureKind(new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound))));
        Assert.True(SafeRemoteImageLoader.IsPermanentFailure(new RemoteImageBlockedException()));
    }

    [Fact]
    public void Timeouts_and_dropped_connections_stay_worth_a_warning()
    {
        Assert.Equal("failed", SafeRemoteImageLoader.FailureKind(new TaskCanceledException("HttpClient.Timeout")));
        Assert.Equal("failed", SafeRemoteImageLoader.FailureKind(new HttpRequestException("reset", new SocketException((int)SocketError.ConnectionReset))));
        Assert.Equal("failed", SafeRemoteImageLoader.FailureKind(new IOException("Remote image could not be safely loaded.")));   // too large
        Assert.Equal("failed", SafeRemoteImageLoader.FailureKind(null));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
