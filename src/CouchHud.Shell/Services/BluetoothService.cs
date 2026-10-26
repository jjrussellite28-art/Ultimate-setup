using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

public sealed record BluetoothDeviceInfo(string Id, string Name, bool Paired, bool Connected, bool LowEnergy, string Kind);

public sealed record BluetoothBatteryInfo(string Name, string Kind, int Percent);

/// <summary>
/// Bluetooth from the couch: list, pair and remove headsets and controllers, and turn
/// the radio on or off. Pairing accepts confirm-only and PIN-display requests, which
/// covers headsets, Xbox and PlayStation controllers.
/// </summary>
/// <param name="confirm">Shows a question on the TV (PIN to type or to compare); true to go on.</param>
public sealed class BluetoothService(Func<string, Task<bool>> confirm)
{
    // Association endpoint protocol ids for Bluetooth classic and Bluetooth LE.
    private const string ClassicProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private const string LowEnergyProtocol = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";

    private static readonly string[] Properties =
    [
        "System.Devices.Aep.IsPaired",
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.Category",
        "System.Devices.Aep.ProtocolId"
    ];

    // The battery level Windows keeps for a Bluetooth device (what Settings › Bluetooth shows).
    private const string BatteryProperty = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    /// <summary>
    /// Battery percent of paired, connected Bluetooth devices that report it to Windows (most headsets, and Xbox or PlayStation
    /// controllers over Bluetooth). Devices that report nothing are left out.
    /// </summary>
    public async Task<IReadOnlyList<BluetoothBatteryInfo>> BatteriesAsync()
    {
        var found = new Dictionary<string, BluetoothBatteryInfo>();
        string[] selectors =
        [
            Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)
        ];
        foreach (var selector in selectors)
        {
            try
            {
                var devices = await DeviceInformation.FindAllAsync(selector, [BatteryProperty, "System.Devices.Connected"]);
                foreach (var d in devices)
                {
                    if (string.IsNullOrWhiteSpace(d.Name)) continue;
                    if (!d.Properties.TryGetValue(BatteryProperty, out var raw) || raw is null) continue;
                    if (d.Properties.TryGetValue("System.Devices.Connected", out var c) && c is bool connected && !connected) continue;
                    var percent = Convert.ToInt32(raw);
                    if (percent is < 0 or > 100) continue;
                    found[d.Name] = new BluetoothBatteryInfo(d.Name, Kind(d.Name), percent);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"[bluetooth] battery: {ex.Message}");
            }
        }
        return found.Values.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<object> StateAsync()
    {
        var radio = await RadioAsync();
        return new { available = radio is not null, on = radio?.State == RadioState.On };
    }

