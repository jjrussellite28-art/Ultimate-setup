using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CouchHud.Sdk;

namespace CouchHud.Core.Integrations.Tv;

// TV control from the couch: switch the TV on and off, choose the input, volume and mute. PC graphics cards generally do not speak HDMI-CEC
// (the CEC line is not wired to the GPU's HDMI port), so the TV's network API is used. Since 0.7 the protocols are plugins (SDK 5,
// capability "tv.control": LG webOS in "webostv", Samsung Tizen in "samsungtv"); the core keeps the page, the pairing flow, the triggers,
// Wake-on-LAN, discovery, the address policy and the key storage, and it is the only one that opens a connection to the TV (TvLink).

/// <summary>A TV found by discovery or typed in: the address (an IP literal of the local network), what it said about itself and the provider id that fits it.</summary>
public sealed record TvDevice(string Address, string? Name, string Provider);

/// <summary>
/// A stored pairing. <see cref="ClientKey"/> is a secret (stored with DPAPI, never logged, never handed to the UI, only ever handed to the
/// provider of <see cref="Plugin"/>); <see cref="CertPin"/> is the SHA-256 of the TV certificate's public key (SPKI, base64), trusted on
/// first use and required afterwards. <see cref="PcApp"/> is the app that was in front on the TV while pairing (the PC's input); "TV off
/// with the PC" only switches off while that app is in front.
/// </summary>
public sealed record TvPairing(string Provider, string Address, bool Secure, string ClientKey, string? CertPin, string? Mac, string? Name, string? Model, string? PcApp = null, string Plugin = "");

/// <summary>Text and certificate helpers for everything that comes from a TV.</summary>
public static class TvText
{
    /// <summary>A label from the TV: control and format characters removed, whitespace collapsed, at most 48 characters; null if empty.</summary>
    public static string? CleanLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sb = new StringBuilder(Math.Min(text.Length, 64));
        var space = false;
        foreach (var c in text)
        {
            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
            if (sb.Length >= 48) break;
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>An app id from the TV ("com.webos.app.hdmi1", "org.tizen.hdmi"): letters, digits, ".", "_", "-", at most 128 characters; else null.</summary>
    public static string? CleanAppId(string? id) =>
        id is { Length: > 0 and <= 128 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? id : null;

    /// <summary>A pairing key as the core keeps it: printable ASCII without spaces, 8 to 256 characters.</summary>
    public static bool IsValidKey(string? key) => key is { Length: >= 8 and <= 256 } && key.All(c => c is > ' ' and < (char)127);

    /// <summary>SHA-256 over the certificate's SubjectPublicKeyInfo, base64: survives a re-issued certificate with the same key.</summary>
    public static string SpkiPin(X509Certificate2 certificate) =>
        Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    /// <summary>Constant-time comparison of two pins (both base64 of 32 bytes).</summary>
    public static bool SamePin(string? expected, string? actual)
    {
        if (expected is null || actual is null) return false;
        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(actual);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>The address as stored: IPv4-mapped IPv6 as plain IPv4.</summary>
    public static string Canonical(IPAddress address) => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
}

/// <summary>Which addresses a TV may have: only the local network, never the internet, never this PC.</summary>
public static class TvAddressPolicy
{
    /// <summary>
    /// RFC 1918 (10/8, 172.16/12, 192.168/16), IPv4 link-local (169.254/16) and IPv6 unique-local (fd00::/8). Loopback only when
    /// <paramref name="allowLoopback"/> (the tests' fake TV). Not: public addresses, 100.64/10 (CGNAT, Tailscale), multicast, broadcast,
    /// 0.0.0.0, and IPv6 link-local (it needs a scope id that an address typed on a TV cannot carry reliably).
    /// </summary>
    public static bool IsAllowed(IPAddress? address, bool allowLoopback = false) => IsAllowed(address, allowLoopback, LocalSubnets);

    /// <summary>
    /// As above, with the PC's own IPv4 subnets (address, mask) for the network and broadcast addresses: an address that is the network
    /// or broadcast address of a subnet of this PC is never a TV. Outside the PC's subnets, x.x.x.0 and x.x.x.255 are refused (the usual
    /// /24 home network); inside a larger subnet of the PC those can be real hosts and stay allowed.
    /// </summary>
    public static bool IsAllowed(IPAddress? address, bool allowLoopback, Func<IReadOnlyList<(IPAddress Address, IPAddress Mask)>> subnets)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return allowLoopback;
        var b = address.GetAddressBytes();
        var own = subnets();
        if (address.AddressFamily == AddressFamily.InterNetwork && IsNetworkOrBroadcast(b, own)) return false;
        // This PC itself is never a TV: a tv.control plugin would reach every local service bound to 0.0.0.0 (the phone remote, ...).
        if (own.Any(s => s.Address.Equals(address))) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && IsOwnIPv6(address)) return false;
        return InLocalRange(address);
    }

    /// <summary>Only the address range (private, link-local, unique-local), nothing about this PC: for picking where to send discovery from.</summary>
    public static bool InLocalRange(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => b[0] == 10
                                          || (b[0] == 172 && b[1] is >= 16 and <= 31)
                                          || (b[0] == 192 && b[1] == 168 && !(b[2] == 0 && b[3] == 0))
                                          || (b[0] == 169 && b[1] == 254 && b[2] is > 0 and < 255),
            AddressFamily.InterNetworkV6 => b[0] == 0xFD,
            _ => false
        };
    }

