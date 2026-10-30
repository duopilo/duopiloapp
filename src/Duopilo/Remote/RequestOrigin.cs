using System.Net;

namespace Kopilot.Remote;

/// <summary>
/// Unterscheidet direkte lokale Aufrufe von Aufrufen über den Cloudflare Tunnel.
/// cloudflared läuft zwar auch lokal, setzt aber immer CF-Connecting-IP und reicht den öffentlichen Hostnamen durch.
/// </summary>
public static class RequestOrigin
{
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]", "::1"];

    public static bool IsLocal(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;

        // TestServer setzt keine IP – dort zählt nur der Host.
        if (ip is not null && !IPAddress.IsLoopback(ip))
        {
            return false;
        }

        if (context.Request.Headers.ContainsKey("CF-Connecting-IP") || context.Request.Headers.ContainsKey("Cf-Ray"))
        {
            return false;
        }

        return IsLoopbackHost(context.Request.Host.Host);
    }

    public static bool IsLoopbackHost(string host) =>
        LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
}
