using System.Net;
using System.Net.Sockets;

namespace CouchHud.Core.Net;

/// <summary>
/// Which peers may talk to the phone remote and the MCP server: the machine itself and addresses of the local
/// network (private ranges, link-local). Public addresses never, even if a router forwards the port.
/// </summary>
public static class LanOnly
{
    /// <summary>Loopback, private (10/8, 172.16/12, 192.168/16), link-local (169.254/16, fe80::/10) or unique-local (fc00::/7).</summary>
    public static bool IsAllowed(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                return bytes[0] == 10
                       || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                       || (bytes[0] == 192 && bytes[1] == 168)
                       || (bytes[0] == 169 && bytes[1] == 254);
            case AddressFamily.InterNetworkV6:
                return address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
            default:
                return false;
        }
    }

    /// <summary>Only the machine itself (the MCP server without the LAN option).</summary>
    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }
}