    public async Task SetRadioAsync(bool on)
    {
        if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) throw new InvalidOperationException(Strings.Get("bluetooth.notAllowed"));
        var radio = await RadioAsync() ?? throw new InvalidOperationException(Strings.Get("bluetooth.noAdapter"));
        var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        if (result != RadioAccessStatus.Allowed) throw new InvalidOperationException(Strings.Get("bluetooth.switchFailed"));
    }

    /// <summary>Paired devices plus whatever is in pairing mode nearby (searches for a few seconds).</summary>
    public async Task<IReadOnlyList<BluetoothDeviceInfo>> ScanAsync(TimeSpan duration)
    {
        var found = new Dictionary<string, BluetoothDeviceInfo>();
        var gate = new object();
        var query = $"(System.Devices.Aep.ProtocolId:=\"{ClassicProtocol}\" OR System.Devices.Aep.ProtocolId:=\"{LowEnergyProtocol}\")";
        var watcher = DeviceInformation.CreateWatcher(query, Properties, DeviceInformationKind.AssociationEndpoint);
        var done = new TaskCompletionSource();

        watcher.Added += (_, info) =>
        {
            if (ToInfo(info) is { } device) lock (gate) found[device.Id] = device;
        };
        watcher.Updated += (_, update) =>
        {
            lock (gate)
            {
                if (!found.TryGetValue(update.Id, out var known)) return;
                var props = update.Properties;
                found[update.Id] = known with
                {
                    Paired = props.TryGetValue("System.Devices.Aep.IsPaired", out var p) && p is bool paired ? paired : known.Paired,
                    Connected = props.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is bool connected ? connected : known.Connected
                };
            }
        };
        watcher.Removed += (_, update) =>
        {
            lock (gate) found.Remove(update.Id);
        };
        watcher.EnumerationCompleted += (_, _) => { };
        watcher.Stopped += (_, _) => done.TrySetResult();

        watcher.Start();
        await Task.Delay(duration);
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            watcher.Stop();
            await Task.WhenAny(done.Task, Task.Delay(2000));
        }

        lock (gate)
        {
            return found.Values
                .Where(d => d.Name.Length > 0)
                .OrderByDescending(d => d.Connected)
                .ThenByDescending(d => d.Paired)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public async Task<string> PairAsync(string id)
    {
        var device = await DeviceInformation.CreateFromIdAsync(id, Properties, DeviceInformationKind.AssociationEndpoint);
        if (device.Pairing.IsPaired) return Strings.Get("bluetooth.alreadyPaired", device.Name);
        if (!device.Pairing.CanPair) throw new InvalidOperationException(Strings.Get("bluetooth.cannotPair", device.Name));

        var custom = device.Pairing.Custom;
        async void OnRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                switch (args.PairingKind)
                {
                    case DevicePairingKinds.ProvidePin:
                        // Older headsets: the PIN is almost always 0000.
                        args.Accept("0000");
                        break;
                    case DevicePairingKinds.DisplayPin:
                        // Keyboards: the user types this PIN on the device.
                        if (await confirm(Strings.Get("bluetooth.typePin", args.Pin))) args.Accept();
                        break;
                    case DevicePairingKinds.ConfirmPinMatch:
                        if (await confirm(Strings.Get("bluetooth.matchPin", device.Name, args.Pin))) args.Accept();
                        break;
                    default:
                        args.Accept();
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[bluetooth] pairing request: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        }
        custom.PairingRequested += OnRequested;
        try
        {
            var result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ProvidePin | DevicePairingKinds.ConfirmPinMatch);
            return result.Status switch
            {
                DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired => $"{device.Name} gekoppelt",
                DevicePairingResultStatus.AuthenticationTimeout or DevicePairingResultStatus.ConnectionRejected => throw new InvalidOperationException(Strings.Get("bluetooth.noAnswer", device.Name)),
                _ => throw new InvalidOperationException(Strings.Get("bluetooth.pairFailed", result.Status))
            };
        }
        finally
        {
            custom.PairingRequested -= OnRequested;
        }
    }

    public async Task<string> UnpairAsync(string id)
    {
        var device = await DeviceInformation.CreateFromIdAsync(id, Properties, DeviceInformationKind.AssociationEndpoint);
        var result = await device.Pairing.UnpairAsync();
        return result.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired
            ? $"{device.Name} entfernt"
            : throw new InvalidOperationException(Strings.Get("bluetooth.removeFailed", result.Status));
    }

    private static async Task<Radio?> RadioAsync()
    {
        try
        {
            return (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static BluetoothDeviceInfo? ToInfo(DeviceInformation info)
    {
        var props = info.Properties;
        var paired = props.TryGetValue("System.Devices.Aep.IsPaired", out var p) && p is bool b && b;
        var connected = props.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is bool bc && bc;
        var lowEnergy = props.TryGetValue("System.Devices.Aep.ProtocolId", out var proto) && proto is Guid g && g == Guid.Parse(LowEnergyProtocol);
        return new BluetoothDeviceInfo(info.Id, info.Name ?? "", paired, connected, lowEnergy, Kind(info.Name ?? ""));
    }

    /// <summary>A rough icon category from the name: good enough for a list on the TV.</summary>
    private static string Kind(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("wireless controller") || n.Contains("8bitdo") || n.Contains("joy-con") || n.Contains("pro controller")) return "controller";
        if (n.Contains("headset") || n.Contains("buds") || n.Contains("airpods") || n.Contains("wh-") || n.Contains("wf-") || n.Contains("headphone") || n.Contains("kopfhörer") || n.Contains("bose") || n.Contains("jbl") || n.Contains("sennheiser")) return "headphones";
        if (n.Contains("keyboard") || n.Contains("tastatur")) return "keyboard";
        if (n.Contains("mouse") || n.Contains("maus")) return "mouse";
        if (n.Contains("speaker") || n.Contains("soundbar") || n.Contains("box")) return "speaker";
        if (n.Contains("phone") || n.Contains("iphone") || n.Contains("galaxy") || n.Contains("pixel")) return "phone";
        return "other";
    }
}
