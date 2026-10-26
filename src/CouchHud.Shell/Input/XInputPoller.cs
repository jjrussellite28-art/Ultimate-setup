using CouchHud.Core.Input;
using CouchHud.Shell.Native;

namespace CouchHud.Shell.Input;

/// <summary>
/// Polls XInput on a background thread. Unlike browser gamepad input this keeps
/// working while a game has focus, which is what lets the Guide button bring
/// the HUD back from anywhere. Events are raised on the polling thread.
/// </summary>
public sealed class XInputPoller : IDisposable
{
    private const int PollMs = 16;
    // Querying an empty slot is slow, so only re-check disconnected pads now and then.
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(2);

    private delegate uint GetStateFn(uint index, out XInputState state);

    private readonly GetStateFn? _getState;
    private readonly Pad[] _pads = [new(), new(), new(), new()];
    private readonly PadMerger _merger = new();
    private Thread? _thread;
    private volatile bool _running;

    private sealed class Pad
    {
        public bool Connected;
        public DateTime LastCheck = DateTime.MinValue;
    }

    public XInputPoller()
    {
        _getState = Probe();
        _merger.MirrorDetected += (a, b) =>
            Console.Error.WriteLine($"[input] slots {a} and {b} mirror each other (likely a virtual pad from Steam Input/DS4Windows)");
    }

    public event Action<string>? Down;
    public event Action<string>? Up;
    public event Action<int, bool>? ConnectionChanged;

    public bool Available => _getState is not null;

    public void Start()
    {
        if (!Available || _running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "XInput", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(200);
    }

    /// <summary>Connected pads with their battery state ("wired", "full", "medium", "low", "empty").</summary>
    public IReadOnlyList<(int Index, string Battery)> Pads()
    {
        var result = new List<(int, string)>();
        for (var i = 0; i < _pads.Length; i++)
        {
            if (!_pads[i].Connected) continue;
            var battery = "unknown";
            try
            {
                if (NativeMethods.XInputGetBatteryInformation((uint)i, 0, out var info) == NativeMethods.ErrorSuccess)
                {
                    battery = info.BatteryType switch
                    {
                        1 => "wired",
                        0 => "unknown",
                        _ => info.BatteryLevel switch { 0 => "empty", 1 => "low", 2 => "medium", _ => "full" }
                    };
                }
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
            }
            result.Add((i, battery));
        }
        return result;
    }

    /// <summary>Short vibration on all connected pads; strength 0..1.</summary>
    public void Rumble(double strength, int milliseconds)
    {
        if (!Available) return;
        var speed = (ushort)(Math.Clamp(strength, 0, 1) * ushort.MaxValue);
        var connected = Enumerable.Range(0, _pads.Length).Where(i => _pads[i].Connected).ToList();
        if (connected.Count == 0) return;
        try
        {
            foreach (var i in connected)
            {
                var on = new XInputVibration { LeftMotorSpeed = (ushort)(speed / 2), RightMotorSpeed = speed };
                NativeMethods.XInputSetState((uint)i, ref on);
            }
            Task.Delay(milliseconds).ContinueWith(_ =>
            {
                foreach (var i in connected)
                {
                    var off = new XInputVibration();
                    NativeMethods.XInputSetState((uint)i, ref off);
                }
            }, TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }

    /// <summary>Left stick and right stick Y of the pad that moves most, -1..1 (for the mouse mode).</summary>
    public (double X, double Y, double ScrollY) Sticks() => _merger.Sticks();

    /// <summary>Right stick and triggers (mouse mode: fine pointer and scrolling).</summary>
    public (double RX, double RY, double LT, double RT) Extras() => _merger.Extras();

    public bool IsPressed(string button) => _merger.IsPressed(button);

    private static GetStateFn? Probe()
    {
        try
        {
            NativeMethods.XInputGetStateEx(0, out _);
            return NativeMethods.XInputGetStateEx;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
        try
        {
            NativeMethods.XInputGetState(0, out _);
            return NativeMethods.XInputGetState;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Console.Error.WriteLine("[xinput] not available");
            return null;
        }
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[xinput] {ex}");
            }
            Thread.Sleep(PollMs);
        }
    }

    private void Poll()
    {
        var now = DateTime.UtcNow;
        var samples = new PadSample?[_pads.Length];
        for (var i = 0; i < _pads.Length; i++)
        {
            var pad = _pads[i];
            if (!pad.Connected && now - pad.LastCheck < ReconnectInterval) continue;
            pad.LastCheck = now;

            if (_getState!((uint)i, out var state) != NativeMethods.ErrorSuccess)
            {
                if (!pad.Connected) continue;
                pad.Connected = false;
                Console.Error.WriteLine($"[input] pad {i} disconnected");
                pendingConnection.Add((i, false));
                continue;
            }
            if (!pad.Connected)
            {
                pad.Connected = true;
                Console.Error.WriteLine($"[input] pad {i} connected, {Describe(i)}");
                pendingConnection.Add((i, true));
            }
            var g = state.Gamepad;
            samples[i] = new PadSample(g.Buttons, g.LeftTrigger, g.RightTrigger, g.ThumbLX, g.ThumbLY, g.ThumbRX, g.ThumbRY);
        }

        // Disconnected pads are null, so their held buttons are released through the merger.
        foreach (var (button, down) in _merger.Update(samples))
        {
            if (down) Down?.Invoke(button);
            else Up?.Invoke(button);
        }
        foreach (var (slot, connected) in pendingConnection) ConnectionChanged?.Invoke(slot, connected);
        pendingConnection.Clear();
    }

    private readonly List<(int, bool)> pendingConnection = [];

    private string Describe(int slot)
    {
        var text = "type unknown";
        try
        {
            if (NativeMethods.XInputGetCapabilities((uint)slot, 0, out var caps) == NativeMethods.ErrorSuccess)
            {
                text = $"type {caps.SubType} (flags 0x{caps.Flags:X4})";
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
        try
        {
            if (NativeMethods.XInputGetCapabilitiesEx(1, (uint)slot, 0, out var ex2) == NativeMethods.ErrorSuccess)
            {
                text += $", vid 0x{ex2.VendorId:X4} pid 0x{ex2.ProductId:X4}";
                if (ex2.VendorId == 0x28DE) text += " (Steam virtual pad)";
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
        var battery = Pads().FirstOrDefault(p => p.Index == slot).Battery ?? "unknown";
        return $"{text}, battery {battery}";
    }
}
