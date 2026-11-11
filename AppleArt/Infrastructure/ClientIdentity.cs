using System.Net;
using Microsoft.AspNetCore.Http;

namespace AnimatedArtworks.Infrastructure;

public static class ClientIdentity
{
    // CF-Connecting-IP is set by Cloudflare and only trusted when the connection comes from the
    // local tunnel (cloudflared); X-Forwarded-For is caller-controlled and never used.
    public static string Resolve(HttpContext context)
    {
        IPAddress? remote = context.Connection.RemoteIpAddress;
        if (remote == null)
        {
            return "unknown";
        }

        if (!AppleEgressHandler.IsPublic(remote) &&
            IPAddress.TryParse(context.Request.Headers["CF-Connecting-IP"].ToString().Trim(), out IPAddress? clientIp))
        {
            return clientIp.ToString();
        }

        return remote.ToString();
    }
}
