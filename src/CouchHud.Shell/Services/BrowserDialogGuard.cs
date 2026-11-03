using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using CouchHud.Core.Guard;

namespace CouchHud.Shell.Services;

/// <summary>
/// The native backstop of the couch browser: while the couch rules apply (<see cref="BrowserDialogRules.Enforced"/>) a standard Windows
/// dialog (open, save, print, color) in the web view's process tree is asked to close the moment it is created or shown, whatever the page
/// did to get it (script guard bypassed, closed shadow root, a frame the script never reached). Two ways in, so that one missed event is not
/// a way out: a WinEvent hook (create, show, dialog start) and a sweep over the top-level windows twice a second. A dialog that does not
/// close is escalated (<paramref name="stubborn"/>: the HUD ends the browser, which takes its process and the dialog with it).
/// Only the browser's own tree is touched: not the HUD's own web view, not a game, and nothing in PC mode (<paramref name="enforced"/>).
/// Needs a message pump: create and Start on the UI thread.
/// </summary>
public sealed class BrowserDialogGuard : IDisposable
{
    private const uint EventSystemDialogStart = 0x0010;
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WmSysCommand = 0x0112;
    private const int ScClose = 0xF060;
    private const int ObjidWindow = 0;
    // 150 ms steps: WM_CLOSE three times, then SC_CLOSE, and after about 1.2 s the dialog counts as stubborn.
    private const int StubbornAfter = 8;
    private const uint GaRootOwner = 3;
    private const int GwlStyle = -16;
    private const long WsChild = 0x40000000L;
    private static readonly TimeSpan BlindGrace = TimeSpan.FromSeconds(8);

    private readonly Func<bool> _enforced;
    private readonly Func<int?> _browserPid;
    private readonly Func<bool> _browserOpen;
    private readonly Action<string> _log;
    private readonly Action _stubborn;
    private readonly Func<IntPtr> _host;
    private DateTime? _blindSince;
    private readonly WinEventProc _callback;
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _sweep = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<IntPtr, int> _pending = [];
    private IntPtr _hookDialog;
    private IntPtr _hookObject;
    private int _inside;

    public BrowserDialogGuard(Func<bool> enforced, Func<int?> browserPid, Func<bool> browserOpen, Action<string> log, Action stubborn, Func<IntPtr> hostWindow)
    {
        _host = hostWindow;
        _enforced = enforced;
        _browserPid = browserPid;
        _browserOpen = browserOpen;
        _log = log;
        _stubborn = stubborn;
        _callback = OnEvent;
        _retry.Tick += (_, _) => Retry();
        _sweep.Tick += (_, _) => Sweep();
    }

    /// <summary>The hook is installed. False: the browser must not open while the couch rules apply (fail closed, see Hud.OpenBrowserAsync).</summary>
    public bool IsActive => _hookObject != IntPtr.Zero && _hookDialog != IntPtr.Zero;

    public void Start()
    {
        if (IsActive) return;
        _hookDialog = SetWinEventHook(EventSystemDialogStart, EventSystemDialogStart, IntPtr.Zero, _callback, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);
        _hookObject = SetWinEventHook(EventObjectCreate, EventObjectShow, IntPtr.Zero, _callback, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);
        if (!IsActive)
        {
            _log("[browser-dialogs] hook failed");
            Stop();
            return;
        }
        _sweep.Start();
    }

    public void Stop()
    {
        _sweep.Stop();
        _retry.Stop();
        _pending.Clear();
        if (_hookDialog != IntPtr.Zero) UnhookWinEvent(_hookDialog);
        if (_hookObject != IntPtr.Zero) UnhookWinEvent(_hookObject);
        _hookDialog = _hookObject = IntPtr.Zero;
    }

