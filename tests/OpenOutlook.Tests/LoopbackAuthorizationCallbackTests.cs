using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenOutlook.Auth;

namespace OpenOutlook.Tests;

public class LoopbackAuthorizationCallbackTests
{
    [Fact]
    public async Task CapturesExactCallbackAndReturnsGenericResponse()
    {
        await using var receiver = new LoopbackAuthorizationCallback();
        Assert.Equal("127.0.0.1", receiver.RedirectUri.Host);
        Assert.Equal("/callback", receiver.RedirectUri.AbsolutePath);
        Assert.InRange(receiver.RedirectUri.Port, 1, 65535);
        var pending = DesktopOAuth.Begin(OAuthProvider.Google, "public-client", receiver.RedirectUri, ["openid"]);
        var capture = receiver.CaptureAsync();
        var response = await SendAsync(receiver.RedirectUri.Port, $"GET /callback?code=private-code&state={pending.State} HTTP/1.1\r\nHost: 127.0.0.1:{receiver.RedirectUri.Port}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.DoesNotContain("private-code", response);
        Assert.DoesNotContain(pending.State, response);
        Assert.Equal($"{receiver.RedirectUri}?code=private-code&state={pending.State}", (await capture).AbsoluteUri);
        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.CaptureAsync());
    }

    [Theory]
    [InlineData("POST /callback HTTP/1.1", "Host: 127.0.0.1:{port}")]
    [InlineData("GET /other?code=bad HTTP/1.1", "Host: 127.0.0.1:{port}")]
    [InlineData("GET /callback/../callback?code=bad HTTP/1.1", "Host: 127.0.0.1:{port}")]
    [InlineData("GET http://127.0.0.1:{port}/callback HTTP/1.1", "Host: 127.0.0.1:{port}")]
    [InlineData("GET /callback#fragment HTTP/1.1", "Host: 127.0.0.1:{port}")]
    [InlineData("GET /callback?code=%ZZ HTTP/1.1", "Host: 127.0.0.1:{port}")]
    [InlineData("GET /callback HTTP/1.1", "Host: localhost:{port}")]
    [InlineData("GET /callback HTTP/1.1", "Host: attacker.example:{port}")]
    [InlineData("GET /callback HTTP/1.1", "Host: 127.0.0.1:{port}\r\nHost: 127.0.0.1:{port}")]
    [InlineData("GET /callback HTTP/1.1", "X-Test: yes")]
    [InlineData("GET /callback HTTP/1.1", "Host: 127.0.0.1:{port}\r\nContent-Length: 1")]
    public async Task RejectsMalformedRequestWithoutConsuming(string requestLine, string header)
    {
        await using var receiver = new LoopbackAuthorizationCallback();
        var port = receiver.RedirectUri.Port;
        var capture = receiver.CaptureAsync();
        var request = $"{requestLine}\r\n{header}\r\n\r\n".Replace("{port}", port.ToString());
        var response = await SendAsync(port, request);
        Assert.StartsWith("HTTP/1.1 400 Bad Request", response);
        Assert.False(capture.IsCompleted);
        var accepted = await SendAsync(port, $"GET /callback?code=ok HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200 OK", accepted);
        Assert.Equal($"{receiver.RedirectUri}?code=ok", (await capture).AbsoluteUri);
    }

    [Fact]
    public async Task BoundsOversizedRequestsAndSupportsCancellationAndDisposal()
    {
        await using (var receiver = new LoopbackAuthorizationCallback())
        {
            using var cancellation = new CancellationTokenSource();
            var capture = receiver.CaptureAsync(cancellation.Token);
            var oversized = $"GET /callback?{new string('x', 13000)} HTTP/1.1\r\nHost: 127.0.0.1:{receiver.RedirectUri.Port}\r\n\r\n";
            Assert.StartsWith("HTTP/1.1 400 Bad Request", await SendAsync(receiver.RedirectUri.Port, oversized));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        }
        var disposed = new LoopbackAuthorizationCallback();
        var waiting = disposed.CaptureAsync();
        disposed.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task TimesOutWithoutReceivingCallback()
    {
        await using var receiver = new LoopbackAuthorizationCallback(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAsync<TimeoutException>(() => receiver.CaptureAsync());
    }

    private static async Task<string> SendAsync(int port, string request)
    {
        using var socket = new TcpClient();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await socket.ConnectAsync(IPAddress.Loopback, port, limit.Token);
        using var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), limit.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(limit.Token);
    }
}
