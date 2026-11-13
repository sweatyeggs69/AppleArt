using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AnimatedArtworks.Infrastructure;

// Every outgoing request, including each redirect hop, must target an Apple host over HTTPS.
public sealed class AppleEgressHandler : DelegatingHandler
{
    private const int MaxRedirects = 5;

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "music.apple.com",
        "amp-api.music.apple.com"
    };

    public static bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 &&
        AllowedHosts.Contains(uri.IdnHost);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpRequestMessage current = request;

        for (int hop = 0; ; hop++)
        {
            if (!IsAllowed(current.RequestUri))
            {
                throw new HttpRequestException($"Blocked request to non-Apple host: {current.RequestUri?.Host}");
            }

            HttpResponseMessage response = await base.SendAsync(current, ct);

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                return response;
            }

            response.Dispose();

            if (hop >= MaxRedirects)
            {
                throw new HttpRequestException("Too many redirects.");
            }

            var next = new HttpRequestMessage(HttpMethod.Get, new Uri(current.RequestUri!, location));
            foreach (KeyValuePair<string, IEnumerable<string>> header in current.Headers)
            {
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            current = next;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public static SocketsHttpHandler CreatePrimaryHandler()
    {
        // An operator-configured egress proxy (HTTPS_PROXY) may live on a private address.
        Uri? proxy = HttpClient.DefaultProxy.GetProxy(new Uri("https://music.apple.com/"));

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = (context, ct) => ConnectAsync(context, proxy, ct)
        };
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, Uri? proxy, CancellationToken ct)
    {
        bool isProxy = proxy != null &&
                       string.Equals(proxy.Host, context.DnsEndPoint.Host, StringComparison.OrdinalIgnoreCase) &&
                       proxy.Port == context.DnsEndPoint.Port;

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        IPAddress[] publicAddresses = isProxy ? addresses : addresses.Where(IsPublic).ToArray();
        if (publicAddresses.Length == 0)
        {
            throw new HttpRequestException($"Blocked connection to non-public address for {context.DnsEndPoint.Host}");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(publicAddresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 0 ||
                     b[0] == 10 ||
                     (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||
                     (b[0] == 169 && b[1] == 254) ||
                     (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                     (b[0] == 192 && b[1] == 168) ||
                     (b[0] == 192 && b[1] == 0 && b[2] == 0) ||
                     (b[0] == 198 && (b[1] == 18 || b[1] == 19)) ||
                     b[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal ||
                     address.IsIPv6SiteLocal ||
                     address.IsIPv6Multicast ||
                     (b[0] & 0xfe) == 0xfc);
        }

        return false;
    }
}
