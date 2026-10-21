using System.ServiceProcess;

namespace CouchHud.Privileged;

/// <summary>
/// Where CouchHud.Service lives on a machine: the names the setup (installer/CouchHud.iss), the uninstall cleanup and the service
/// share. The service's own copies (ServiceHardening, EventLogSink) are pinned to these and to the .iss by tests, so they cannot drift.
/// </summary>
public static class ServiceInstall
{
    /// <summary>The SCM name of the service.</summary>
    public const string ServiceName = "CouchHud";

    /// <summary>The event source in the Application log, registered by the setup.</summary>
    public const string EventSource = "CouchHud.Service";

    /// <summary>The registry key of the event source below HKLM.</summary>
    public const string EventSourceKey = @"SYSTEM\CurrentControlSet\Services\EventLog\Application\" + EventSource;

    /// <summary>The service's folder below the install folder (its own folder: two self-contained apps in one folder collide).</summary>
    public const string ServiceFolder = "service";

    public const string ServiceExe = "CouchHud.Service.exe";

    /// <summary>
    /// The emergency repair the setup writes next to CouchHud.exe: native sc.exe, icacls.exe and reg.exe only, absolute paths, run elevated
    /// through UAC as <c>%SystemRoot%\System32\cmd.exe /d /c "&lt;app&gt;\repair-service.cmd"</c>. It re-creates the service with the fixed
    /// hardened configuration and nothing else.
    /// </summary>
    public const string RepairScript = "repair-service.cmd";

    /// <summary>The name of the service's data folder inside its own folder.</summary>
    public const string DataFolderName = "data";

    /// <summary>
    /// The service's state (state.json, codes.json, staging, jobs, logs, the setup log of an update): <c>{app}\service\data</c>, inside
    /// the service folder whose access list the setup sets (SYSTEM and Administrators full, Users read, nothing inherited); the data folder
    /// itself breaks inheritance again and is SYSTEM and Administrators only (no user may read the state, the code hashes or a staged
    /// setup). Not below
    /// ProgramData: any user may create <c>ProgramData\CouchHud</c> before the install and would own it. Created by the setup,
    /// kept by updates, removed with the uninstall.
    /// </summary>
    /// <param name="serviceDirectory">The folder of CouchHud.Service.exe (in the service: <c>AppContext.BaseDirectory</c>).</param>
    public static string DataFolder(string serviceDirectory) => Path.Combine(serviceDirectory, DataFolderName);

    /// <summary>The setup logs of updates the service runs (/LOG=…\setup-&lt;version&gt;.log).</summary>
    public static string SetupLogFolder(string serviceDirectory) => Path.Combine(DataFolder(serviceDirectory), "logs");

    /// <summary>Where 0.4.1 development builds kept the state; only cleaned up once on uninstall (<see cref="ServiceDataCleanup"/>).</summary>
    public static string LegacyProgramDataFolder(string commonApplicationData) => Path.Combine(commonApplicationData, "CouchHud", "Service");

    /// <summary>Win32 errors sc.exe reports as its exit code.</summary>
    public const int ErrorServiceDoesNotExist = 1060;
    public const int ErrorServiceNotActive = 1062;
    public const int ErrorServiceMarkedForDelete = 1072;
}

/// <summary>
/// Takes the service off the machine for the uninstall: start type disabled first (so neither the SCM's failure actions nor an
/// interactive user, who may start it, bring it back), stop, kill if it hangs, delete. Only native programs from the system folder
/// with fixed arguments. Never throws; the result says whether the service is gone (or marked for deletion).
/// </summary>
public sealed class ServiceRemoval
{
    private readonly IProcessLauncher _launcher;
    private readonly IEventSink _log;
    private readonly Func<ServiceControllerStatus?> _status;
    private readonly Action<TimeSpan> _sleep;
    private readonly string _system;

    /// <param name="status">The service's state; null when it is not installed.</param>
    /// <param name="systemDirectory">Where sc.exe and taskkill.exe are (Environment.SystemDirectory, not an environment variable).</param>
    public ServiceRemoval(IProcessLauncher launcher, IEventSink log, Func<ServiceControllerStatus?> status, Action<TimeSpan> sleep, string systemDirectory)
    {
        _launcher = launcher;
        _log = log;
        _status = status;
        _sleep = sleep;
        _system = systemDirectory;
    }

    public static ServiceRemoval ForThisMachine(IProcessLauncher launcher, IEventSink log) =>
        new(launcher, log, QueryStatus, Thread.Sleep, Environment.SystemDirectory);

    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProgramTimeout = TimeSpan.FromSeconds(60);

    /// <summary>True when the service is not installed afterwards (or Windows deletes it once the last handle closes).</summary>
    public bool Remove()
    {
        try
        {
            if (_status() is null)
            {
                _log.Info("[uninstall] service: not installed");
                return true;
            }
            Sc("config", ServiceInstall.ServiceName, "start=", "disabled");
            if (!WaitStopped())
            {
                _log.Error("[uninstall] service: did not stop in time, ending its process");
                Run("taskkill.exe", "/F", "/FI", $"SERVICES eq {ServiceInstall.ServiceName}");
                if (!Wait(KillTimeout)) _log.Error("[uninstall] service: still not stopped, deleting it anyway (Windows removes it at the next restart)");
            }
            var code = Sc("delete", ServiceInstall.ServiceName);
            var gone = code is 0 or ServiceInstall.ErrorServiceDoesNotExist or ServiceInstall.ErrorServiceMarkedForDelete;
            if (gone) _log.Info("[uninstall] service: removed");
            else _log.Error($"[uninstall] service: sc delete failed ({code})");
            return gone;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error($"[uninstall] service: {ex.GetType().Name}");
            return false;
        }
    }

    private bool WaitStopped()
    {
        if (_status() is null or ServiceControllerStatus.Stopped) return true;
        var code = Sc("stop", ServiceInstall.ServiceName);
        if (code is not (0 or ServiceInstall.ErrorServiceNotActive)) _log.Error($"[uninstall] service: sc stop returned {code}");
        return Wait(StopTimeout);
    }

    private bool Wait(TimeSpan limit)
    {
        for (var waited = TimeSpan.Zero; ; waited += Poll)
        {
            if (_status() is null or ServiceControllerStatus.Stopped) return true;
            if (waited >= limit) return false;
            _sleep(Poll);
        }
    }

    private int Sc(params string[] arguments) => Run("sc.exe", arguments);

    private int Run(string program, params string[] arguments)
    {
        var result = _launcher.Run(Path.Combine(_system, program), arguments, ProgramTimeout);
        _log.Info($"[uninstall] service: {program} {string.Join(' ', arguments)} -> {result.ExitCode}");
        return result.ExitCode;
    }

    /// <summary>
    /// Null only when the SCM says the service does not exist. Any other failure (access denied, SCM not reachable) throws: the state
    /// is unknown, <see cref="Remove"/> reports false and the caller leaves the service's data alone.
    /// </summary>
    internal static ServiceControllerStatus? QueryStatus()
    {
        try
        {
            using var service = new ServiceController(ServiceInstall.ServiceName);
            return service.Status;
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: ServiceInstall.ErrorServiceDoesNotExist })
        {
            return null;
        }
    }
}
