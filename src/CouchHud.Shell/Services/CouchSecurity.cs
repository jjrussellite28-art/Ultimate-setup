using System.Diagnostics;
using System.Runtime.InteropServices;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Security;
using CouchHud.Shell.Native;
using Microsoft.Win32;
using CouchHud.Core.Platform;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

// Couch lock: with automatic sign-in, CouchHud is the only thing between the couch and
// the Windows account. Everything here makes sure that leaving CouchHud, on purpose or
// not, ends at the Windows lock screen or behind the passcode.

/// <summary>The real Windows lock screen: Windows Hello, PIN or password.</summary>
public static class WindowsLock
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    /// <summary>Locks Windows; false when that failed (nothing is waiting for an unlock then).</summary>
    public static bool Lock()
    {
        if (LockWorkStation()) return true;
        Console.Error.WriteLine($"[lock] LockWorkStation failed: {Marshal.GetLastWin32Error()}");
        return false;
    }

    /// <summary>
    /// Locks Windows, trying again a few times: right after the sign-in or while the screen changes, LockWorkStation can fail
    /// once and work a second later. False only when every try failed.
    /// </summary>
    public static bool LockWithRetry(int attempts = 4, int pauseMs = 1500) =>
        RecoveryRules.Repeat(Lock, attempts, () => Thread.Sleep(pauseMs));

    public static bool Configured(string dataDir)
    {
        try
        {
            return new ConfigStore(dataDir).Current.CouchLock != "off";
        }
        catch (Exception ex)
        {
            // Unreadable config: better locked than open.
            Console.Error.WriteLine($"[lock] config: {ex.Message}");
            return true;
        }
    }

    /// <summary>Locks if the user set up the couch lock, read straight from the config file.</summary>
    public static void LockIfConfigured(string dataDir)
    {
        if (Configured(dataDir)) Lock();
    }
}

