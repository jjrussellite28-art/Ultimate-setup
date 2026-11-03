using System.Diagnostics;
using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Shell.Native;
using Microsoft.Win32;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

/// <summary>Process paths and Steam's running app for the game session tracker.</summary>
public sealed class Win32ProcessProbe : IProcessProbe
{
    public IReadOnlyList<(int Pid, string? Path)> Snapshot()
    {
        var result = new List<(int, string?)>();
        var buffer = new char[1024];
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                result.Add((p.Id, ImagePath(p.Id, buffer)));
            }
        }
        return result;
    }

    /// <summary>
    /// QueryFullProcessImageName with limited rights works for most processes,
    /// unlike Process.MainModule which throws for anything elevated.
    /// </summary>
    internal static string? ImagePathOf(int pid, char[] buffer) => ImagePath(pid, buffer);

    private static string? ImagePath(int pid, char[] buffer)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var size = (uint)buffer.Length;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public IReadOnlyDictionary<int, int> ParentMap() => OwnLaunches.ParentMap();

    public IReadOnlyDictionary<int, long> StartTimes() => Win32ProcessControl.AllStartTimes();

    public int SteamRunningAppId()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("RunningAppID") is int id ? id : 0;
    }

    /// <summary>
    /// Brings the main window of the given processes to the front (see <see cref="GameFront.PickWindow"/>): only a window the program
    /// really shows, judged by its normal size even while minimized, and with the same means CouchHUD uses for itself
    /// (<see cref="ForceForeground"/>), because a plain SetForegroundWindow is refused by Windows when the last press came from the
    /// controller and not from Windows' own input. True when the window is in front afterwards.
    /// </summary>
    public static bool Activate(IReadOnlyCollection<int> pids) => PickAndFront(pids, out _) == GameFocusResult.InFront;

    /// <summary>
    /// The game's window, handed the front (the HUD's Resume game). Logs nothing about the window (its title may name a profile or a server).
    /// A full-screen game coming back from minimized may answer late: one more try after a moment, awaited, so the UI thread keeps running.
    /// </summary>
    public static async Task<GameFocusResult> ActivateGameAsync(IReadOnlyCollection<int> pids)
    {
        var result = PickAndFront(pids, out var hwnd);
        if (result != GameFocusResult.Refused) return result;
        await Task.Delay(150);
        return ForceForeground(hwnd) ? GameFocusResult.InFront : GameFocusResult.Refused;
    }

    /// <summary>Whether the processes show a window that could take the front (read-only: nothing is changed).</summary>
    public static bool HasGameWindow(IReadOnlyCollection<int> pids)
    {
        var real = pids.Where(p => p > 0).ToHashSet();
        return real.Count > 0 && GameFront.PickWindow(GameWindows(real), real) is not null;
    }

    /// <summary>The handle of the window <see cref="HasGameWindow"/> counts (the game's main window: see GameFront.PickWindow), or 0. Read-only.</summary>
    public static long GameMainWindow(IReadOnlyCollection<int> pids)
    {
        var real = pids.Where(p => p > 0).ToHashSet();
        return real.Count == 0 ? 0 : GameFront.PickWindow(GameWindows(real), real)?.Handle ?? 0;
    }

    private static GameFocusResult PickAndFront(IReadOnlyCollection<int> pids, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        var real = pids.Where(p => p > 0).ToHashSet();
        if (real.Count == 0) return GameFocusResult.NoWindow;
        var window = GameFront.PickWindow(GameWindows(real), real);
        if (window is null) return GameFocusResult.NoWindow;
        hwnd = new IntPtr(window.Handle);
        // CouchHUD is in front right now and may pass the right on: the game's process gets it before the switch.
        NativeMethods.AllowSetForegroundWindow(window.Pid);
        return ForceForeground(hwnd) ? GameFocusResult.InFront : GameFocusResult.Refused;
    }

    /// <summary>The top-level windows of these processes, as <see cref="GameFront.PickWindow"/> needs them.</summary>
    private static List<GameWindow> GameWindows(HashSet<int> pids)
    {
        var list = new List<GameWindow>();
        var z = 0;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            z++;
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (!pids.Contains(pid)) return true;
            const int GwlExStyle = -20, WsExToolWindow = 0x80;
            var cloaked = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DwmwaCloaked, out var c, sizeof(int)) == 0 && c != 0;
            var minimized = NativeMethods.IsIconic(hWnd);
            int width = 0, height = 0;
            if (minimized)
            {
                var placement = new NativeMethods.WindowPlacement { Length = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WindowPlacement>() };
                if (NativeMethods.GetWindowPlacement(hWnd, ref placement))
                {
                    width = placement.NormalPosition.Right - placement.NormalPosition.Left;
                    height = placement.NormalPosition.Bottom - placement.NormalPosition.Top;
                }
            }
            else if (NativeMethods.GetWindowRect(hWnd, out var r))
            {
                width = r.Right - r.Left;
                height = r.Bottom - r.Top;
            }
            list.Add(new GameWindow(hWnd.ToInt64(), pid, NativeMethods.IsWindowVisible(hWnd), cloaked,
                ToolWindow: (NativeMethods.GetWindowLong(hWnd, GwlExStyle) & WsExToolWindow) != 0,
                Owned: NativeMethods.GetWindow(hWnd, 4 /* GW_OWNER */) != IntPtr.Zero,
                minimized, width, height, Titled: NativeMethods.GetWindowTextLength(hWnd) > 0, z));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Visible, titled, unowned top-level windows of other processes (what a user would call an app window).</summary>
    public static HashSet<IntPtr> TopLevelWindows()
    {
        var set = new HashSet<IntPtr>();
        var self = Environment.ProcessId;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == self) return true;
            const int GwlExStyle = -20, WsExToolWindow = 0x80;
            if ((NativeMethods.GetWindowLong(hWnd, GwlExStyle) & WsExToolWindow) != 0) return true;
            if (NativeMethods.GetWindow(hWnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            if (NativeMethods.GetWindowTextLength(hWnd) == 0) return true;
            if (!NativeMethods.GetWindowRect(hWnd, out var r) || r.Right - r.Left < 100 || r.Bottom - r.Top < 60) return true;
            set.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return set;
    }

    /// <summary>
    /// After a URL/protocol start the owning process is unknown (the browser may already run): waits for a window
    /// that was not there before and brings it to the front. False when none showed up in time.
    /// </summary>
    public static async Task<bool> ActivateNewWindowAsync(HashSet<IntPtr> before, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300);
            var fresh = TopLevelWindows().Where(w => !before.Contains(w)).ToList();
            if (fresh.Count == 0) continue;
            var best = fresh[0];
            long bestArea = 0;
            foreach (var w in fresh)
            {
                if (!NativeMethods.GetWindowRect(w, out var r)) continue;
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = w;
                }
            }
            NativeMethods.ShowWindow(best, NativeMethods.SwRestore);
            NativeMethods.SetForegroundWindow(best);
            return true;
        }
        return false;
    }

    /// <summary>The program registered for a URL scheme (https gives msedge.exe, chrome.exe …); null when unknown.</summary>
    public static string? HandlerExe(string? scheme)
    {
        if (string.IsNullOrEmpty(scheme)) return null;
        uint len = 0;
        // First call (null buffer) reports the length; S_FALSE is the normal answer.
        NativeMethods.AssocQueryString(NativeMethods.AssocfIsProtocol, NativeMethods.AssocstrExecutable, scheme, null, null, ref len);
        if (len == 0 || len > 4096) return null;
        var sb = new System.Text.StringBuilder((int)len);
        return NativeMethods.AssocQueryString(NativeMethods.AssocfIsProtocol, NativeMethods.AssocstrExecutable, scheme, null, sb, ref len) == 0
            ? sb.ToString()
            : null;
    }

    /// <summary>
    /// Titled, unowned top-level windows of other processes in Z-order (topmost first), minimized ones included.
    /// Only windows of the handler program are looked at closely; for the rest the exe is not resolved.
    /// </summary>
    public static List<WindowInfo> WindowSnapshot()
    {
        var list = new List<WindowInfo>();
        var self = Environment.ProcessId;
        var buffer = new char[1024];
        var paths = new Dictionary<int, string?>();
        var z = 0;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            z++;
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == self) return true;
            const int GwlExStyle = -20, WsExToolWindow = 0x80;
            if ((NativeMethods.GetWindowLong(hWnd, GwlExStyle) & WsExToolWindow) != 0) return true;
            if (NativeMethods.GetWindow(hWnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            var len = NativeMethods.GetWindowTextLength(hWnd);
            if (len == 0) return true;
            var minimized = NativeMethods.IsIconic(hWnd);
            long area = 0;
            if (NativeMethods.GetWindowRect(hWnd, out var r)) area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (!minimized && (r.Right - r.Left < 100 || r.Bottom - r.Top < 60)) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            if (!paths.TryGetValue(pid, out var path)) paths[pid] = path = ImagePath(pid, buffer);
            list.Add(new WindowInfo(hWnd.ToInt64(), pid, LaunchWindowPicker.ExeNameOf(path) ?? "", sb.ToString(), minimized, z, area));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Restores, shows and takes a foreign window to the front (thread input attach, topmost flip, Alt tap fallback).</summary>
    public static bool ForceForeground(IntPtr hwnd, bool maximize = false)
    {
        // Only a minimized window needs restoring; SW_RESTORE on a maximized one would shrink it again.
        if (maximize) NativeMethods.ShowWindow(hwnd, NativeMethods.SwShowMaximized);
        else if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
        var thisThread = NativeMethods.GetCurrentThreadId();
        var foreground = NativeMethods.GetForegroundWindow();
        var foreThread = foreground == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var attached = foreThread != 0 && foreThread != thisThread && !NativeMethods.IsHungAppWindow(foreground)
            && NativeMethods.AttachThreadInput(thisThread, foreThread, true);
        try
        {
            const uint flags = NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpShowWindow | NativeMethods.SwpNoActivate;
            // The flip lifts the window above the others; a window that is topmost by itself (some full-screen games) is above them
            // already and must stay topmost, the flip would take that away.
            const int GwlExStyle = -20, WsExTopmost = 0x8;
            if ((NativeMethods.GetWindowLong(hwnd, GwlExStyle) & WsExTopmost) == 0)
            {
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTopmost, 0, 0, 0, 0, flags);
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndNoTopmost, 0, 0, 0, 0, flags);
            }
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(thisThread, foreThread, false);
        }
        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            // CouchHUD's own key: never counted as someone at the PC (the couch browser's unlock, Bluetooth).
            KeyboardInput.MarkInjected();
            NativeMethods.keybd_event(NativeMethods.VkMenu, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VkMenu, 0, NativeMethods.KeyEventFKeyUp, UIntPtr.Zero);
            KeyboardInput.MarkInjected();
            NativeMethods.SetForegroundWindow(hwnd);
        }
        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    /// <summary>
    /// After a URL/protocol start the owning window may belong to an already running process (Edge with a minimized window):
    /// waits for a new window, else reuses a window of the scheme's handler program. Returns what was found.
    /// </summary>
    public static async Task<WindowMatchKind> ActivateLaunchWindowAsync(IReadOnlyList<WindowInfo> before, string? handlerExe, TimeSpan timeout, string? scheme = null, bool maximizeWebWindows = false)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            await Task.Delay(300);
            // Reusing a window that did not change is a guess: only after the handler had time to react.
            var allowUnchanged = DateTime.UtcNow - start > TimeSpan.FromSeconds(4);
            var match = LaunchWindowPicker.Choose(before, WindowSnapshot(), handlerExe, allowUnchanged);
            if (match.Window is null) continue;
            var plan = LaunchWindowPicker.PlanActivation(match.Window, scheme, maximizeWebWindows);
            // Right before the activation: CouchHUD still has the foreground right and hands it to the browser's process.
            NativeMethods.AllowSetForegroundWindow(plan.AllowPid);
            ForceForeground(new IntPtr(match.Window.Handle), plan.Maximize);
            return match.Kind;
        }
        return WindowMatchKind.None;
    }

    /// <summary>
    /// Asks the game to close, then ends it after a grace period. Only the processes that are still the ones the tracker saw
    /// (same start time and image) are touched, never a stranger that got an old process id (see <see cref="SafeTermination"/>).
    /// Game clients (Steam, Epic …) are spared even when the game started them, unless the client is the session's own entry
    /// (<see cref="SafeTermination.SpareClients"/>).
    /// </summary>
    /// <param name="force">End right away, for a game that hangs.</param>
    public static async Task<SafeTermination.Outcome> QuitAsync(IReadOnlyDictionary<int, ProcessStamp> stamps, bool force = false, TimeSpan? grace = null, CancellationToken cancel = default, Func<bool>? mayEnd = null)
    {
        var outcome = await SafeTermination.QuitAsync(Win32ProcessControl.Instance, stamps, force, force ? TimeSpan.Zero : grace ?? TimeSpan.FromSeconds(4), cancel,
            spare: SafeTermination.SpareClients(stamps), mayEnd: mayEnd).ConfigureAwait(false);
        if (outcome.Found == 0) throw new InvalidOperationException(Strings.Get("hud.cannotCloseGame"));
        return outcome;
    }
}
