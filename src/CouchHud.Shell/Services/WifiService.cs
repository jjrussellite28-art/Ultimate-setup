using System.Net.NetworkInformation;
using System.Net.Sockets;
using CouchHud.Core.Platform;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

/// <summary>What the WLAN page shows without scanning.</summary>
public sealed record WifiState(bool Available, string Access, string? Ssid, string? Ip, string? Message);

/// <summary>Outcome of a connect try; NeedsPassword asks the UI to open the keyboard.</summary>
public sealed record WifiConnectResult(bool Connected, bool NeedsPassword, string Message);

/// <summary>
/// WLAN from the couch through Windows.Devices.WiFi: no admin rights and no profile files needed,
/// Windows stores the network itself. A scan needs the location permission (Windows 11 24H2 asks
/// for it for every WLAN scan), which is checked first so the page can explain what to switch on.
/// </summary>
public sealed class WifiService
{
    private static string LocationHint => Strings.Get("wifi.locationHint");

    private WiFiAdapter? _adapter;
    private string? _adapterId;
    private WiFiAccessStatus? _access;
    private Dictionary<string, WiFiAvailableNetwork> _lastScan = new(StringComparer.Ordinal);
    private Dictionary<string, WifiSecurity> _security = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads the state without the location permission: adapter presence through device enumeration,
    /// the connected SSID through the connection profiles. Opening the page therefore never asks anything.
    /// </summary>
    public async Task<WifiState> StateAsync()
    {
        var id = await AdapterIdAsync();
        if (id is null) return new WifiState(false, "none", null, null, Strings.Get("wifi.noAdapter"));
        var access = _access ?? WiFiAccessStatus.Unspecified;
        var denied = access is WiFiAccessStatus.DeniedByUser or WiFiAccessStatus.DeniedBySystem;
        var ssid = ConnectedSsid();
        return new WifiState(true, AccessName(access), ssid, ssid is null ? null : WirelessIp(), denied ? AccessMessage(access) : null);
    }

    public async Task<IReadOnlyList<WifiNetworkEntry>> ScanAsync()
    {
        var adapter = await AdapterAsync() ?? throw NoAdapter();
        if (_access != WiFiAccessStatus.Allowed) throw new InvalidOperationException(AccessMessage(_access ?? WiFiAccessStatus.Unspecified));

        try
        {
            await Timed(adapter.ScanAsync().AsTask(), "WLAN-Suche");
        }
        catch (UnauthorizedAccessException)
        {
            // Location switched off while the page was open.
            _access = WiFiAccessStatus.DeniedBySystem;
            throw new InvalidOperationException(LocationHint);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            throw new InvalidOperationException(Strings.Get("wifi.scanFailed", ex.Message));
        }

        var connected = ConnectedSsid();
        var networks = adapter.NetworkReport.AvailableNetworks.Where(n => !n.IsWiFiDirect).ToList();
        _lastScan = networks
            .Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
            .GroupBy(n => n.Ssid, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(n => n.NetworkRssiInDecibelMilliwatts).First(), StringComparer.Ordinal);
        _security = networks
            .Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
            .GroupBy(n => n.Ssid, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Wifi.Classify(_lastScan[g.Key].SecuritySettings.NetworkAuthenticationType.ToString()), StringComparer.Ordinal);
        return Wifi.Arrange(networks.Select(n => new RawWifiNetwork(
            n.Ssid,
            Wifi.SignalPercent((int)Math.Round(n.NetworkRssiInDecibelMilliwatts)),
            n.SecuritySettings.NetworkAuthenticationType.ToString(),
            connected is not null && n.Ssid == connected)));
    }

