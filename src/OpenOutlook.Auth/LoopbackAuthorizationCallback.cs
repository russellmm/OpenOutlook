using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenOutlook.Auth;

/// <summary>
/// A single-use, loopback-only HTTP callback receiver. Create it before calling DesktopOAuth.Begin,
/// pass RedirectUri to Begin, then pass the URI returned by CaptureAsync to DesktopOAuth.ExchangeAsync.
/// It does not open a browser, validate OAuth state, or exchange tokens.
/// </summary>
public sealed class LoopbackAuthorizationCallback : IDisposable, IAsyncDisposable
{
    private const int MaxRequestBytes = 12 * 1024;
    private static readonly TimeSpan PerConnectionTimeout = TimeSpan.FromSeconds(5);
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _deadline;
    private int _captureStarted;
    private int _disposed;

    public Uri RedirectUri { get; }

    /// <summary>Reserves an OS-assigned port bound exclusively to IPv4 loopback.</summary>
    public LoopbackAuthorizationCallback(TimeSpan? timeout = null, bool useLocalhostRedirect = false)
    {
        var duration = timeout ?? TimeSpan.FromMinutes(2);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(8);
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        RedirectUri = new Uri($"http://{(useLocalhostRedirect ? "localhost" : "127.0.0.1")}:{port}/callback");
        _deadline = new CancellationTokenSource(duration);
    }

    /// <summary>Waits for one well-formed GET on the exact registered callback path.</summary>
    /// <remarks>Rejected local requests receive a generic failure and do not consume the callback.
    /// Never log or persist the returned URI: its query can contain an authorization code.</remarks>
    public async Task<Uri> CaptureAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _captureStarted, 1) != 0)
            throw new InvalidOperationException("Callback capture already started.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, _deadline.Token);
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
                // Limit slow clients independently of the overall authorization deadline.
                using var requestLimit = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                requestLimit.CancelAfter(PerConnectionTimeout);
                Uri? callback = null;
                try
                {
                    var bytes = await ReadHeadersAsync(client.GetStream(), requestLimit.Token).ConfigureAwait(false);
                    callback = ParseRequest(bytes, RedirectUri);
                }
                catch (OperationCanceledException) when (!linked.IsCancellationRequested) { }
                catch (IOException) when (!linked.IsCancellationRequested) { }
                catch (SocketException) when (!linked.IsCancellationRequested) { }
                // Never echo URI, query, code, state, error, headers, or exception details to the browser.
                try { await SendResponseAsync(client.GetStream(), callback is not null, requestLimit.Token).ConfigureAwait(false); }
                catch (IOException) when (!linked.IsCancellationRequested) { }
                catch (SocketException) when (!linked.IsCancellationRequested) { }
                catch (OperationCanceledException) when (!linked.IsCancellationRequested) { }
                if (callback is not null) return callback;
                linked.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (_deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("Authorization callback timed out.");
        }
        catch (SocketException) when (_lifetime.IsCancellationRequested)
        {
            throw new OperationCanceledException("Callback receiver disposed.");
        }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
        {
            throw new OperationCanceledException("Callback receiver disposed.");
        }
        finally { _listener.Stop(); }
    }

    private static async Task<byte[]> ReadHeadersAsync(NetworkStream stream, CancellationToken token)
    {
        var data = new byte[MaxRequestBytes + 1];
        var count = 0;
        // Reading one byte at a time stops at the header boundary, without swallowing a body
        // or allowing an unlimited client-side write to exhaust memory.
        while (count < data.Length)
        {
            var read = await stream.ReadAsync(data.AsMemory(count, 1), token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
            if (count >= 4 && data[count - 4] == '\r' && data[count - 3] == '\n' && data[count - 2] == '\r' && data[count - 1] == '\n')
                return data[..count];
        }
        return [];
    }

    private static Uri? ParseRequest(byte[] bytes, Uri redirect)
    {
        if (bytes.Length < 4 || bytes.Length > MaxRequestBytes) return null;
        // HTTP headers must be ASCII; reject control characters, bare LF, and non-ASCII input.
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] > 126 || (bytes[i] < 32 && bytes[i] is not (13 or 10))) return null;
        var text = Encoding.ASCII.GetString(bytes);
        if (!text.EndsWith("\r\n\r\n", StringComparison.Ordinal)) return null;
        var lines = text[..^4].Split("\r\n");
        if (lines.Length < 2 || lines.Any(s => s.Contains('\r') || s.Contains('\n'))) return null;
        const string prefix = "GET ";
        const string suffix = " HTTP/1.1";
        var line = lines[0];
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var target = line[prefix.Length..^suffix.Length];
        if (target != "/callback" && !target.StartsWith("/callback?", StringComparison.Ordinal)) return null;
        if (target.Length > 8202 || target.Any(ch => ch is < '!' or > '~' or '#' or '\\')) return null;
        // Reject invalid %-encoding rather than having Uri silently rewrite the callback.
        for (var i = 0; i < target.Length; i++)
            if (target[i] == '%' && (i + 2 >= target.Length || !Uri.IsHexDigit(target[i + 1]) || !Uri.IsHexDigit(target[i + 2]))) return null;
        var hostCount = 0;
        foreach (var header in lines.Skip(1))
        {
            var colon = header.IndexOf(':');
            if (colon < 1 || header[0] is ' ' or '\t') return null;
            var name = header[..colon];
            if (name.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-'))) return null;
            var value = header[(colon + 1)..];
            if (value.Any(ch => ch is < ' ' or > '~')) return null;
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                hostCount++;
                if (value != $" {redirect.Host}:{redirect.Port}" && value != $"{redirect.Host}:{redirect.Port}") return null;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                     (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && value.Trim(' ') != "0")) return null;
        }
        if (hostCount != 1) return null;
        if (!Uri.TryCreate(redirect.GetLeftPart(UriPartial.Authority) + target, UriKind.Absolute, out var callback)) return null;
        // Uri may normalize certain input; preserve the exact request target or reject it.
        return callback.PathAndQuery == target ? callback : null;
    }

    private static Task SendResponseAsync(NetworkStream stream, bool success, CancellationToken token)
    {
        var status = success ? "200 OK" : "400 Bad Request";
        var body = success ? "Authorization received. You may close this window." : "Authorization request could not be received.";
        var response = $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        return stream.WriteAsync(Encoding.UTF8.GetBytes(response), token).AsTask();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _listener.Stop();
        // Sources remain alive until any in-flight capture has observed cancellation.
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
