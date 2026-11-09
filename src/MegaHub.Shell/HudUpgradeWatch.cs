using System.Diagnostics;
using MegaHub.Core.Guard;
using MegaHub.Core.Localization;
using MegaHub.Core.Notifications;
using MegaHub.Core.Updates;
using MegaHub.Shell.Native;
using MegaHub.Shell.Services;

namespace MegaHub.Shell;

/// <summary>
/// An app upgrade that needs an answer outside the HUD: Windows' elevation prompt or an installer's window would sit behind the
/// full-screen HUD (the prompt only blinks on a taskbar the couch does not have). While winget runs, its process tree and
/// consent.exe of this session are looked at twice a second; <see cref="UpgradeYield"/> decides when the HUD steps aside and when
/// it comes back. Only windows of winget's own children and the prompt are brought forward, nothing else; the couch lock and the
/// foreground guard stay as they are (the prompt asks Windows' own question, an installer window is judged by the guard like any
/// other). The prompt runs on the secure desktop, which no program and no controller can reach: it is answered with mouse or
/// keyboard, the HUD says so once.
/// </summary>
public sealed partial class Hud
{
    // The controller mouse mode was started by the upgrade watch (UI thread only).
    private bool _upgradeMouse;

    private static readonly TimeSpan UpgradeLook = TimeSpan.FromMilliseconds(500);