    /// <summary>Connects; an empty password uses the profile Windows already knows. Never logs the password.</summary>
    public async Task<WifiConnectResult> ConnectAsync(string ssid, string? password)
    {
        var adapter = await AdapterAsync() ?? throw NoAdapter();
        if (_access != WiFiAccessStatus.Allowed) throw new InvalidOperationException(AccessMessage(_access ?? WiFiAccessStatus.Unspecified));
        if (!_lastScan.TryGetValue(ssid, out var network))
        {
            await ScanAsync();
            if (!_lastScan.TryGetValue(ssid, out network)) throw new InvalidOperationException(Strings.Get("wifi.outOfRange", ssid));
        }

        var security = _security.GetValueOrDefault(ssid, WifiSecurity.Psk);
        if (Wifi.CheckPassword(password, security) is { } problem) throw new InvalidOperationException(problem);

        var result = string.IsNullOrEmpty(password)
            ? await Timed(adapter.ConnectAsync(network, WiFiReconnectionKind.Automatic).AsTask(), "Verbinden", 40)
            : await Timed(adapter.ConnectAsync(network, WiFiReconnectionKind.Automatic, new PasswordCredential { Password = password }).AsTask(), "Verbinden", 40);
        if (result.ConnectionStatus == WiFiConnectionStatus.InvalidCredential)
        {
            return string.IsNullOrEmpty(password) && security != WifiSecurity.Open
                ? new WifiConnectResult(false, true, Strings.Get("wifi.unknownNetwork"))
                : new WifiConnectResult(false, false, Strings.Get("wifi.wrongPassword"));
        }
        var message = result.ConnectionStatus switch
        {
            WiFiConnectionStatus.Success => Strings.Get("wifi.connected", ssid),
            WiFiConnectionStatus.NetworkNotAvailable => Strings.Get("wifi.outOfRange", ssid),
            WiFiConnectionStatus.Timeout => Strings.Get("wifi.connectTimeout"),
            WiFiConnectionStatus.AccessRevoked => Strings.Get("wifi.denied"),
            WiFiConnectionStatus.UnsupportedAuthenticationProtocol => Strings.Get("wifi.encryptionUnsupported"),
            _ => Strings.Get("wifi.connectFailed")
        };
        return new WifiConnectResult(result.ConnectionStatus == WiFiConnectionStatus.Success, false, message);
    }

    public async Task<string> DisconnectAsync()
    {
        var adapter = await AdapterAsync() ?? throw NoAdapter();
        adapter.Disconnect();
        return "WLAN getrennt";
    }

    private InvalidOperationException NoAdapter() => new(_access is null or WiFiAccessStatus.Allowed
        ? Strings.Get("wifi.noAdapter")
        : AccessMessage(_access.Value));

    /// <summary>Device id of the first WLAN adapter, or null. Needs no permission; cached once found.</summary>
    private async Task<string?> AdapterIdAsync()
    {
        if (_adapterId is not null) return _adapterId;
        try
        {
            var infos = await Timed(DeviceInformation.FindAllAsync(WiFiAdapter.GetDeviceSelector()).AsTask(), Strings.Get("wifi.findAdapter"));
            return _adapterId = infos.FirstOrDefault()?.Id;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[wifi] {ex.Message}");
            throw new InvalidOperationException(Strings.Get("wifi.statusUnreadable", ex.Message));
        }
    }

    /// <summary>The adapter object; asks for the location permission once (with a timeout), only when really needed.</summary>
    private async Task<WiFiAdapter?> AdapterAsync()
    {
        var id = await AdapterIdAsync();
        if (id is null) return null;
        try
        {
            if (_access != WiFiAccessStatus.Allowed)
                _access = await Timed(WiFiAdapter.RequestAccessAsync().AsTask(), "Standortabfrage", 15);
            if (_access != WiFiAccessStatus.Allowed) return null;
            return _adapter ??= await Timed(WiFiAdapter.FromIdAsync(id).AsTask(), Strings.Get("wifi.openAdapter"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[wifi] {ex.Message}");
            throw new InvalidOperationException(Strings.Get("wifi.statusUnreadable", ex.Message));
        }
    }

    /// <summary>No WinRT call may hang the page: a clear error after the timeout.</summary>
    private static async Task<T> Timed<T>(Task<T> task, string what, int seconds = 10)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(Strings.Get("wifi.noResponse", what));
        }
    }

    private static async Task Timed(Task task, string what, int seconds = 10) =>
        await Timed(Task.Run(async () => { await task; return true; }), what, seconds);

    private static string? ConnectedSsid()
    {
        try
        {
            foreach (var profile in NetworkInformation.GetConnectionProfiles())
            {
                if (!profile.IsWlanConnectionProfile) continue;
                if (profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None) continue;
                var ssid = profile.WlanConnectionProfileDetails.GetConnectedSsid();
                if (!string.IsNullOrEmpty(ssid)) return ssid;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
        return null;
    }

    private static string? WirelessIp()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static string AccessName(WiFiAccessStatus status) => status switch
    {
        WiFiAccessStatus.Allowed => "allowed",
        WiFiAccessStatus.DeniedByUser => "denied",
        WiFiAccessStatus.DeniedBySystem => "denied",
        _ => "unspecified"
    };

    private static string AccessMessage(WiFiAccessStatus status) => status switch
    {
        WiFiAccessStatus.DeniedByUser or WiFiAccessStatus.DeniedBySystem => LocationHint,
        _ => Strings.Get("wifi.accessNotGranted") + LocationHint
    };
}
