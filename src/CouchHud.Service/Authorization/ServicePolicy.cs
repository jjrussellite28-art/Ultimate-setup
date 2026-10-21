namespace CouchHud.Service.Authorization;

/// <summary>
/// Which Windows services a caller may pause or resume. The allowlist comes from the service's own fixed list (GamingServices,
/// P5); the denylist here is hard-coded and wins over any list, so a mistake in the allowlist can never stop protection, updates,
/// the firewall, the event log or this service itself.
/// </summary>
public static class ServicePolicy
{
    public static readonly IReadOnlySet<string> Denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Defender and its parts.
        "WinDefend", "WdNisSvc", "WdNisDrv", "WdFilter", "WdBoot", "Sense", "SecurityHealthService", "wscsvc", "MDCoreSvc",
        // Updates.
        "wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS", "TrustedInstaller", "DoSvc",
        // Firewall and filtering.
        "mpssvc", "BFE", "IKEEXT", "PolicyAgent",
        // Logging, identity, the core of the system.
        "EventLog", "Schedule", "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "Winmgmt", "CryptSvc", "ProfSvc",
        "gpsvc", "AppIDSvc", "AppXSvc", "ClipSVC", "KeyIso", "VaultSvc", "Netlogon", "seclogon", "UserManager",
        // Touch keyboard and pen input: suggested by the shell, but the service does not pause it (input is what a locked couch
        // needs to get out again; the shell's own suggestion list stays as it is).
        "TabletInputService",
        // This service, under its SCM name and its binary name.
        ServiceHardening.ServiceName, "CouchHud.Service",
    };

    /// <summary>On the list and not denied.</summary>
    public static bool IsAllowed(string name, Func<string, bool> onList) =>
        !string.IsNullOrEmpty(name) && !Denied.Contains(name) && onList(name);
}
