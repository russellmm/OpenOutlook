using System.Net;
using System.Net.Sockets;

namespace OpenOutlook.Desktop;

/// <summary>Fetches message images without proxies or private-network access.</summary>
public static class SafeRemoteImageLoader
{
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
                    throw new IOException("Image host does not have a public address.");
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    public static async Task<byte[]> FetchAsync(string value, HttpClient client,
        CancellationToken cancellationToken = default)
    {
        if (!TryAcceptUrl(value, out var uri))
            throw new InvalidDataException("Remote image URL is not permitted.");
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("image/png, image/jpeg, image/gif");
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
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength > SafeInlineImage.MaximumBytes)
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
