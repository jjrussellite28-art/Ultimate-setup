using System.Diagnostics;
using CouchHud.Core.Platform;
using CouchHud.Shell.Native;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

/// <summary>What the start-up comparison of shell entry and settings found.</summary>
public sealed record ShellReconcile(bool ForceFull, IReadOnlyList<string> Notes);

/// <summary>Shell registration, PC mode, autostart and power actions.</summary>
public sealed class ShellManager(IRegistry registry)
{
    private const string WinlogonKey = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string MachineWinlogonKey = @"HKLM\Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";

    /// <summary>Command line Winlogon should start as shell.</summary>
    public string SelfCommand { get; } = $"\"{Environment.ProcessPath}\"";

    /// <summary>The per-user shell value, null if there is none (Windows then uses explorer.exe).</summary>
    public string? ShellValue() => registry.ReadString(WinlogonKey, "Shell");

    /// <summary>Whether the per-user shell value starts exactly this exe, not just any path with "couchhud" in it.</summary>
    public bool IsInstalledAsShell() =>
        Environment.ProcessPath is { } exe && ShellRegistration.PointsTo(ShellValue(), exe);

    /// <summary>Per-user shell replacement: only this Windows account boots into CouchHud.</summary>
    public void InstallAsShell()
    {
        // A wrong value here means a black screen at the next login, so be strict.
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe) || !Path.GetFileName(exe).Equals(ShellRegistration.ExeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Strings.Get("shell.installedOnly"));
        }
        // Only the protected program folder: a copy in a user folder (or a build folder) can be swapped by any program of the user.
        if (!InstallFolders().Any(folder => ShellRegistration.IsUnder(exe, folder)))
        {
            throw new InvalidOperationException(Strings.Get("shell.programFilesOnly"));
        }
        registry.WriteString(WinlogonKey, "Shell", SelfCommand);
        var back = ShellValue();
        Console.WriteLine($"[shell] shell value written: {back}");
        if (!ShellRegistration.PointsTo(back, exe))
        {
            throw new InvalidOperationException(Strings.Get("shell.notApplied", back ?? Strings.Get("shell.nothing")));
        }
    }

    private static IEnumerable<string> InstallFolders() =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The RunOnce entry of a restart into PC mode is void once the shell entry was dealt with otherwise.</summary>
    public void ClearRestoreFallback() => registry.DeleteValue(RunOnceKey, "CouchHudRestoreShell");

    /// <summary>Without a per-user value Windows falls back to HKLM, i.e. explorer.exe.</summary>
    public void UninstallAsShell()
    {
        registry.DeleteValue(WinlogonKey, "Shell");
        Console.WriteLine($"[shell] shell value removed, now: {ShellValue() ?? "(none)"}");
    }

    /// <summary>Writes everything that decides which shell Windows starts to the log.</summary>
    public void LogDiagnostics(string context)
    {
        try
        {
            Console.WriteLine($"[shell] {context}: session {Process.GetCurrentProcess().SessionId}, installedAsShell {IsInstalledAsShell()}, desktopShellRunning {IsDesktopShellRunning()}");
            Console.WriteLine($"[shell] HKCU Winlogon Shell: {Show(ShellValue())}");
            Console.WriteLine($"[shell] HKLM Winlogon Shell: {Show(registry.ReadString(MachineWinlogonKey, "Shell"))}, Userinit: {Show(registry.ReadString(MachineWinlogonKey, "Userinit"))}");
            Console.WriteLine($"[shell] Policies\\System Shell: HKCU {Show(registry.ReadString(@"HKCU\" + PolicyKey, "Shell"))}, HKLM {Show(registry.ReadString(@"HKLM\" + PolicyKey, "Shell"))}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[shell] diagnostics failed: {ex.Message}");
        }

        static string Show(string? value) => value is null ? "(none)" : $"\"{value}\"";
    }

    /// <summary>
    /// Compares the shell entry with the settings at start. A shell value that points to a
    /// missing exe is removed: it would be a black screen at the next login.
    /// </summary>
    public ShellReconcile Reconcile(string integrationMode)
    {
        var notes = new List<string>();
        var forceFull = false;
        var value = ShellValue();
        var exe = ShellRegistration.ExePath(value is null ? null : Environment.ExpandEnvironmentVariables(value));
        if (exe is not null && (exe.Contains(':') || exe.StartsWith(@"\\", StringComparison.Ordinal)) && !File.Exists(exe))
        {
            registry.DeleteValue(WinlogonKey, "Shell");
            notes.Add(Strings.Get("shell.missingFile", exe));
        }
        else if (IsInstalledAsShell())
        {
            // Set up as the shell earlier: that was a deliberate choice for the full integration.
            if (integrationMode != "full")
            {
                forceFull = true;
                notes.Add(Strings.Get("shell.notFull"));
            }
        }
        else if (ShellRegistration.NamesCouchHud(value))
        {
            notes.Add(Strings.Get("shell.otherInstall", exe));
        }
        foreach (var note in notes) Console.Error.WriteLine($"[shell] reconcile: {note}");
        return new ShellReconcile(forceFull, notes);
    }

    /// <summary>
    /// Whether this session has a desktop with taskbar. Counting explorer.exe
    /// processes is wrong: folder windows and other users' sessions have them too.
    /// </summary>
    public static bool IsDesktopShellRunning() => NativeMethods.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;

    /// <summary>
    /// Polls for the taskbar: true as soon as it is there, false when the time is up.
    /// Without an explorer.exe in this session nothing can show up, so that answers at once
    /// (the usual case as the shell; no delay at every boot).
    /// </summary>
    public static bool WaitForDesktopShell(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!IsDesktopShellRunning())
        {
            if (DateTime.UtcNow >= until || !ExplorerInSession()) return false;
            Thread.Sleep(150);
        }
        return true;
    }

    private static bool ExplorerInSession()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var processes = Process.GetProcessesByName("explorer");
        try
        {
            return processes.Any(p => p.SessionId == session);
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    /// <summary>
    /// Starts the regular Windows desktop. explorer.exe only brings up taskbar
    /// and desktop if it believes it *is* the shell, so we briefly point the
    /// Shell value back at it, start it, and restore our own entry afterwards.
    /// </summary>
    public async Task EnterPcModeAsync()
    {
        if (IsDesktopShellRunning()) return;

        // An explorer.exe without taskbar is a leftover (shell value was ours when it started): it would
        // answer our new explorer.exe by opening a folder window instead of becoming the desktop.
        LeavePcMode(killExplorer: true);

        var ourShell = IsInstalledAsShell();
        if (ourShell)
        {
            // Before the value points at explorer: if the PC goes down or this process dies in the window, the next sign-in
            // (an automatic one lands on explorer then) puts the shell back and locks (--restore-shell).
            registry.WriteString(RunOnceKey, "CouchHudRestoreShell", $"{SelfCommand} {RestoreSwitch}");
            registry.WriteString(WinlogonKey, "Shell", "explorer.exe");
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();
            // Explorer reads the value while it starts: wait for the taskbar before restoring.
            // ConfigureAwait(false): this is also awaited synchronously in the emergency path.
            var started = DateTime.UtcNow;
            while (!IsDesktopShellRunning())
            {
                if (DateTime.UtcNow - started > TimeSpan.FromSeconds(20))
                {
                    throw new InvalidOperationException(Strings.Get("shell.desktopNotStarted"));
                }
                await Task.Delay(250).ConfigureAwait(false);
            }
            Console.WriteLine($"[shell] desktop is up after {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
            await Task.Delay(500).ConfigureAwait(false);
        }
        finally
        {
            if (ourShell)
            {
                try
                {
                    registry.WriteString(WinlogonKey, "Shell", SelfCommand);
                    Console.WriteLine($"[shell] shell value restored: {ShellValue()}");
                    // Back in order: the safety net is not needed any more.
                    ClearRestoreFallback();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[shell] could not restore the shell value: {ex.Message}");
                }
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Closes the taskbar the way "Exit Explorer" in its context menu does (message 0x5B4),
    /// so Windows does not restart it right away. Used by the light integration.
    /// </summary>
    public static bool ExitExplorer()
    {
        var tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        return tray != IntPtr.Zero && PostMessage(tray, 0x5B4, IntPtr.Zero, IntPtr.Zero);
    }

    public static void StartExplorer() =>
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();

    public static void LeavePcMode(bool killExplorer)
    {
        if (!killExplorer) return;
        // We started this explorer ourselves, so Winlogon does not know it and does not restart it
        // (it only restarts the shell process it launched, and that is CouchHud).
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            using (p)
            {
                if (p.SessionId != session) continue;
                try
                {
                    p.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    public const string RestoreSwitch = "--restore-shell";
    private const string RunOnceKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce";

    /// <summary>
    /// Restarts once into the normal desktop: the shell entry goes away now and a
    /// RunOnce entry puts it back at that login, so the boot after is the couch again.
    /// The caller also sets pendingShellRestore in the config, the fallback when RunOnce does not run.
    /// </summary>
    public void RestartIntoPcMode()
    {
        if (IsInstalledAsShell())
        {
            registry.WriteString(RunOnceKey, "CouchHudRestoreShell", $"{SelfCommand} {RestoreSwitch}");
            UninstallAsShell();
        }
        Power("restart");
    }

    public static void Power(string action)
    {
        switch (action)
        {
            case "shutdown":
                Run("shutdown.exe", "/s /t 0");
                break;
            case "restart":
                Run("shutdown.exe", "/r /t 0");
                break;
            case "logoff":
                Run("shutdown.exe", "/l");
                break;
            case "sleep":
                // Fails e.g. on some Modern Standby machines; tell the UI instead of doing nothing.
                if (!System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, force: false, disableWakeEvent: false))
                {
                    throw new InvalidOperationException(Strings.Get("shell.noStandby"));
                }
                break;
            default:
                throw new ArgumentException($"unknown power action {action}", nameof(action));
        }
    }

    private static void Run(string file, string args) =>
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
}
