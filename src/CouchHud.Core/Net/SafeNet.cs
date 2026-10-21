using System.Net;
using System.Net.Sockets;

namespace CouchHud.Core.Net;

/// <summary>
/// Keeps downloads on the public internet: address lists in the catalog caches of other programs or in
/// answers of web services must never make CouchHUD talk to something inside the home network or on
/// this PC (routers, cameras, the remote and MCP servers, cloud metadata).
/// </summary>
public static class SafeNet
{
    /// <summary>False for loopback, unspecified, link-local, private (RFC 1918, ULA), shared (CGNAT), multicast and reserved addresses.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0                                    // 0.0.0.0/8
                || b[0] == 10                                     // private
                || (b[0] == 100 && b[1] is >= 64 and <= 127)      // shared address space (CGNAT)
                || (b[0] == 169 && b[1] == 254)                   // link-local
                || (b[0] == 172 && b[1] is >= 16 and <= 31)       // private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)        // IETF protocol assignments
                || (b[0] == 192 && b[1] == 168)                   // private
                || (b[0] == 198 && b[1] is 18 or 19)              // benchmarking
                || b[0] >= 224);                                  // multicast, reserved, broadcast
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC);                        // unique local fc00::/7
        }
        return false;
    }

    /// <summary>
    /// A handler that resolves names itself and refuses to connect to anything that is not public. The check
    /// happens on the address actually used (so a name that resolves to a private address, or changes between
    /// check and use, can't get through), redirects are never followed here (callers follow them, hop by hop,
    /// through their own checks), and the system proxy is not used (its address would be private).
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = ConnectPublicAsync
    };

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(IsPublic).ToList();
        if (allowed.Count == 0) throw new HttpRequestException($"{host} does not resolve to a public address");

        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw new HttpRequestException($"could not connect to {host}", last);
    }
}
