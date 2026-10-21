using CouchHud.Privileged;
using CouchHud.Service.Logging;
using CouchHud.Service.State;

namespace CouchHud.Service.Dispatch;

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
        // {app}\service\CouchHud.Service.exe: the program folder is the one above the service's own.
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
            new WindowsSessions(accounts));
    }

    /// <summary>When the machine started (Unix ms), from the kernel; it changes only with a restart. 0 when unknown (then every start counts as a restart).</summary>
    public static long BootTimeMs() => SystemBoot.TimeMs() ?? 0;
}
