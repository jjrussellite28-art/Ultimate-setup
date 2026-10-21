namespace CouchHud.Service;

/// <summary>
/// The security settings of the service in one place, for the service itself and for the installer (P8), which applies the
/// <c>sc</c> ones. Verified in the P0 spike as a real SYSTEM service in the VM.
/// </summary>
public static class ServiceHardening
{
    /// <summary>The SCM name of the service.</summary>
    public const string ServiceName = "CouchHud";

    /// <summary>
    /// The pipe's security descriptor. Owner SYSTEM; protected DACL in canonical order (deny first): NETWORK denied, SYSTEM and
    /// Administrators full, INTERACTIVE users only 0x120183 = ReadData | WriteData | ReadAttributes | WriteAttributes | READ_CONTROL |
    /// SYNCHRONIZE. Not GENERIC_READ/WRITE: those include FILE_APPEND_DATA, which on a pipe is FILE_CREATE_PIPE_INSTANCE (users could
    /// add server instances). Mandatory label Medium, no-write-up and no-read-up: a Low IL process may neither write nor read; AppContainers are not in the DACL.
    /// The SACL label needs the native CreateNamedPipeW (PipeSecurity/NamedPipeServerStreamAcl fail with it as SYSTEM).
    /// A client opens the pipe with exactly ReadData | WriteData | ReadAttributes | WriteAttributes | Synchronize (not InOut).
    /// </summary>
    public const string PipeSddl = "O:SYD:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x120183;;;IU)S:(ML;;NWNR;;;ME)";

    /// <summary><c>sc sidtype CouchHud unrestricted</c>: the service SID for ACLs on ProgramData.</summary>
    public const string SidType = "unrestricted";

    /// <summary><c>sc privs CouchHud …</c>: only these (no SeDebug, no SeTcb). Backup/Restore for RegLoadKey, Shutdown for "restart needed".</summary>
    public static readonly string[] RequiredPrivileges =
        ["SeChangeNotifyPrivilege", "SeImpersonatePrivilege", "SeBackupPrivilege", "SeRestorePrivilege", "SeShutdownPrivilege"];

    /// <summary><c>sc sdset CouchHud …</c>: SYSTEM and Administrators manage it, interactive users may query and start it, not stop it.</summary>
    public const string ServiceSddl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCRPRC;;;IU)";

    /// <summary><c>sc failure CouchHud reset= 86400 actions= …</c> plus <c>sc failureflag CouchHud 1</c> (a stop with an error code counts as a failure).</summary>
    public const string FailureActions = "restart/5000/restart/30000/restart/60000";
    public const int FailureResetSeconds = 86400;

    /// <summary>Pipe instances: <see cref="MaxConnections"/> connected ones plus the one that always listens.</summary>
    public const int MaxConnections = 16;
    public const int MaxPipeInstances = MaxConnections + 1;

    /// <summary>Open connections per account (design §3), so one account cannot take all instances.</summary>
    public const int MaxConnectionsPerAccount = 4;

    /// <summary>The Win32 exit code a service reports when it gives its own reason in dwServiceSpecificExitCode (ERROR_SERVICE_SPECIFIC_ERROR).</summary>
    public const int ServiceSpecificError = 1066;

    /// <summary>Service-specific exit code when the pipe name was taken before the service started (squatting suspected).</summary>
    public const int ExitPipeSquatted = 0xC0D1;

    /// <summary>Service-specific exit code when the pipe server stopped for another reason.</summary>
    public const int ExitPipeFailed = 0xC0D2;
}
