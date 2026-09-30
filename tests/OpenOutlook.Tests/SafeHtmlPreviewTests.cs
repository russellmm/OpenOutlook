using OpenOutlook.Desktop;
using System.Net;

namespace OpenOutlook.Tests;

public sealed class SafeHtmlPreviewTests
{
    [Fact]
    public void KeepsReadableFormattingWithoutActivatingMarkup()
    {
        var runs = SafeHtmlPreview.Parse("""
            <h2>Hello &amp; welcome</h2><p>Normal <strong>bold</strong> and <em>italic</em>.</p>
            <ul><li>First</li><li>Second</li></ul>
            <a href="https://example.test/track">Read more</a><img src="https://example.test/pixel" />
            <script>fetch('https://example.test/hidden')</script>
            <style>body{background:url(https://example.test/hidden)}</style>
            """);
        var text = string.Concat(runs.Select(run => run.Text));
        Assert.Contains("Hello & welcome", text);
        Assert.Contains("\n\n", text);
        Assert.Contains("• First", text);
        Assert.Contains("Read more", text);
        Assert.Contains("[Image]", text);
        Assert.DoesNotContain("fetch", text);
        Assert.DoesNotContain("background", text);
        Assert.DoesNotContain("https://", text);
        Assert.Contains(runs, run => run.Bold && run.Text.Contains("bold"));
        Assert.Contains(runs, run => run.Italic && run.Text.Contains("italic"));
        Assert.Contains(runs, run => run.Scale > 1 && run.Text.Contains("Hello"));
    }

    [Fact]
    public void RejectsUnboundedInput()
    {
        Assert.Throws<InvalidDataException>(() => SafeHtmlPreview.Parse(new string('x', 512 * 1024 + 1)));
    }

    [Fact]
    public void FindsCidAndRemoteImagesForLoading()
    {
        var runs = SafeHtmlPreview.Parse("<p>Before<img src='cid:photo-1' width='500'></p>" +
            "<img src='https://example.test/pixel'><img src='file:///etc/passwd'>");
        Assert.Single(runs.Where(run => run.ImageContentId is not null));
        Assert.Equal("photo-1", runs.Single(run => run.ImageContentId is not null).ImageContentId);
        Assert.Single(runs.Where(run => run.RemoteImageUrl is not null));
        Assert.Single(runs.Where(run => run.Text.Contains("[Image blocked]")));
        Assert.DoesNotContain("https://", string.Concat(runs.Select(run => run.Text)));
    }

    [Fact]
    public void EmbeddedImageDimensionsAreBoundedBeforeDecode()
    {
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10,
            0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 64, 0, 0, 0, 32 };
        Assert.Equal((64, 32), SafeInlineImage.Validate(png));
        png[16] = 0x01;
        Assert.Throws<InvalidDataException>(() => SafeInlineImage.Validate(png));
        Assert.Throws<InvalidDataException>(() => SafeInlineImage.Validate(new byte[SafeInlineImage.MaximumBytes + 1]));
    }

    [Fact]
    public void RemoteImagesUsePublicNetworkAddresses()
    {
        Assert.True(SafeRemoteImageLoader.TryAcceptUrl("https://images.example.com/photo.png", out _));
        Assert.True(SafeRemoteImageLoader.TryAcceptUrl("http://images.example.com/photo.png", out _));
        Assert.False(SafeRemoteImageLoader.TryAcceptUrl("http://images.example.com:8080/photo.png", out _));
        Assert.False(SafeRemoteImageLoader.TryAcceptUrl("https://localhost/photo.png", out _));
        Assert.False(SafeRemoteImageLoader.TryAcceptUrl("https://user:pass@images.example.com/photo.png", out _));
        Assert.False(SafeRemoteImageLoader.IsPublicAddress(System.Net.IPAddress.Parse("127.0.0.1")));
        Assert.False(SafeRemoteImageLoader.IsPublicAddress(System.Net.IPAddress.Parse("192.168.1.2")));
        Assert.False(SafeRemoteImageLoader.IsPublicAddress(System.Net.IPAddress.IPv6Loopback));
        Assert.True(SafeRemoteImageLoader.IsPublicAddress(System.Net.IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public async Task RemoteImageFetchRejectsRedirectsAndOversizedBodies()
    {
        const string url = "https://images.example.com/photo.png";
        using var redirected = new HttpClient(new ImageHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)));
        await Assert.ThrowsAsync<IOException>(() => SafeRemoteImageLoader.FetchAsync(url, redirected));
        using var privateRedirect = new HttpClient(new ImageHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://localhost/image.png") }
        }));
        await Assert.ThrowsAsync<IOException>(() => SafeRemoteImageLoader.FetchAsync(url, privateRedirect));
        using var oversized = new HttpClient(new ImageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[SafeInlineImage.MaximumBytes + 1])
        }));
        await Assert.ThrowsAsync<IOException>(() => SafeRemoteImageLoader.FetchAsync(url, oversized));
    }

    [Fact]
    public async Task RemoteImageFetchFollowsPublicRedirect()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
        var requests = 0;
        using var client = new HttpClient(new ImageHandler(_ => ++requests == 1
            ? new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://cdn.example.com/image.png") }
            }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) }));
        Assert.Equal(png, await SafeRemoteImageLoader.FetchAsync("http://images.example.com/image.png", client));
        Assert.Equal(2, requests);
    }

    private sealed class ImageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
