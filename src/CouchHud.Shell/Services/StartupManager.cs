using System.Diagnostics;
using CouchHud.Core.Guard;
using CouchHud.Core.Platform;
using CouchHud.Core.Startup;
using Microsoft.Win32;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

/// <summary>
/// Takes over explorer's autostart duty with two profiles: what runs on the couch
/// at boot, and what waits until the user switches to PC mode.
/// </summary>
public sealed class StartupManager(IRegistry registry)
{
    public IReadOnlyList<StartupEntry> Entries() =>
        StartupCatalog.Read(registry, StartupFolders(), ListFiles);

    /// <summary>At boot as the shell: everything enabled in Windows except the PC-mode entries.</summary>
    public void RunCouch(IReadOnlyDictionary<string, string> modes, Func<string, string>? adapt = null)
    {
        foreach (var (entry, commandLine) in StartupCatalog.CouchCommands(Entries(), modes, adapt))
        {
            Start(entry, commandLine);
        }
    }

    /// <summary>
    /// RunOnce entries (installers that finish after a restart) are explorer's job too. Only the
    /// user's own can be cleared without admin rights; the machine-wide ones are just logged.
    /// </summary>
    public void RunOnce()
    {
        var userKey = StartupCatalog.RunOnceKeys.First(k => k.CurrentUser).Key;
        foreach (var e in StartupCatalog.ReadRunOnce(registry))
        {
            if (!e.CurrentUser)
            {
                Console.WriteLine($"[startup] RunOnce for all users not run (needs admin to clear): {e.Name}: {e.Command}");
                continue;
            }
            try
            {
                // Like Windows: the value goes first, except with a "!" prefix: then after a successful start.
                if (StartupCatalog.DeleteRunOnceBefore(e)) registry.DeleteValue(userKey, e.ValueName);
                var started = Start(new StartupEntry($"RunOnce:{e.Name}", e.Name, e.Command, "RunOnce", false));
                if (StartupCatalog.DeleteRunOnceAfter(e, started)) registry.DeleteValue(userKey, e.ValueName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                Console.Error.WriteLine($"[startup] RunOnce {e.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>On the way to PC mode: the entries that waited, unless they already run.</summary>
    public void RunPc(IReadOnlyDictionary<string, string> modes)
    {
        foreach (var e in Entries().Where(e => !e.DisabledInWindows && StartupCatalog.ModeOf(e, modes) == StartupMode.Pc))
        {
            if (StartupCatalog.ProcessName(e) is { } name && IsRunning(name)) continue;
            Start(e);
        }
    }

    private static readonly HashSet<string> KeepRunning = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update", "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "explorer", "CouchHud"
    };

    /// <summary>Back on the couch: end the PC-mode programs again (tray tools, sync clients …).</summary>
    public int ClosePc(IReadOnlyDictionary<string, string> modes)
    {
        var closed = 0;
        var ctl = Win32ProcessControl.Instance;
        foreach (var name in Entries().Where(e => StartupCatalog.ModeOf(e, modes) == StartupMode.Pc).Select(StartupCatalog.ProcessName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Updaters like Squirrel's Update.exe only start the real program, and browsers have
            // the user's open windows: never end those by name.
            if (KeepRunning.Contains(name)) continue;
            // Only what is still the program found now (start time, image), only in this session, never Windows' own: see SafeTermination.
            var stamps = SafeTermination.StampsByName(name, ctl.List(), ctl);
            if (stamps.Count == 0) continue;
            try
            {
                var outcome = Task.Run(() => SafeTermination.QuitAsync(ctl, stamps, false, TimeSpan.FromSeconds(3))).GetAwaiter().GetResult();
                closed += outcome.Found;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine($"[startup] could not close {name}: {ex.Message}");
            }
        }
        return closed;
    }

    /// <summary>
    /// Explorer runs the autostart entries once per session when it becomes the shell.
    /// CouchHud did that already (and starts the PC-mode ones itself), so mark it done
    /// to keep explorer from starting everything a second time.
    /// </summary>
    public static void MarkStartupDoneForExplorer()
    {
        try
        {
            var session = Process.GetCurrentProcess().SessionId;
            using var key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{session}\StartupHasBeenRun",
                writable: true,
                RegistryOptions.Volatile);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"[startup] could not mark autostart as done: {ex.Message}");
        }
    }

    private static IEnumerable<(string, string)> StartupFolders() =>
    [
        (Environment.GetFolderPath(Environment.SpecialFolder.Startup), Strings.Get("startup.folder")),
        (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Strings.Get("startup.folderAll"))
    ];

    private static IEnumerable<string> ListFiles(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder) : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsRunning(string processName)
    {
        var session = Process.GetCurrentProcess().SessionId;
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Any(p => p.SessionId == session);
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private static bool Start(StartupEntry entry) => Start(entry, Environment.ExpandEnvironmentVariables(entry.Command));

    private static bool Start(StartupEntry entry, string commandLine)
    {
        Console.WriteLine($"[startup] {entry.Name}: {commandLine}");
        try
        {
            var (file, args) = CommandLine.Split(commandLine);
            Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.IsPathRooted(file) ? Path.GetDirectoryName(file) ?? "" : ""
            })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[startup] {entry.Name} failed: {ex.Message}");
            return false;
        }
    }
}