    /// <summary>Starts the watch for the winget process <paramref name="wingetPid"/>; disposing it ends the watch (and brings the HUD back if it stepped aside).</summary>
    private IDisposable WatchAppUpgrade(int wingetPid)
    {
        // Mega Hub is in front (the button was just pressed): winget may take the foreground, so its elevation request counts as one
        // from the front and Windows shows the prompt right away instead of a blinking taskbar button.
        NativeMethods.AllowSetForegroundWindow(wingetPid);
        var yield = new UpgradeYield();
        var stop = new CancellationTokenSource();
        var wingetStart = Win32ProcessControl.StartOf(wingetPid);
        var shown = new UpgradeWatchState();
        var hud = _window is { } own ? new System.Windows.Interop.WindowInteropHelper(own).Handle.ToInt64() : 0;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(UpgradeLook, stop.Token);
                    var look = LookForUpgradeWindows(wingetPid, wingetStart, hud);
                    // Decided on the UI thread (where the HUD's state lives); the yield object is only ever touched there.
                    Ui(() =>
                    {
                        var scene = new UpgradeScene(look.Prompt.Handle, look.PromptRunning, look.Tree.Select(w => w.Handle).ToList(),
                            HudOwnsScreen(), look.Foreground, look.ForegroundKind);
                        var step = yield.Next(scene);
                        if (step.Action == UpgradeYieldAction.None) return;
                        // Yield and Restore look at the front once more at the moment they happen.
                        if (step.Action is UpgradeYieldAction.Yield or UpgradeYieldAction.Restore)
                        {
                            var (nowWindow, nowKind) = ForegroundOf(hud, null);
                            step = yield.Recheck(step, nowWindow, nowKind);
                        }
                        var pid = step.Target == look.Prompt.Handle ? look.Prompt.Pid : look.Tree.FirstOrDefault(w => w.Handle == step.Target).Pid;
                        ApplyUpgradeYield(step, pid, shown);
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"[updates] upgrade watch: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                stop.Dispose();
            }
            var (fg, kind) = ForegroundOf(hud, null);
            Ui(() =>
            {
                var (nowWindow, nowKind) = ForegroundOf(hud, null);
                var end = yield.Recheck(yield.End(HudOwnsScreen(), fg, kind), nowWindow, nowKind);
                if (end.Action != UpgradeYieldAction.None) ApplyUpgradeYield(end, 0, shown);
            });
        });
        return new UpgradeWatchHandle(stop);
    }

    /// <summary>The HUD is what the couch sees: no game (also one that is starting or paused), no PC mode, no browser, no capture.</summary>
    private bool HudOwnsScreen()
    {
        var context = ForegroundNow();
        return !(context.GameRunning || context.PcMode || context.Browsing || context.Suspended);
    }

    /// <summary>What this watch did (UI thread only): the note was shown, the mouse mode was started by it.</summary>
    private sealed class UpgradeWatchState
    {
        public bool Told;
    }

    private sealed class UpgradeWatchHandle(CancellationTokenSource stop) : IDisposable
    {
        public void Dispose()
        {
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>On the UI thread: steps aside for the window, tells about it, or comes back.</summary>
    private void ApplyUpgradeYield(UpgradeYieldStep step, int pid, UpgradeWatchState shown)
    {
        try
        {
            switch (step.Action)
            {
                case UpgradeYieldAction.Note:
                    // A game, PC mode or the browser owns the screen: nothing is pushed over it, the note says that a question waits.
                    Tell(shown);
                    return;
                case UpgradeYieldAction.Yield:
                {
                    // UpgradeYield only steps aside while the HUD owns the screen; asked once more here, at the moment it happens.
                    if (!HudOwnsScreen())
                    {
                        Tell(shown);
                        return;
                    }
                    Console.WriteLine($"[updates] stepping aside for {(step.Prompt ? "the elevation prompt" : "an installer window")}");
                    var hwnd = new IntPtr(step.Target);
                    if (pid > 0) NativeMethods.AllowSetForegroundWindow(pid);
                    if (_window is { } w) TaskbarFullscreen.Mark(new System.Windows.Interop.WindowInteropHelper(w).Handle, false);
                    Win32ProcessProbe.ForceForeground(hwnd);
                    // An installer's window can be clicked with the controller as a mouse, but only on an open couch (a locked one has no
                    // mouse mode) and only when the installer runs without elevation (Windows keeps input of a normal process away from
                    // an elevated window; such a window needs mouse or keyboard).
                    if (!step.Prompt && !Locked && _mouse is { Active: false } mouse)
                    {
                        mouse.Start();
                        _upgradeMouse = true;
                    }
                    Tell(shown);
                    return;
                }
                case UpgradeYieldAction.Release:
                    // Someone else has the front now (an app the user started, a game): the HUD stays where it is, the mouse mode
                    // stays for whatever is in front (Guide ends it as always).
                    Console.WriteLine("[updates] nothing waits for an answer any more; another window is in front, the HUD stays behind it");
                    EndUpgradeMouse();
                    return;
                case UpgradeYieldAction.Restore:
                    // Never over a game, the browser or PC mode (UpgradeYield knows that already; asked again at the moment it happens).
                    if (!HudOwnsScreen())
                    {
                        EndUpgradeMouse();
                        return;
                    }
                    Console.WriteLine("[updates] nothing waits for an answer any more, the HUD comes back");
                    // Only the mouse mode this watch started.
                    EndUpgradeMouse();
                    _window?.BringToFront();
                    return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[updates] stepping aside failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Ends the controller mouse the upgrade watch started, and only that one (Stop, not EndMouseMode, which would show the HUD).
    /// Called when the watch lets go of the screen and when a game, PC mode or the browser takes it over.
    /// </summary>
    private void EndUpgradeMouse(bool stop = true)
    {
        if (!_upgradeMouse) return;
        _upgradeMouse = false;
        if (stop && _mouse?.Active == true) _mouse.Stop();
    }

    /// <summary>The note that a question waits outside the HUD, once per upgrade run.</summary>
    private void Tell(UpgradeWatchState shown)
    {
        if (shown.Told) return;
        shown.Told = true;
        PostNotification("Updates", Strings.Ref("update.elevation"), Strings.Ref("update.elevation.body"), target: NotificationTargets.Downloads);
    }

    private readonly record struct FoundWindow(long Handle, int Pid);

    private sealed record UpgradeWindows(FoundWindow Prompt, bool PromptRunning, List<FoundWindow> Tree, long Foreground, UpgradeForeground ForegroundKind);

    /// <summary>
    /// The window in front and whose it is: none, the HUD's, the upgrade's (<paramref name="ofUpgrade"/> says for a process id, null
    /// when that is not known) or someone else's.
    /// </summary>
    private static (long Window, UpgradeForeground Kind) ForegroundOf(long hud, Func<int, bool>? ofUpgrade)
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return (0, UpgradeForeground.None);
        if (fg.ToInt64() == hud) return (hud, UpgradeForeground.Hud);
        NativeMethods.GetWindowThreadProcessId(fg, out var pid);
        if (pid == Environment.ProcessId) return (fg.ToInt64(), UpgradeForeground.Hud);
        // The bare desktop (wallpaper, taskbar) is nobody's window: the HUD comes back onto it.
        if (IsDesktopShell(fg)) return (0, UpgradeForeground.None);
        return (fg.ToInt64(), ofUpgrade is not null && pid != 0 && ofUpgrade(pid) ? UpgradeForeground.Upgrade : UpgradeForeground.Other);
    }

    private static bool IsDesktopShell(IntPtr window)
    {
        var name = new System.Text.StringBuilder(64);
        if (NativeMethods.GetClassName(window, name, name.Capacity) == 0) return false;
        return name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>
    /// The elevation prompt of this session (its window, if it shows one) and the visible windows of processes winget started
    /// (checked by parent chain and start times, so a reused process id is never mistaken for a child), biggest first.
    /// </summary>
    private static UpgradeWindows LookForUpgradeWindows(int wingetPid, long wingetStart, long hud)
    {
        var session = Process.GetCurrentProcess().SessionId;
        var consent = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("consent"))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId == session) consent.Add(p.Id);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        var parents = OwnLaunches.ParentMap();
        var starts = new Dictionary<int, DateTime?>();
        DateTime? StartOf(int pid)
        {
            if (starts.TryGetValue(pid, out var known)) return known;
            var ticks = Win32ProcessControl.StartOf(pid);
            return starts[pid] = ticks > 0 ? DateTime.FromFileTimeUtc(ticks) : null;
        }
        bool IsWinget(int pid) => pid == wingetPid && wingetStart > 0 && Win32ProcessControl.StartOf(pid) == wingetStart;
        var self = Environment.ProcessId;
        var prompt = default(FoundWindow);
        var tree = new List<(FoundWindow Window, long Area)>();
        var verdicts = new Dictionary<int, bool>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self || pid == 0) return true;
            if (consent.Contains(pid))
            {
                // The prompt's window, also when it waits minimized (the blinking taskbar button).
                if (prompt.Handle == 0 && (NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))) prompt = new(hwnd.ToInt64(), pid);
                return true;
            }
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            const int GwlExStyle = -20, WsExToolWindow = 0x80;
            if ((NativeMethods.GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) != 0) return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var r) || r.Right - r.Left < 100 || r.Bottom - r.Top < 60) return true;
            if (OfUpgradeTree(pid)) tree.Add((new FoundWindow(hwnd.ToInt64(), pid), (long)(r.Right - r.Left) * (r.Bottom - r.Top)));
            return true;
        }, IntPtr.Zero);
        // The window in front: the prompt's, winget's own or one of its children's count as the upgrade's.
        var (foreground, kind) = ForegroundOf(hud, pid => consent.Contains(pid) || pid == wingetPid || OfUpgradeTree(pid));
        return new(prompt, consent.Count > 0, tree.OrderByDescending(t => t.Area).Select(t => t.Window).ToList(), foreground, kind);

        bool OfUpgradeTree(int pid)
        {
            if (!verdicts.TryGetValue(pid, out var ours))
            {
                ours = pid != wingetPid && ProcessTree.DescendsFrom(pid, IsWinget,
                    p => parents.TryGetValue(p, out var parent) ? parent : null, StartOf);
                verdicts[pid] = ours;
            }
            return ours;
        }
    }
}
