using System.Runtime.InteropServices;

namespace CouchHud.Shell.Services;

/// <summary>
/// Volume keys of keyboards and remotes. Explorer normally answers them (volume bar,
/// mute); as the shell nobody does, so a low-level hook hands them to CouchHud's
/// audio service. As soon as a taskbar exists, Windows keeps its own handling.
/// The hook has a thread of its own with a message loop, so a busy UI thread can never
/// stall the keyboard of the whole session; the callback only passes the key on and returns.
/// </summary>
public sealed class MediaKeys : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, WmQuit = 0x0012;
    private const int VkVolumeMute = 0xAD, VkVolumeDown = 0xAE, VkVolumeUp = 0xAF;
    private const int VkSnapshot = 0x2C;
    private const uint PmNoRemove = 0;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    // Kept in a field: the hook would call into a collected delegate otherwise.
    private readonly HookProc _proc;
    private readonly Action<int> _onKey;
    private readonly Action? _onSnip;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;

    /// <param name="onKey">Called on the hook thread with +2 / -2 for volume up / down and 0 for mute; hand over to the UI thread there.</param>
    /// <param name="onSnip">Called on the hook thread when Print Screen goes down (the key is taken, Windows' own snipping does not start).</param>
    public MediaKeys(Action<int> onKey, Action? onSnip = null)
    {
        _onKey = onKey;
        _onSnip = onSnip;
        _proc = Callback;
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "CouchHud media keys" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void Loop()
    {
        try
        {
            _threadId = NativeThreadId();
            // Creates the message queue of this thread, so Dispose can post the quit message.
            PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
            _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Console.Error.WriteLine($"[keys] volume key hook failed: {Marshal.GetLastWin32Error()}");
            _ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A thread of its own: an exception here would end the whole process.
            Console.Error.WriteLine($"[keys] volume key thread: {ex.GetType().Name}: {ex.Message}");
            _ready.Set();
        }
    }

    private static uint NativeThreadId() => Native.NativeMethods.GetCurrentThreadId();

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            // KBDLLHOOKSTRUCT starts with the virtual key code.
            var vk = Marshal.ReadInt32(lParam);
            if (vk == VkSnapshot && _onSnip is not null)
            {
                if ((int)wParam is WmKeyDown or WmSysKeyDown)
                {
                    try { _onSnip(); }
                    catch (Exception ex) { Console.Error.WriteLine($"[keys] {ex.Message}"); }
                }
                return 1;
            }
            if (vk is VkVolumeMute or VkVolumeDown or VkVolumeUp && !ShellManager.IsDesktopShellRunning())
            {
                if ((int)wParam is WmKeyDown or WmSysKeyDown)
                {
                    try
                    {
                        _onKey(vk == VkVolumeUp ? 2 : vk == VkVolumeDown ? -2 : 0);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[keys] {ex.Message}");
                    }
                }
                return 1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        var thread = _thread;
        if (thread is null) return;
        _thread = null;
        PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(1));
    }
}