    public void Dispose() => Stop();

    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != ObjidWindow) return;
        Inspect(hwnd);
    }

    private void Sweep()
    {
        try
        {
            if (!_enforced() || !_browserOpen())
            {
                _blindSince = null;
                return;
            }
            // The web view's browser process cannot be named: the couch browser cannot be told from other programs, so it does not run blind.
            if (_browserPid() is null)
            {
                _blindSince ??= DateTime.UtcNow;
                if (BrowserDialogRules.MustEndBlind(true, true, null, DateTime.UtcNow - _blindSince.Value, BlindGrace))
                {
                    _blindSince = null;
                    _log("[browser-dialogs] the web view's process is unknown: ending the browser");
                    _stubborn();
                    return;
                }
            }
            else
            {
                _blindSince = null;
            }
            EnumWindows((hwnd, _) =>
            {
                if (IsWindowVisible(hwnd)) Inspect(hwnd);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log($"[browser-dialogs] sweep: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Inspect(IntPtr hwnd)
    {
        if (_inside > 0) return;
        _inside++;
        string? name = null;
        var windowClass = "";
        try
        {
            if (!_enforced() || !_browserOpen()) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return;
            // Only the web view's processes have windows of the browser; the name is the cheap first filter, the tree decides. A
            // process whose name cannot be read goes on to the tree.
            name = ProcessName((int)pid);
            if (name is not null && !LockedForeground.IsOwnWebViewHelper(name)) return;
            windowClass = ClassName(hwnd);
            var window = Facts(hwnd, windowClass);
            if (!window.TopLevel && windowClass != BrowserDialogRules.DialogClass) return;
            var tree = Attribute((int)pid);
            if (BrowserDialogRules.Decide(true, true, window, name, tree) != BrowserDialogAction.Close) return;
            Close(hwnd, name, windowClass);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A dialog of a web view process that we could not judge is closed only when it belongs to the HUD's own window chain (the
            // couch browser's); another program's WebView2 app keeps its dialogs.
            _log($"[browser-dialogs] {ex.GetType().Name}: {ex.Message}");
            try
            {
                if (LockedForeground.IsOwnWebViewHelper(name) && windowClass == BrowserDialogRules.DialogClass && Facts(hwnd, windowClass).OwnedByHost) Close(hwnd, name, windowClass);
            }
            catch (Exception inner) when (inner is not OutOfMemoryException)
            {
                _log($"[browser-dialogs] {inner.GetType().Name}: {inner.Message}");
            }
        }
        finally
        {
            _inside--;
        }
    }

    private BrowserTree Attribute(int pid)
    {
        try
        {
            Dictionary<int, int>? parents = null;
            return BrowserDialogRules.Attribute(pid, _browserPid(), OwnLaunches.CanRead(pid),
                p => (parents ??= OwnLaunches.ParentMap()).TryGetValue(p, out var parent) ? parent : null,
                OwnLaunches.StartTime);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return BrowserTree.Unknown;
        }
    }

    /// <summary>What the rules need to know about one window; the owner chain (GA_ROOTOWNER) must lead to the HUD's own window.</summary>
    private BrowserDialogRules.BrowserWindow Facts(IntPtr hwnd, string windowClass)
    {
        var host = _host();
        var hostOwner = host == IntPtr.Zero ? IntPtr.Zero : GetAncestor(host, GaRootOwner);
        var owner = GetAncestor(hwnd, GaRootOwner);
        var owned = hostOwner != IntPtr.Zero && owner == hostOwner && hwnd != hostOwner;
        var topLevel = (GetWindowLong(hwnd, GwlStyle) & WsChild) == 0;
        return new(windowClass, IsWindowVisible(hwnd), topLevel, owned);
    }

    private void Close(IntPtr hwnd, string? name, string windowClass)
    {
        if (_pending.ContainsKey(hwnd)) return;
        _log($"[browser-dialogs] closing a window of {name ?? "?"} ({windowClass})");
        PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        _pending[hwnd] = 0;
        _retry.Start();
    }

    private void Retry()
    {
        var stubborn = false;
        foreach (var hwnd in _pending.Keys.ToList())
        {
            if (!IsWindow(hwnd))
            {
                _pending.Remove(hwnd);
                continue;
            }
            var tries = _pending[hwnd] + 1;
            if (tries >= StubbornAfter)
            {
                _pending.Remove(hwnd);
                stubborn = true;
                continue;
            }
            _pending[hwnd] = tries;
            if (tries < 4) PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            else PostMessage(hwnd, WmSysCommand, new IntPtr(ScClose), IntPtr.Zero);
        }
        if (_pending.Count == 0) _retry.Stop();
        if (!stubborn) return;
        _log("[browser-dialogs] a dialog does not close: ending the browser");
        try
        {
            _stubborn();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log($"[browser-dialogs] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongRaw(IntPtr hwnd, int index);

    private static long GetWindowLong(IntPtr hwnd, int index) => (uint)GetWindowLongRaw(hwnd, index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
