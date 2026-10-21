using CouchHud.Core.Localization;
namespace CouchHud.Core.Platform;

/// <summary>How a network is secured, as far as CouchHud can connect to it without a profile file.</summary>
public enum WifiSecurity
{
    Open,
    Wep,
    Psk,
    /// <summary>WPA/WPA2/WPA3 Enterprise (802.1X): needs a certificate or user name, not supported here.</summary>
    Enterprise,
    Unsupported
}

/// <summary>A network as the adapter reports it, before duplicates (several access points) are folded.</summary>
public sealed record RawWifiNetwork(string Ssid, int Signal, string Auth, bool Connected);

/// <summary>A network in the list: SSID, signal 0-100, security, connected.</summary>
public sealed record WifiNetworkEntry(string Ssid, int Signal, bool Secured, bool Connected, string Security, bool Supported);

/// <summary>Pure logic for the WLAN page: list cleanup, signal conversion and password checks.</summary>
public static class Wifi
{
    /// <summary>Maps the name of the WinRT NetworkAuthenticationType.</summary>
    public static WifiSecurity Classify(string authenticationType) => authenticationType switch
    {
        "None" or "Open80211" or "Owe" => WifiSecurity.Open,
        "SharedKey80211" => WifiSecurity.Wep,
        "WpaPsk" or "RsnaPsk" or "Wpa3Sae" => WifiSecurity.Psk,
        "Wpa" or "Rsna" or "Wpa3" or "Wpa3Enterprise192Bits" or "Wpa3Enterprise" => WifiSecurity.Enterprise,
        _ => WifiSecurity.Unsupported
    };

    /// <summary>One entry per SSID (strongest access point), hidden networks dropped, connected first, then by signal.</summary>
    public static IReadOnlyList<WifiNetworkEntry> Arrange(IEnumerable<RawWifiNetwork> networks) =>
        networks
            .Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
            .GroupBy(n => n.Ssid, StringComparer.Ordinal)
            .Select(g =>
            {
                var best = g.OrderByDescending(n => n.Signal).First();
                var kind = Classify(best.Auth);
                return new WifiNetworkEntry(g.Key, Math.Clamp(best.Signal, 0, 100), kind != WifiSecurity.Open, g.Any(n => n.Connected),
                    kind.ToString().ToLowerInvariant(), kind is WifiSecurity.Open or WifiSecurity.Wep or WifiSecurity.Psk);
            })
            .OrderByDescending(n => n.Connected)
            .ThenByDescending(n => n.Signal)
            .ThenBy(n => n.Ssid, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Windows' usual mapping: -50 dBm or better is 100 %, -100 dBm or worse is 0 %.</summary>
    public static int SignalPercent(int rssiDbm) => Math.Clamp(2 * (rssiDbm + 100), 0, 100);

    /// <summary>Only open networks need no password.</summary>
    public static bool IsSecured(string authenticationType) => Classify(authenticationType) != WifiSecurity.Open;

    /// <summary>
    /// Null if the password can be tried for this kind of network. WPA passphrases are 8-63 characters or
    /// exactly 64 hex digits; WEP keys 5/13 characters or 10/26 hex digits. Empty is allowed everywhere
    /// (means: use the profile Windows already knows).
    /// </summary>
    public static string? CheckPassword(string? password, WifiSecurity security = WifiSecurity.Psk)
    {
        if (security == WifiSecurity.Enterprise) return Strings.Get("wifi.enterpriseUnsupported");
        if (security == WifiSecurity.Unsupported) return Strings.Get("wifi.encryptionUnsupported");
        if (string.IsNullOrEmpty(password)) return null;
        if (security == WifiSecurity.Wep)
        {
            var hex = password.All(Uri.IsHexDigit);
            if (password.Length is 5 or 13 || (password.Length is 10 or 26 && hex)) return null;
            return Strings.Get("wifi.wepLength");
        }
        if (password.Length == 64 && password.All(Uri.IsHexDigit)) return null;
        if (password.Length < 8) return Strings.Get("wifi.passwordMin");
        if (password.Length > 63) return Strings.Get("wifi.passwordMax");
        return null;
    }
}
