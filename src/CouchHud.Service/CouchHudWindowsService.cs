using System.Runtime.InteropServices;
using System.ServiceProcess;
using CouchHud.Privileged;

namespace CouchHud.Service;

/// <summary>
/// The SCM side: start and stop the runtime. When the pipe server fails (a squatted name, a broken pipe) the service reports
/// SERVICE_STOPPED itself with ERROR_SERVICE_SPECIFIC_ERROR (1066) and its own code in dwServiceSpecificExitCode
/// (<see cref="ServiceHardening.ExitPipeSquatted"/> or <see cref="ServiceHardening.ExitPipeFailed"/>), then ends the process.
/// ServiceBase has no setter for the service-specific code, and its Stop() cannot run while the start is still pending (the
/// failure can come that early), hence SetServiceStatus directly and Environment.Exit. A stop with an error counts as a failure
/// (sc failureflag 1), so the failure actions (<see cref="ServiceHardening.FailureActions"/>) restart the service.
/// </summary>
public sealed class CouchHudWindowsService : ServiceBase
{
    private readonly IEventSink _log;
    private readonly ServiceRuntime _runtime;

    public CouchHudWindowsService(IEventSink log)
    {
        ServiceName = ServiceHardening.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanPauseAndContinue = false;
        // SERVICE_CONTROL_SESSIONCHANGE: a signed-out account's holds on paused services end (P5).
        CanHandleSessionChangeEvent = true;
        // The runtime writes its own log; ServiceBase's own entries would go to a source of another name.
        AutoLog = false;
        _log = log;
        _runtime = new ServiceRuntime(log);
        _runtime.Failed += FailAndExit;
    }

    protected override void OnStart(string[] args) => _runtime.Start();

    protected override void OnStop() => _runtime.Stop(TimeSpan.FromSeconds(15));

    protected override void OnShutdown() => _runtime.Stop(TimeSpan.FromSeconds(5));

    protected override void OnSessionChange(SessionChangeDescription change) => _runtime.SessionChanged(Map(change.Reason), change.SessionId);

    /// <summary>SERVICE_USER_DEFINED_CONTROL (128 to 255): an administrator's approval through UAC (see AdminApprovals).</summary>
    protected override void OnCustomCommand(int command) => _runtime.CustomCommand(command);

    /// <summary>What the grants care about (P9): sign-in and unlock give a Windows grant, lock, disconnect and sign-out end every grant of the session.</summary>
    internal static Authorization.SessionChange Map(SessionChangeReason reason) => reason switch
    {
        SessionChangeReason.SessionLogon => Authorization.SessionChange.Logon,
        SessionChangeReason.SessionUnlock => Authorization.SessionChange.Unlock,
        SessionChangeReason.SessionLock => Authorization.SessionChange.Lock,
        SessionChangeReason.SessionLogoff => Authorization.SessionChange.Logoff,
        SessionChangeReason.ConsoleDisconnect or SessionChangeReason.RemoteDisconnect => Authorization.SessionChange.Disconnect,
        _ => Authorization.SessionChange.Other
    };

    private void FailAndExit(int code)
    {
        _log.Error($"[stop] stopping with service-specific code 0x{code:X}");
        ExitCode = ServiceHardening.ServiceSpecificError;
        var status = new ServiceStatus
        {
            ServiceType = 0x10,   // SERVICE_WIN32_OWN_PROCESS
            CurrentState = 1,     // SERVICE_STOPPED
            Win32ExitCode = ServiceHardening.ServiceSpecificError,
            ServiceSpecificExitCode = code
        };
        if (!SetServiceStatus(ServiceHandle, ref status)) _log.Error($"[stop] SetServiceStatus failed (Win32 {Marshal.GetLastPInvokeError()})");
        Environment.Exit(code);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
