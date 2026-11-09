using System.Net;
using System.Net.Sockets;

namespace MegaHub.Core.Net;

/// <summary>
/// Keeps downloads on the public internet: address lists in the catalog caches of other programs or in
/// answers of web services must never make Mega Hub talk to something inside the home network or on
/// this PC (routers, cameras, the remote and MCP servers, cloud metadata).
/// </summary>
public static class SafeNet
{
    /// <summary>
    /// False for loopback, unspecified, link-local, private (RFC 1918, ULA), shared (CGNAT), documentation, multicast and reserved addresses. An IPv6 address
    /// that carries an IPv4 address (mapped ::ffff:a.b.c.d, NAT64 64:ff9b::/96, 6to4 2002::/16, Teredo 2001::/32, compatible ::a.b.c.d) is judged by the
    /// IPv4 address inside, so no private or local address gets through in disguise.
    /// </summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork) return IsPublicV4(ip.GetAddressBytes());
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC) return false; // unique local fc00::/7
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false;                         // documentation 2001:db8::/32
            if (b[0] == 0x01 && b[1] == 0 && b[2..8].All(x => x == 0)) return false;                                // discard 100::/64
            // NAT64 64:ff9b::/96 (the address is the one in the last four bytes); the local-use range 64:ff9b:1::/48 is never public.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B)
            {
                if (b[4..12].All(x => x == 0)) return IsPublicV4(b[12..16]);
                if (b[4] == 0x00 && b[5] == 0x01) return false;
            }
            // 6to4 2002::/16: the address is in bytes 2..5.
            if (b[0] == 0x20 && b[1] == 0x02) return IsPublicV4(b[2..6]);
            // Teredo 2001::/32: the server in bytes 4..7 and the client (stored inverted) in bytes 12..15 must both be public.
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0)
            {
                var client = new[] { (byte)~b[12], (byte)~b[13], (byte)~b[14], (byte)~b[15] };
                return IsPublicV4(b[4..8]) && IsPublicV4(client);
            }
            // IPv4-compatible ::a.b.c.d (deprecated): the first twelve bytes are zero.
            if (b[..12].All(x => x == 0)) return IsPublicV4(b[12..16]);
            return true;
        }
        return false;
    }

    private static bool IsPublicV4(byte[] b) =>
        !(b[0] == 0                                           // 0.0.0.0/8
            || b[0] == 10                                     // private
            || (b[0] == 100 && b[1] is >= 64 and <= 127)      // shared address space (CGNAT)
            || b[0] == 127                                    // loopback
            || (b[0] == 169 && b[1] == 254)                   // link-local
            || (b[0] == 172 && b[1] is >= 16 and <= 31)       // private
            || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)   // IETF protocol assignments, documentation (TEST-NET-1)
            || (b[0] == 192 && b[1] == 88 && b[2] == 99)      // 6to4 relay anycast (deprecated)
            || (b[0] == 192 && b[1] == 168)                   // private
            || (b[0] == 198 && b[1] is 18 or 19)              // benchmarking
            || (b[0] == 198 && b[1] == 51 && b[2] == 100)     // documentation (TEST-NET-2)
            || (b[0] == 203 && b[1] == 0 && b[2] == 113)      // documentation (TEST-NET-3)
            || b[0] >= 224);                                  // multicast, reserved (240/4), broadcast

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
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
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
