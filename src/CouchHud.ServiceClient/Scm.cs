using System.Runtime.InteropServices;

namespace CouchHud.ServiceClient;

/// <summary>What the service control manager says about the service.</summary>
public enum ScmState
{
    /// <summary>The SCM could not be asked (access refused, API failure): nothing is known.</summary>
    Unknown,
    NotInstalled,
    Stopped,
    StartPending,
    StopPending,
    Running,
    /// <summary>Paused or continue pending: the service never does that, so it counts as not usable.</summary>
    Other
}

/// <summary>
/// One look at the service in the SCM. <see cref="ProcessId"/> is the process the SCM started (0 when it does not run): the pipe
/// server must be exactly that process. <see cref="Win32ExitCode"/> and <see cref="ServiceSpecificExitCode"/> say why a stopped
/// service stopped (the service reports 1066 and <see cref="Squatting.ExitCode"/> when someone else held its pipe name).
/// </summary>
public sealed record ScmInfo(ScmState State, int ProcessId, int Win32ExitCode = 0, int ServiceSpecificExitCode = 0, bool Disabled = false)
{
    public static readonly ScmInfo Unknown = new(ScmState.Unknown, 0);

    public static readonly ScmInfo NotInstalled = new(ScmState.NotInstalled, 0);

    /// <summary>The service stopped because its pipe name was taken before it started (see the service's ExitPipeSquatted).</summary>
    public bool StoppedBySquatting =>
        State == ScmState.Stopped && Win32ExitCode == Squatting.ServiceSpecificError && ServiceSpecificExitCode == Squatting.ExitCode;
}

/// <summary>The exit codes of the service that mean "someone else holds the pipe name" (the same values as in CouchHud.Service).</summary>
public static class Squatting
{
    /// <summary>ERROR_SERVICE_SPECIFIC_ERROR: the reason is in dwServiceSpecificExitCode.</summary>
    public const int ServiceSpecificError = 1066;

    /// <summary>The service's own code for "the pipe name was taken before the service started".</summary>
    public const int ExitCode = 0xC0D1;
}

/// <summary>The service in the SCM, for the shell (query and start; an interactive user may do both, not stop).</summary>
public interface IServiceControl
{
    ScmInfo Query();

    /// <summary>Asks the SCM to start the service (interactive users have SERVICE_START); false when that was refused or failed.</summary>
    bool TryStart();
}

/// <summary>The real SCM through advapi32 (no ServiceController: it asks for more rights than needed and hides the exit codes).</summary>
public sealed class WindowsServiceControl(string serviceName) : IServiceControl
{
    /// <summary>The SCM name of the service (CouchHud.Service's ServiceHardening.ServiceName).</summary>
    public const string DefaultServiceName = "CouchHud";

    public static WindowsServiceControl Default { get; } = new(DefaultServiceName);

    public string ServiceName { get; } = serviceName;

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ScStatusProcessInfo = 0;
    private const int ServiceDisabled = 4;

    public ScmInfo Query()
    {
        try
        {
            using var scm = Native.OpenSCManagerW(null, null, ScManagerConnect);
            if (scm.IsInvalid) return ScmInfo.Unknown;
            using var service = Native.OpenServiceW(scm, ServiceName, ServiceQueryStatus | ServiceQueryConfig);
            if (service.IsInvalid)
            {
                return Marshal.GetLastPInvokeError() == ErrorServiceDoesNotExist ? ScmInfo.NotInstalled : ScmInfo.Unknown;
            }
            if (!Native.QueryServiceStatusEx(service, ScStatusProcessInfo, out var status, Marshal.SizeOf<Native.ServiceStatusProcess>(), out _))
            {
                return ScmInfo.Unknown;
            }
            var state = status.CurrentState switch
            {
                1 => ScmState.Stopped,
                2 => ScmState.StartPending,
                3 => ScmState.StopPending,
                4 => ScmState.Running,
                _ => ScmState.Other
            };
            return new ScmInfo(state, state == ScmState.Running ? status.ProcessId : 0, status.Win32ExitCode, status.ServiceSpecificExitCode, StartType(service) == ServiceDisabled);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return ScmInfo.Unknown;
        }
    }

    public bool TryStart()
    {
        try
        {
            using var scm = Native.OpenSCManagerW(null, null, ScManagerConnect);
            if (scm.IsInvalid) return false;
            using var service = Native.OpenServiceW(scm, ServiceName, ServiceStart);
            if (service.IsInvalid) return false;
            return Native.StartServiceW(service, 0, IntPtr.Zero) || Marshal.GetLastPInvokeError() == ErrorServiceAlreadyRunning;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>dwStartType of QUERY_SERVICE_CONFIGW (the second DWORD), -1 when unreadable.</summary>
    private static int StartType(Native.ScHandle service)
    {
        Native.QueryServiceConfigW(service, IntPtr.Zero, 0, out var needed);
        if (needed <= 0 || needed > 64 * 1024) return -1;
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            return Native.QueryServiceConfigW(service, buffer, needed, out _) ? Marshal.ReadInt32(buffer, 4) : -1;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct ServiceStatusProcess
        {
            public int ServiceType;
            public int CurrentState;
            public int ControlsAccepted;
            public int Win32ExitCode;
            public int ServiceSpecificExitCode;
            public int CheckPoint;
            public int WaitHint;
            public int ProcessId;
            public int ServiceFlags;
        }

        public sealed class ScHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(true)
        {
            protected override bool ReleaseHandle() => CloseServiceHandle(handle);
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ScHandle OpenSCManagerW(string? machine, string? database, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ScHandle OpenServiceW(ScHandle scm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryServiceStatusEx(ScHandle service, int infoLevel, out ServiceStatusProcess status, int size, out int needed);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryServiceConfigW(ScHandle service, IntPtr buffer, int size, out int needed);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool StartServiceW(ScHandle service, int argc, IntPtr argv);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr handle);
    }
}
