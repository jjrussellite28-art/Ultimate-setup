using System.Runtime.InteropServices;
using CouchHud.Core.Security;

namespace CouchHud.Shell.Services;

/// <summary>
/// The real <see cref="ISessionSource"/>: lock and unlock of this session from WTSRegisterSessionNotification at a message-only
/// window with a message loop of its own, and the lock state read from WTSQuerySessionInformation (WTSSessionInfoEx).
/// Deliberately not <c>SystemEvents.SessionSwitch</c>: that one posts its events to the synchronization context of the thread that
/// subscribed, and on the UI thread of a window that is busy (or waiting for exactly this event) nothing ever arrives.
/// </summary>
public sealed class WtsSessionSource : ISessionSource, IDisposable
{
    private const uint WmWtsSessionChange = 0x02B1, WmClose = 0x0010, WmDestroy = 0x0002;
    private const int WtsSessionLock = 7, WtsSessionUnlock = 8;
    private const int HwndMessage = -3;
    private const int WtsSessionInfoEx = 25;

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string name, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private readonly WndProc _proc;
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _hwnd;

    public event Action<bool>? LockChanged;

    /// <summary>The window and its loop are up (the poll works without them either way).</summary>
    public bool Listening => _hwnd != IntPtr.Zero;

    public WtsSessionSource()
    {
        _proc = OnMessage;
        _thread = new Thread(Loop) { IsBackground = true, Name = "CouchHud session notifications" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmWtsSessionChange:
                var what = wParam.ToInt32();
                if (what is WtsSessionLock or WtsSessionUnlock)
                {
                    try
                    {
                        LockChanged?.Invoke(what == WtsSessionLock);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        Console.Error.WriteLine($"[session] {ex.Message}");
                    }
                }
                return IntPtr.Zero;
            case WmClose:
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void Loop()
    {
        var className = "CouchHudSessionWatcher" + Environment.ProcessId;
        var instance = GetModuleHandleW(null);
        try
        {
            var cls = new WndClassEx { Size = Marshal.SizeOf<WndClassEx>(), WndProc = Marshal.GetFunctionPointerForDelegate(_proc), Instance = instance, ClassName = className };
            if (RegisterClassExW(ref cls) == 0)
            {
                Console.Error.WriteLine($"[session] window class: {Marshal.GetLastWin32Error()}");
                return;
            }
            var hwnd = CreateWindowExW(0, className, "CouchHud session", 0, 0, 0, 0, 0, new IntPtr(HwndMessage), IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Console.Error.WriteLine($"[session] window: {Marshal.GetLastWin32Error()}");
                return;
            }
            if (!WTSRegisterSessionNotification(hwnd, 0)) Console.Error.WriteLine($"[session] notification: {Marshal.GetLastWin32Error()}");
            _hwnd = hwnd;
            _ready.Set();
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            WTSUnRegisterSessionNotification(hwnd);
            UnregisterClassW(className, instance);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"[session] {ex.Message}");
        }
        finally
        {
            _ready.Set();
        }
    }

    /// <summary>The lock state of this session, read from Windows; null when it cannot be told.</summary>
    public bool? IsLocked()
    {
        try
        {
            // WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION. WTSINFOEXW: Level, then (8-aligned) SessionId, SessionState, SessionFlags.
            if (!WTSQuerySessionInformationW(IntPtr.Zero, 0xFFFFFFFF, WtsSessionInfoEx, out var buffer, out var bytes) || bytes < 20) return null;
            try
            {
                if (Marshal.ReadInt32(buffer, 0) != 1) return null;
                return Marshal.ReadInt32(buffer, 16) switch
                {
                    0 => true, // WTS_SESSIONSTATE_LOCK
                    1 => false, // WTS_SESSIONSTATE_UNLOCK
                    _ => null
                };
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        var hwnd = _hwnd;
        _hwnd = IntPtr.Zero;
        if (hwnd != IntPtr.Zero) PostMessageW(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
