using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace AnimatedArtworks.Infrastructure;

public static class ClientIdentity
{
    private static readonly byte[]? ProxySecret =
        Environment.GetEnvironmentVariable("PROXY_SECRET") is { Length: > 0 } secret ? Encoding.UTF8.GetBytes(secret) : null;

    // CF-Connecting-IP is set by Cloudflare and only trusted when the connection comes from the
    // local tunnel (cloudflared); X-Forwarded-For is caller-controlled and never used.
    // Requests relayed by the Tromme API Worker all carry the Worker's own CF-Connecting-IP, so the
    // Worker's X-Client-IP is used instead, but only alongside a matching PROXY_SECRET.
    public static string Resolve(HttpContext context)
    {
        IPAddress? remote = context.Connection.RemoteIpAddress;
        if (remote == null)
        {
            return "unknown";
        }

        if (AppleEgressHandler.IsPublic(remote))
        {
            return remote.ToString();
        }

        if (IsFromProxy(context.Request) &&
            IPAddress.TryParse(context.Request.Headers["X-Client-IP"].ToString().Trim(), out IPAddress? relayedIp))
        {
            return relayedIp.ToString();
        }

        if (IPAddress.TryParse(context.Request.Headers["CF-Connecting-IP"].ToString().Trim(), out IPAddress? clientIp))
        {
            return clientIp.ToString();
        }

        return remote.ToString();
    }

    private static bool IsFromProxy(HttpRequest request)
    {
        if (ProxySecret == null)
        {
            return false;
        }

        byte[] presented = Encoding.UTF8.GetBytes(request.Headers["X-Origin-Secret"].ToString());
        return CryptographicOperations.FixedTimeEquals(presented, ProxySecret);
    }
}
