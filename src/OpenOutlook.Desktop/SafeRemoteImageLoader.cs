using System.Net;
using System.Net.Sockets;

namespace OpenOutlook.Desktop;

/// <summary>The picture's server answered with an error status (403 / 404 / 410 ...): retrying will not help.</summary>
public sealed class RemoteImageRefusedException(HttpStatusCode status) : IOException($"Remote image could not be safely loaded (HTTP {(int)status}).")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>The picture's host has no public address: a DNS filter (AdGuard, Pi-hole) answers 0.0.0.0 / :: for ad and tracking hosts.</summary>
public sealed class RemoteImageBlockedException() : IOException("Image host does not have a public address (blocked by a network filter?).");

/// <summary>Fetches message images without proxies or private-network access.</summary>
public static class SafeRemoteImageLoader
{
    /// <summary>
    /// What kind of failure a picture had, for the reading pane's warning. "Gone" (the sender's server refuses it: 4xx) and "blocked" (the host
    /// resolves to nothing public or not at all: an ad filter, a dead domain) will fail again on every retry, as they would in Outlook, which simply
    /// leaves an empty spot; only "failed" (timeouts, server errors, dropped connections) is worth a warning and a retry. Found 2026-10-16: an Ulta
    /// Lab Tests mailing whose social icons answer 403 to everyone, and Slickdeals ad slots whose ad server AdGuard answers with 0.0.0.0.
    /// </summary>
    public static string FailureKind(Exception? error) => error switch
    {
        HttpRequestException { InnerException: RemoteImageBlockedException } => "blocked",     // thrown in ConnectCallback, wrapped by HttpClient
        RemoteImageRefusedException r when (int)r.Status is >= 400 and < 500 && r.Status != HttpStatusCode.RequestTimeout && (int)r.Status != 429 => "gone",
        RemoteImageBlockedException => "blocked",
        HttpRequestException { InnerException: System.Net.Sockets.SocketException s } when
            s.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData => "blocked",
        System.Net.Sockets.SocketException s when s.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData => "blocked",
        _ => "failed"
    };

    /// <summary>True when a retry cannot bring the picture back (see <see cref="FailureKind"/>).</summary>
    public static bool IsPermanentFailure(Exception? error) => FailureKind(error) != "failed";
    /// <summary>
    /// Image servers behind bot protection (Cloudflare and the like) answer 403 to a request without a browser User-Agent: .NET sends
    /// none by default, so newsletter pictures failed while Outlook and any browser showed them (found 2026-10-15 on a Steelers Now
    /// newsletter: 5 of 5 images refused, all 200 with a browser User-Agent).
    /// </summary>
    public static string BrowserUserAgent =>
        OperatingSystem.IsWindows() ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36"
        : OperatingSystem.IsMacOS() ? "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36"
        : "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    /// <summary>The formats the reader can check and show; a server that can convert (most CDNs) picks one of them.</summary>
    public const string AcceptedImageTypes = "image/webp,image/png,image/jpeg,image/gif;q=0.9,image/*;q=0.5";

    public static bool TryAcceptUrl(string value, out Uri uri)
    {
        uri = null!;
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp) ||
            !parsed.IsDefaultPort || parsed.UserInfo.Length != 0 ||
            parsed.HostNameType != UriHostNameType.Dns || parsed.Host.Length > 253 ||
            parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            parsed.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            parsed.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            parsed.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                var address = addresses.FirstOrDefault(IsPublicAddress) ??
                    throw new RemoteImageBlockedException();
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }

    public static async Task<byte[]> FetchAsync(string value, HttpClient client,
        CancellationToken cancellationToken = default)
    {
        if (!TryAcceptUrl(value, out var uri))
            throw new InvalidDataException("Remote image URL is not permitted.");
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(AcceptedImageTypes);
            if (!request.Headers.UserAgent.Any() && !client.DefaultRequestHeaders.UserAgent.Any()) request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } actualUri && actualUri != uri)
                throw new IOException("Remote image request was redirected unexpectedly.");
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                if (redirect == 3 || location is null ||
                    !TryAcceptUrl(new Uri(uri, location).AbsoluteUri, out var next))
                    throw new IOException("Remote image redirect is not permitted.");
                uri = next;
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new RemoteImageRefusedException(response.StatusCode);
            if (response.Content.Headers.ContentLength > SafeInlineImage.MaximumBytes)
                throw new IOException("Remote image could not be safely loaded.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > SafeInlineImage.MaximumBytes)
                    throw new InvalidDataException("Remote image exceeds the preview limit.");
                output.Write(buffer, 0, read);
            }
            var bytes = output.ToArray();
            SafeInlineImage.Validate(bytes);
            return bytes;
        }
        throw new IOException("Remote image exceeded the redirect limit.");
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] is 0 or 10 or 127 or >= 224 || b[0] == 169 && b[1] == 254 ||
                b[0] == 172 && b[1] is >= 16 and <= 31 ||
                b[0] == 192 && (b[1] == 168 || b[1] == 0 || b[1] == 2) ||
                b[0] == 100 && b[1] is >= 64 and <= 127 ||
                b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100) ||
                b[0] == 203 && b[1] == 0 && b[2] == 113)
                return false;
            return true;
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || IPAddress.IsLoopback(address) ||
            address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;
        var v6 = address.GetAddressBytes();
        return v6[0] is >= 0x20 and <= 0x3f &&
            !(v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0d && v6[3] == 0xb8);
    }
}
