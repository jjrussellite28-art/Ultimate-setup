using MegaHub.Privileged;
using MegaHub.Service.Logging;
using MegaHub.Service.State;

namespace MegaHub.Service.Dispatch;

/// <summary>The real Windows parts of <see cref="OperationsAParts"/>, put together once by the service.</summary>
public static class OperationsAWiring
{
    /// <summary>The service's own folder, from AppContext.BaseDirectory (without the trailing separator).</summary>
    public static string ServiceDirectory(string serviceBaseDirectory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(serviceBaseDirectory));

    /// <summary>
    /// <c>{app}\service\data</c> (ServiceInstall.DataFolder): the chain from the service folder down is checked, the folder itself created
    /// protected when the setup has not. Null when it cannot be trusted (the state and the codes are then unavailable, fail-closed).
    /// </summary>
    public static string? EnsureDataFolder(IEventSink log)
    {
        var serviceDir = ServiceDirectory(AppContext.BaseDirectory);
        var dataFolder = ServiceInstall.DataFolder(serviceDir);
        return new WindowsProtectedFolder(serviceDir, Path.GetFileName(dataFolder), log).Ensure();
    }

    /// <param name="folder">The checked data folder (<see cref="EnsureDataFolder"/>), null when refused.</param>
    public static OperationsAParts Create(IEventSink log, RecentEvents events, Func<string, bool> serviceOnList, string? folder)
    {
        var serviceDir = ServiceDirectory(AppContext.BaseDirectory);
        // {app}\service\MegaHub.Service.exe: the program folder is the one above the service's own.
        var appDir = Path.GetDirectoryName(serviceDir) ?? serviceDir;
        var state = ServiceStateStore.Open(folder, log, BootTimeMs, serviceOnList);
        var processes = new ProcessLauncher();
        var tasks = new SchtasksScheduler(processes, log);
        var accounts = new WindowsAccounts();
        return new OperationsAParts(
            state,
            new WindowsUserHives(log),
            new WindowsScm(),
            new WindowsFirewall(processes, log),
            new AutoLogonSetup(new LsaSecrets(log)),
            accounts,
            new WindowsLogonVerifier(),
            tasks,
            new PolicyInstaller(tasks, log, appDir),
            appDir,
            log,
            events,
            serviceOnList,
            () => DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(8),
            new WindowsSessions(accounts),
            () => ShellRuns(appDir, log));
    }

    /// <summary>
    /// Whether a MegaHub.exe from this installation's program folder runs on the machine (any account). Only the path of the running image
    /// counts, not the name (<see cref="IsShellImage"/>); an image whose path cannot be read does not count (the watch then lets go rather than
    /// hold on), and that is logged once, with the count only.
    /// </summary>
    public static bool ShellRuns(string appDir, IEventSink? log = null)
    {
        var unreadable = 0;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("MegaHub"))
        {
            using (process)
            {
                try
                {
                    if (IsShellImage(process.MainModule?.FileName, appDir)) return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    unreadable++;
                }
            }
        }
        if (unreadable > 0 && !_unreadableLogged)
        {
            _unreadableLogged = true;
            log?.Error($"[services] the image path of {unreadable} MegaHub process(es) cannot be read; they do not keep a pause alive");
        }
        return false;
    }

    private static bool _unreadableLogged;

    /// <summary>
    /// The image is this installation's <c>MegaHub.exe</c>: the same full path as <c>{appDir}\MegaHub.exe</c>, ignoring case and a trailing
    /// separator on the folder; the same file name in another folder, another file name and an unreadable (null or empty) path do not match.
    /// </summary>
    public static bool IsShellImage(string? imagePath, string appDir)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(appDir)) return false;
        try
        {
            var expected = Path.GetFullPath(Path.Combine(appDir.TrimEnd('\\', '/'), "MegaHub.exe"));
            return string.Equals(Path.GetFullPath(imagePath), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>When the machine started (Unix ms), from the kernel; it changes only with a restart. 0 when unknown (then every start counts as a restart).</summary>
    public static long BootTimeMs() => SystemBoot.TimeMs() ?? 0;
}