/// <summary>
/// Whether the Windows account has a password. Without one, "Windows sign-in" as the parents' code is just a
/// click for everyone, so the parental controls and the lock mode "Windows sign-in" refuse it, and automatic sign-in
/// is not needed. The answer is cached; it comes from the account flags (no sign-in try), and only an account that
/// may have an empty password gets one LogonUser try with an empty password (a failed sign-in for an account that does
/// have a password, so at most every <see cref="AccountPasswordRules.ProbeInterval"/>). Ask on a background thread.
/// </summary>
public static class AccountPassword
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buffer);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    // USER_INFO_1
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1
    {
        public IntPtr Name;
        public IntPtr Password;
        public uint PasswordAge;
        public uint Priv;
        public IntPtr HomeDir;
        public IntPtr Comment;
        public uint Flags;
        public IntPtr ScriptPath;
    }

    private static readonly object Gate = new();
    private static bool? _set;
    private static DateTime _probed = DateTime.MinValue;

    /// <summary>The cached answer, null while nobody has asked yet.</summary>
    public static bool? Known
    {
        get { lock (Gate) return _set; }
    }

    /// <summary>The cached answer; asks (once) if there is none yet.</summary>
    public static bool IsSet() => Known ?? Refresh();

    /// <summary>Asks again ("check again"); a sign-in try is never repeated within the probe interval.</summary>
    public static bool Refresh()
    {
        lock (Gate)
        {
            if (FlagsSay() is { } certain)
            {
                _set = certain;
                return certain;
            }
            if (_set is { } cached && DateTime.UtcNow - _probed < AccountPasswordRules.ProbeInterval) return cached;
            _set = Probe();
            _probed = DateTime.UtcNow;
            return _set.Value;
        }
    }

    /// <summary>The account may have got a password since: ask again at the next chance.</summary>
    public static void Recheck()
    {
        lock (Gate) _probed = DateTime.MinValue;
    }

    private static bool? FlagsSay()
    {
        try
        {
            if (NetUserGetInfo(null, Environment.UserName, 1, out var buffer) != 0) return null;
            try
            {
                return AccountPasswordRules.FromFlags(Marshal.PtrToStructure<UserInfo1>(buffer).Flags);
            }
            finally
            {
                NetApiBufferFree(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or AccessViolationException)
        {
            return null;
        }
    }

    private static bool Probe()
    {
        try
        {
            var ok = LogonUser(Environment.UserName, ".", "", 3, 0, out var token);
            var error = Marshal.GetLastWin32Error();
            if (ok) CloseHandle(token);
            return AccountPasswordRules.FromBlankLogon(ok, error);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
    }
}

/// <summary>
/// Task Manager off while the couch is locked: it could start explorer or end CouchHud.
/// A per-user policy that is removed again in PC mode, on quit and by the recovery.
/// </summary>
public static class TaskManagerPolicy
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";

    public static void Apply(bool block)
    {
        try
        {
            if (block)
            {
                using var key = Registry.CurrentUser.CreateSubKey(Key);
                key.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
                key?.DeleteValue("DisableTaskMgr", throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The Policies key only lets administrators write: the setup's scheduled task does it, if there is one.
            PolicyHelper.Want(PolicyGroups.TaskManager, block);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[lock] task manager policy: {ex.Message}");
        }
    }
}

/// <summary>
/// A second CouchHud process that watches the first one. If CouchHud ends while the
/// couch is locked (killed, crashed), the guard locks Windows and starts CouchHud again.
/// The other way round, CouchHud locks Windows if the guard disappears.
/// </summary>
public sealed class Guard
{
    public const string Switch = "--guard";
    private const int SmShuttingDown = 0x2000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private Process? _process;
    private bool _expected;

    public bool Running => _process is { HasExited: false };

    public void Start()
    {
        if (Running) return;
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        _expected = true;
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, $"{Switch} {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true });
            if (p is null) return;
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!_expected || _process != p) return;
                // A setup that CouchHUD started force-closes the guard together with CouchHUD: no lock for that.
                if (SelfUpdateManager.UpdateInProgress(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchHud"))) return;
                Console.Error.WriteLine("[lock] guard ended unexpectedly, locking");
                WindowsLock.Lock();
                _process = null;
                Start();
            });
            _process = p;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"[lock] guard not started: {ex.Message}");
        }
    }

    /// <summary>Unlocked on purpose (PC mode, quit): the guard goes away quietly.</summary>
    public void Stop()
    {
        _expected = false;
        var p = _process;
        _process = null;
        if (p is null) return;
        try
        {
            if (!p.HasExited) p.Kill();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
        p.Dispose();
    }

    /// <summary>The guard process itself: wait for CouchHud, then lock and bring it back.</summary>
    public static int Run(string pidArg)
    {
        if (!int.TryParse(pidArg, out var pid)) return 2;
        try
        {
            using var hud = Process.GetProcessById(pid);
            hud.WaitForExit();
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        // Whatever CouchHud froze must not stay frozen.
        GamePause.ResumeLeftovers(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchHud", "paused.txt"));
        // Windows is shutting down or signing out: nothing to guard, nothing to restart. CouchHud ends
        // first and the flag follows a moment later, so look for a few seconds before deciding.
        for (var i = 0; i < 25; i++)
        {
            if (GetSystemMetrics(SmShuttingDown) != 0) return 0;
            Thread.Sleep(200);
        }
        // The setup closed CouchHud for an update: marker in the protected program folder (with the setup's pid;
        // not in %APPDATA%, which would be another user's when the setup runs in another session).
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchHud");
        // Same for a setup CouchHUD started itself (self-update); that one restarts CouchHUD when it is done.
        if (SelfUpdateManager.UpdateInProgress(dataDir)) return 0;
        Console.Error.WriteLine($"[guard] CouchHud (pid {pid}) ended unexpectedly");
        // Restarting again and again is the problem, not the cure: the recovery takes over.
        if (!new GuardRestarts(dataDir).TryRegister())
        {
            if (Recovery.Run(dataDir, Strings.Get("recovery.guardRestarts"))) return 0;
            // Windows would not lock, so the recovery changed nothing: count the restarts again and start CouchHud as usual
            // (it is locked by its code), not a black screen.
            Recovery.RestoreCounters(dataDir, crashLoop: false, guardRestarts: true);
        }
        WindowsLock.LockWithRetry();
        // As the shell there is nothing else on screen after the unlock: start CouchHud again.
        if (!ShellManager.IsDesktopShellRunning() && Environment.ProcessPath is { } exe)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Console.Error.WriteLine($"[guard] restart failed: {ex.Message}");
            }
        }
        return 0;
    }
}

/// <summary>
/// The way out when something is broken: hold Ctrl + Shift (or LB + RB + View on the controller)
/// for a moment while CouchHud starts, or let it end unclean three times in a row.
/// Shift alone does not count: capital letters in the Windows sign-in would trigger it.
/// Two steps (see <see cref="RecoveryRules"/>): with the couch lock on, Windows is locked first and nothing else happens
/// until the account has signed in; a restart at the lock screen leads back into the couch. Only after the sign-in the
/// shell entry and the automatic sign-in go and the normal desktop comes back.
/// </summary>
public static class Recovery
{
    private const ushort PadLb = 0x0100, PadRb = 0x0200, PadBack = 0x0020;
    private const int VkShift = 0x10, VkControl = 0x11;
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(1500);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    /// <summary>The combination held right now, or null.</summary>
    private static string? Pressed()
    {
        if ((GetAsyncKeyState(VkControl) & 0x8000) != 0 && (GetAsyncKeyState(VkShift) & 0x8000) != 0) return "Strg+Umschalt";
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                if (NativeMethods.XInputGetState(i, out var state) != 0) continue;
                var b = state.Gamepad.Buttons;
                if ((b & PadLb) != 0 && (b & PadRb) != 0 && (b & PadBack) != 0) return $"Controller {i + 1}: LB+RB+View";
            }
        }
        catch (DllNotFoundException)
        {
        }
        return null;
    }

    /// <summary>
    /// The combination, if it stays held for about 1.5 s (checked every 100 ms); null otherwise.
    /// Only waits at all when something is pressed at the start.
    /// </summary>
    public static string? KeyHeld()
    {
        var trigger = Pressed();
        if (trigger is null) return null;
        Console.WriteLine($"[recovery] {trigger} pressed at start, waiting to see if it is held");
        for (var waited = TimeSpan.Zero; waited < HoldTime; waited += TimeSpan.FromMilliseconds(100))
        {
            Thread.Sleep(100);
            if (Pressed() != trigger)
            {
                Console.WriteLine("[recovery] released too early, no recovery");
                return null;
            }
        }
        return trigger;
    }

    /// <summary>
    /// Watches the first seconds after start in the background: a controller is often not
    /// enumerated by XInput yet when CouchHud starts. When the combination is held, onHeld runs
    /// (on a pool thread) and the watching ends.
    /// </summary>
    public static void WatchStart(Action<string> onHeld)
    {
        Task.Run(() =>
        {
            try
            {
                var started = DateTime.UtcNow;
                DateTime? since = null;
                string? current = null;
                while (DateTime.UtcNow - started < TimeSpan.FromSeconds(4) || since is not null)
                {
                    var now = Pressed();
                    if (now is null || now != current) since = now is null ? null : DateTime.UtcNow;
                    current = now;
                    if (since is { } s && DateTime.UtcNow - s >= HoldTime)
                    {
                        Console.WriteLine($"[recovery] {current} held shortly after start");
                        onHeld(current!);
                        return;
                    }
                    Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[recovery] watcher: {ex}");
            }
        });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    /// <summary>
    /// Tells the user that the recovery did not happen. On its own thread (nothing waits for the click) and topmost, so it also
    /// shows over the full-screen HUD.
    /// </summary>
    private static void NotifyLockFailed()
    {
        var text = Strings.Get("recovery.lockFailed");
        Task.Run(() =>
        {
            try
            {
                // OK + warning icon + set foreground + topmost.
                ShowInFront(text, 0x0 | 0x30 | 0x10000 | 0x40000);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Console.Error.WriteLine($"[recovery] message: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// The recovery could not run (Windows would not lock): the unclean ends and guard restarts it consumed count again, so the
    /// next start is a crash loop again and tries once more.
    /// </summary>
    public static void RestoreCounters(string dataDir, bool crashLoop, bool guardRestarts)
    {
        try
        {
            if (crashLoop) new CrashTracker(dataDir).Rearm();
            if (guardRestarts) new GuardRestarts(dataDir).Rearm();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[recovery] {ex.Message}");
        }
    }

    /// <summary>Counts the previous run if it ended unclean and marks this one as running; true on a crash loop.</summary>
    public static bool CrashLoop(string dataDir)
    {
        try
        {
            var tracker = new CrashTracker(dataDir);
            var loop = tracker.RegisterStart();
            Console.WriteLine($"[recovery] unclean starts in a row: {tracker.Consecutive()}, crash loop: {loop}");
            return loop;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[recovery] {ex.Message}");
            return false;
        }
    }

    /// <summary>Running fine for a while: earlier unclean starts and guard restarts no longer count.</summary>
    public static void MarkHealthy(string dataDir)
    {
        try
        {
            new CrashTracker(dataDir).MarkHealthy();
            new GuardRestarts(dataDir).Reset();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[recovery] {ex.Message}");
        }
    }

    /// <summary>Ending on purpose (quit, sign-out, shutdown, restart): this run is no crash.</summary>
    public static void MarkClean(string dataDir)
    {
        try
        {
            new CrashTracker(dataDir).MarkClean();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[recovery] {ex.Message}");
        }
    }

    /// <summary>
    /// Automatic sign-in that CouchHud set up is switched off by the recovery (through the service, the reserve task as fallback, see
    /// <see cref="AutoLogon.TurnOffWithoutPrompt"/>); the account and the lock stay in the config, the console setup brings it back. Returns the
    /// sentence for the user, or null when there was nothing to do (not on, or someone else's).
    /// </summary>
    private static string? SwitchOffAutoLogon(string dataDir)
    {
        try
        {
            var flag = new ConfigStore(dataDir).Current.AutoLogonConfigured;
            if (!AutoLogonRecovery.ShouldSwitchOff(AutoLogon.Evaluate(flag).State)) return null;
            if (AutoLogon.TurnOffWithoutPrompt())
            {
                Console.WriteLine("[recovery] automatic sign-in switched off");
                return Strings.Get("recovery.autoLogonOff");
            }
            Console.Error.WriteLine("[recovery] automatic sign-in could not be switched off (task missing or refused)");
            return Strings.Get("recovery.autoLogonStays");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[recovery] automatic sign-in: {ex.Message}");
            return Strings.Get("recovery.autoLogonStays");
        }
    }

    /// <summary>
    /// First step: lock Windows and wait for the sign-in of the account (the owner knows the password, a guest at the
    /// controller does not). True once the account is back; false when Windows could not be locked, in which case
    /// nothing is touched (the couch stays as it is).
    /// </summary>
    private static bool AwaitSignIn(string dataDir)
    {
        // Not SystemEvents.SessionSwitch: it posts to the synchronization context of the subscribing thread, and a blocked UI thread
        // never gets the unlock. The source has a message window and loop of its own and the poll of the lock state as the net below.
        using var session = new WtsSessionSource();
        if (!WindowsLock.LockWithRetry()) return false;
        Console.WriteLine($"[recovery] Windows locked, waiting for the sign-in before the shell is touched (notifications {(session.Listening ? "on" : "off, polling only")})");
        var result = SignInWait.Wait(session, () => DateTime.UtcNow, Thread.Sleep, SignInWait.DefaultPoll, SignInWait.DefaultLockGrace,
            log: message => Console.Error.WriteLine($"[recovery] {message}"));
        if (result != SignInWaitResult.SignedIn)
        {
            Console.Error.WriteLine($"[recovery] no sign-in seen: {result}");
            return false;
        }
        Console.WriteLine("[recovery] signed in");
        return true;
    }

    /// <summary>
    /// Blocks until the account has signed in again (with the couch lock on). Call off the UI thread where one is running (the wait itself
    /// does not depend on it: see <see cref="WtsSessionSource"/>).
    /// False when Windows could not be locked: nothing was touched and the caller carries on as if there was no recovery.
    /// </summary>
    public static bool Run(string dataDir, string reason)
    {
        Console.Error.WriteLine($"[recovery] {reason}");
        // Step one: anyone can trigger the recovery, so it opens nothing before Windows has asked for the account.
        // The shell entry, the automatic sign-in and the counters stay as they are: a restart at the lock screen is the couch again.
        string couchLock;
        try
        {
            couchLock = new ConfigStore(dataDir).Current.CouchLock;
        }
        catch (Exception ex)
        {
            // Unreadable config: better locked than open.
            Console.Error.WriteLine($"[recovery] config: {ex.Message}");
            couchLock = "code";
        }
        if (RecoveryRules.MustAuthenticateFirst(couchLock) && !AwaitSignIn(dataDir))
        {
            // Windows would not lock, not even after several tries. Without the sign-in the recovery opens nothing (a child at the
            // controller could otherwise reach the desktop without the password): the couch stays as it is, the counters are
            // given back by the caller (see RestoreCounters) so the next start tries again, and the user is told.
            Console.Error.WriteLine("[recovery] Windows could not be locked, nothing was changed");
            NotifyLockFailed();
            return false;
        }

        // Step two: the account is there.
        // The PC would sign in by itself onto the normal desktop: without the couch shell there is no lock behind that sign-in.
        var autoLogonNote = SwitchOffAutoLogon(dataDir);
        if (autoLogonNote is not null) reason = $"{reason}. {autoLogonNote}";
        // The user hears why the next time CouchHud runs; the shell entry is gone then.
        try
        {
            new ConfigStore(dataDir).Update(c =>
            {
                c.ShellRemovedReason = reason;
                c.PendingShellRestore = false;
                // The sign-in is off now: setup and settings show it as open again (integration mode and lock stay for the one-button setup).
                if (!AutoLogon.Status().Enabled) c.AutoLogonConfigured = false;
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[recovery] config: {ex.Message}");
        }
        // A deliberate reset: nothing counts any more.
        MarkClean(dataDir);
        MarkHealthy(dataDir);
        TaskManagerPolicy.Apply(false);
        LockedDesktop.Release(dataDir, wait: true);
        try
        {
            var shell = new ShellManager(new WindowsRegistry());
            shell.UninstallAsShell();
            shell.ClearRestoreFallback();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[recovery] shell: {ex}");
        }
        try
        {
            // CouchHud was the shell: without a taskbar there is no explorer in this session, and removing the Shell value does not
            // start one. The desktop only comes back when we start it (the first explorer.exe of a session becomes the shell).
            if (!ShellManager.IsDesktopShellRunning())
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();
                Console.WriteLine(ShellManager.WaitForDesktopShell(TimeSpan.FromSeconds(15))
                    ? "[recovery] explorer started, the desktop is there"
                    : "[recovery] explorer started, the taskbar did not show up in time");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[recovery] explorer: {ex}");
        }
        // A sign-in by itself (an automatic sign-in that stays) lands on the desktop: lock again right at the sign-in.
        SignInLock.Sync(dataDir);
        new ShellManager(new WindowsRegistry()).LogDiagnostics("after recovery");
        // The note comes up under the starting explorer otherwise (its shell start brings windows of other programs to the front):
        // wait for the taskbar and a little longer, then show it.
        try
        {
            ShellManager.WaitForDesktopShell(TimeSpan.FromSeconds(20));
            Thread.Sleep(2000);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[recovery] wait for shell: {ex.Message}");
        }
        ShowInFront(Strings.Get("recovery.message", reason));
        return true;
    }

    // MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST | MB_SYSTEMMODAL
    private const uint InFrontFlags = 0x0 | 0x40 | 0x10000 | 0x40000 | 0x1000;

    /// <summary>
    /// The note about the recovery must be seen: the window that was in front (a program of the couch session, CaseHUD) would cover an
    /// ordinary box, and with a taskbar that is hidden or not running nobody finds it. MB_TOPMOST alone did not hold against a window
    /// of another process, so the box is also pushed to HWND_TOPMOST and the foreground by a helper while it is open, and flashed
    /// as a fallback when Windows refuses the foreground.
    /// </summary>
    private static void ShowInFront(string text, uint flags = InFrontFlags)
    {
        try
        {
            // Anything that wants the foreground is allowed to take it (this box included): the recovery may start while another program owns it.
            NativeMethods.AllowSetForegroundWindow(-1);
            using var done = new CancellationTokenSource();
            var pusher = Task.Run(() => KeepBoxInFront("CouchHUD", done.Token));
            MessageBoxW(IntPtr.Zero, text, "CouchHUD", flags);
            done.Cancel();
            pusher.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or AggregateException)
        {
            Console.Error.WriteLine($"[recovery] message: {ex.Message}");
        }
    }

    /// <summary>Finds our message box (class #32770, our process) and keeps it topmost and in front for a few seconds.</summary>
    private static void KeepBoxInFront(string caption, CancellationToken stop)
    {
        const uint swpNoSize = 0x1, swpNoMove = 0x2, swpShowWindow = 0x40;
        var topmost = new IntPtr(-1);
        var own = Environment.ProcessId;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        var flashed = false;
        while (!stop.IsCancellationRequested && DateTime.UtcNow < until)
        {
            try
            {
                var box = NativeMethods.FindWindow("#32770", caption);
                if (box != IntPtr.Zero)
                {
                    NativeMethods.GetWindowThreadProcessId(box, out var pid);
                    if (pid == own)
                    {
                        // WS_EX_TOPMOST (0x8) does not always stay (the shell start resets it): check and set it again.
                        const int exStyle = -20;
                        if ((NativeMethods.GetWindowLong(box, exStyle) & 0x8) == 0) Console.Error.WriteLine("[recovery] note lost WS_EX_TOPMOST, setting it again");
                        NativeMethods.SetWindowPos(box, topmost, 0, 0, 0, 0, swpNoMove | swpNoSize | swpShowWindow);
                        // Attached to the foreground thread's input, SetForegroundWindow is not refused.
                        var front = NativeMethods.GetForegroundWindow();
                        var frontThread = front == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(front, out _);
                        var me = NativeMethods.GetCurrentThreadId();
                        var attached = frontThread != 0 && frontThread != me && NativeMethods.AttachThreadInput(me, frontThread, true);
                        var inFront = NativeMethods.SetForegroundWindow(box);
                        if (attached) NativeMethods.AttachThreadInput(me, frontThread, false);
                        if (!flashed && (!inFront || !(NativeMethods.GetForegroundWindow() == box)))
                        {
                            flashed = true;
                            // FLASHW_ALL | FLASHW_TIMERNOFG
                            var info = new NativeMethods.FlashInfo { Size = (uint)Marshal.SizeOf<NativeMethods.FlashInfo>(), Hwnd = box, Flags = 0x3 | 0xC, Count = 0, Timeout = 0 };
                            NativeMethods.FlashWindowEx(ref info);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return;
            }
            stop.WaitHandle.WaitOne(400);
        }
    }
}

/// <summary>
/// While the couch lock and an automatic sign-in are on and CouchHud is not the shell (recovery, PC mode, a light
/// setup), nothing locks the couch behind that sign-in. A Run entry of the account closes the gap: at every sign-in it locks
/// Windows at once. As the shell, CouchHud locks by itself and Run entries do not run at all.
/// </summary>
public static class SignInLock
{
    public const string Switch = "--lock-on-sign-in";
    public const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "CouchHudLockOnSignIn";

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    /// <summary>Is the automatic sign-in on for the account that runs this process (and not for another one)?</summary>
    private static bool AutoLogonForMe(string dataDir)
    {
        if (!AutoLogon.Status().Enabled) return false;
        bool flag;
        try
        {
            flag = new ConfigStore(dataDir).Current.AutoLogonConfigured;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            flag = false;
        }
        return !AutoLogon.Evaluate(flag).OtherUser;
    }

    /// <summary>Puts the Run entry in place or removes it, as <see cref="RecoveryRules.WantsSignInLock"/> says. Call after every change of shell, automatic sign-in or couch lock.</summary>
    public static void Sync(string dataDir)
    {
        try
        {
            var registry = new WindowsRegistry();
            var couchLock = new ConfigStore(dataDir).Current.CouchLock;
            var wanted = RecoveryRules.WantsSignInLock(couchLock, AutoLogonForMe(dataDir), new ShellManager(registry).IsInstalledAsShell());
            if (wanted && Environment.ProcessPath is { } exe) registry.WriteString(RunKey, RunValue, $"\"{exe}\" {Switch}");
            else registry.DeleteValue(RunKey, RunValue);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[lock] sign-in lock: {ex.Message}");
        }
    }

    /// <summary>The same off the calling thread (the registry and the schtasks-free checks are quick, but the callers are UI handlers).</summary>
    public static void SyncInBackground(string dataDir) => Task.Run(() => Sync(dataDir));

    /// <summary>Uninstall: the Run entry of this account goes.</summary>
    public static void RemoveEntry() => new WindowsRegistry().DeleteValue(RunKey, RunValue);

    /// <summary>
    /// Did the automatic sign-in sign this session in, and not a person? A new session starts when Windows boots or after a
    /// sign-out; an automatic sign-in follows the start of that session within seconds, while a person first has to type or
    /// look at the camera. Windows keeps both times of the session (connect and logon), and
    /// <see cref="RecoveryRules.LooksLikeAutoSignIn"/> compares them. Unknown times count as automatic: one extra lock is
    /// harmless, a missed one opens the PC.
    /// </summary>
    private static bool SessionStartedByAutoLogon()
    {
        // WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION, WTSSessionInfo.
        if (!WTSQuerySessionInformationW(IntPtr.Zero, 0xFFFFFFFF, 24, out var buffer, out var bytes) || bytes < 216) return true;
        try
        {
            // WTSINFOW: ConnectTime at offset 176 and LogonTime at offset 200, FILETIMEs.
            var connect = Marshal.ReadInt64(buffer, 176);
            var logon = Marshal.ReadInt64(buffer, 200);
            if (connect <= 0 || logon <= 0) return true;
            return RecoveryRules.LooksLikeAutoSignIn(DateTime.FromFileTimeUtc(connect), DateTime.FromFileTimeUtc(logon));
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or ArgumentException)
        {
            return true;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// The Run entry at sign-in: lock, unless CouchHud is the shell (then it locks itself) or a person signed in by hand. A
    /// CouchHud that happens to run is no reason to skip it: locking twice does no harm.
    /// </summary>
    public static int Run(string dataDir)
    {
        try
        {
            if (new ShellManager(new WindowsRegistry()).IsInstalledAsShell()) return 0;
            // The automatic sign-in was switched off or moved to another account since: nothing signs in by itself any more.
            if (!AutoLogonForMe(dataDir)) return 0;
            if (!SessionStartedByAutoLogon())
            {
                Console.WriteLine("[lock] signed in by hand, no lock at sign-in");
                return 0;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[lock] sign-in lock: {ex.Message}");
        }
        // Unreadable config counts as locked (see WindowsLock.Configured).
        if (WindowsLock.Configured(dataDir)) WindowsLock.LockWithRetry(attempts: 5, pauseMs: 1000);
        return 0;
    }
}