    private static bool IsOwnIPv6(IPAddress address)
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && u.Address.Equals(address));
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return false;
        }
    }

    private static bool IsNetworkOrBroadcast(byte[] b, IReadOnlyList<(IPAddress Address, IPAddress Mask)> subnets)
    {
        var ip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        foreach (var (local, mask) in subnets)
        {
            if (local.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork) continue;
            var l = local.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var lu = (uint)(l[0] << 24 | l[1] << 16 | l[2] << 8 | l[3]);
            var mu = (uint)(m[0] << 24 | m[1] << 16 | m[2] << 8 | m[3]);
            // /31 and /32 have no network or broadcast address; a zero mask is no subnet.
            if (mu == 0 || mu >= 0xFFFFFFFE || (ip & mu) != (lu & mu)) continue;
            return (ip & ~mu) == 0 || (ip | mu) == uint.MaxValue;
        }
        return b[3] is 0 or 255;
    }

    /// <summary>The PC's IPv4 subnets (interfaces that are up); empty when they cannot be read.</summary>
    public static IReadOnlyList<(IPAddress Address, IPAddress Mask)> LocalSubnets()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask is not null)
                .Select(u => (u.Address, u.IPv4Mask))
                .ToList();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>
    /// An address typed by the user: an IP literal of the local network (as text, normalized), or null. Host names are not taken: the
    /// address is pinned to the certificate and the key, and a name could point elsewhere tomorrow (or resolve to a public address).
    /// </summary>
    public static IPAddress? ParseManual(string? text, bool allowLoopback = false, Func<IReadOnlyList<(IPAddress Address, IPAddress Mask)>>? subnets = null)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length is 0 or > 45) return null;
        // IPAddress.TryParse takes "10" or "0x0a.1" as well; only the plain dotted form or an IPv6 literal.
        if (trimmed.Contains('.') && trimmed.Count(c => c == '.') != 3) return null;
        if (!trimmed.All(c => char.IsAsciiHexDigit(c) || c is '.' or ':')) return null;
        if (!IPAddress.TryParse(trimmed, out var ip)) return null;
        if (ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() != trimmed) return null;
        return (subnets is null ? IsAllowed(ip, allowLoopback) : IsAllowed(ip, allowLoopback, subnets)) ? ip : null;
    }
}

/// <summary>Bridge methods of the TV page and the Guide menu rows, and what a locked couch may use of them.</summary>
public static class TvPolicy
{
    /// <summary>
    /// Locked couch: reading the state, volume, mute and switching the TV on or off. They are harmless (the remote on the table does the
    /// same) and a guest should not need the code to turn the sound down or the TV off. Nothing here can reach another device or app.
    /// </summary>
    public static readonly IReadOnlySet<string> OpenWhileLocked = new HashSet<string>
    {
        "tvStatus", "tvVolume", "tvVolumeStep", "tvSetVolume", "tvMute", "tvPower"
    };

    /// <summary>
    /// Only unlocked: discovery, pairing and forgetting the TV, the manual address and MAC, and switching the input (the other input may
    /// be a console, a set-top box or a stick, i.e. a way around the couch lock and the parental controls). The input list itself is
    /// read only to offer the switch, so it is unlocked-only too.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(OpenWhileLocked)
    {
        "tvDiscover", "tvPair", "tvCancelPair", "tvForget", "tvSetMac", "tvInputs", "tvSwitchInput"
    };

    /// <summary>The action name of the parents' one-time release for switching the input (see Hud.RequireParentGrant).</summary>
    public const string ParentAction = "tvInput";

    public static bool Allowed(string method, bool locked) => All.Contains(method) && (!locked || OpenWhileLocked.Contains(method));

    /// <summary>The host's own lock check at the start of every TV call (not only the bridge's): <see cref="Guard.LockedException"/> otherwise.</summary>
    public static void Admit(string method, bool locked, string lockedMessage)
    {
        if (!All.Contains(method)) throw new InvalidOperationException($"unknown method {method}");
        if (!Allowed(method, locked)) throw new Guard.LockedException(lockedMessage);
    }

    /// <summary>
    /// Switching the input: the id is checked first (a parents' release is never spent on an id the TV cannot have), then
    /// <paramref name="requireRelease"/> (unlock and, with parental controls, the release for exactly this input) runs with it.
    /// </summary>
    public static string AdmitInputSwitch(string? inputId, Action<string> requireRelease)
    {
        if (inputId is null || !TvInput.IsValidId(inputId)) throw new ArgumentException("input");
        requireRelease(inputId);
        return inputId;
    }
}
