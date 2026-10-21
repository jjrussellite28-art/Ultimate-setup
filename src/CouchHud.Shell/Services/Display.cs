using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CouchHud.Shell.Services;

/// <summary>Display power: switching the panel off and keeping it on during controller use.</summary>
public static class Display
{
    private const uint EsDisplayRequired = 0x00000002;
    private const uint EsSystemRequired = 0x00000001;
    private const int WmSysCommand = 0x0112;
    private const int ScMonitorPower = 0xF170;
    private const uint MouseEventMove = 0x0001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static long _lastKeepAwake;
    private static bool _off;

    /// <summary>Resets the idle timers once; cheap enough for every button press, but throttled anyway.</summary>
    public static void KeepAwake()
    {
        var now = Environment.TickCount64;
        if (now - _lastKeepAwake < 10_000) return;
        _lastKeepAwake = now;
        SetThreadExecutionState(EsDisplayRequired | EsSystemRequired);
    }

    public static void Off(System.Windows.Window? window)
    {
        var handle = window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        _off = true;
        // 2 = power off. Any real input turns it back on; controller input goes through WakeIfOff.
        SendMessage(handle, WmSysCommand, ScMonitorPower, 2);
    }

    /// <summary>Woken some other way (keyboard, mouse, phone): the next controller press acts normally.</summary>
    public static void MarkAwake() => _off = false;

    /// <summary>True if this press only woke the display (it should not also act in the HUD).</summary>
    public static bool WakeIfOff()
    {
        if (!_off) return false;
        _off = false;
        // A zero-distance mouse move counts as user input and brings the panel back.
        var move = new KeyboardInput.INPUT { type = 0, u = new KeyboardInput.InputUnion { mi = new KeyboardInput.MOUSEINPUT { dwFlags = MouseEventMove } } };
        KeyboardInput.SendInput(1, [move], Marshal.SizeOf<KeyboardInput.INPUT>());
        SetThreadExecutionState(EsDisplayRequired);
        return true;
    }
}
