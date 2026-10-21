namespace CouchHud.Service.Authorization;

/// <summary>Mandatory integrity levels (the last sub-authority of the S-1-16-x label SID).</summary>
public static class IntegrityLevels
{
    public const int Untrusted = 0x0000;
    public const int Low = 0x1000;
    public const int Medium = 0x2000;
    public const int MediumPlus = 0x2100;
    public const int High = 0x3000;
    public const int System = 0x4000;
}

/// <summary>
/// Who is on the other end of a pipe connection, read by the service from the pipe and the caller's token (never from the request).
/// Everything the authorization decides on is in here, so <see cref="Authorizer"/> is a pure function over it.
/// </summary>
/// <remarks>
/// Grants (P9) bind to <see cref="Sid"/> + <see cref="LogonId"/> + <see cref="TokenSessionId"/>, never to the process id: a PID is
/// reused, and any process of the same logon could ask anyway. The logon LUID ends with the sign-out, so a grant cannot outlive it.
/// </remarks>
/// <param name="Sid">The user SID of the token (TokenUser). The only account any operation acts on.</param>
/// <param name="LogonId">TokenStatistics.AuthenticationId: the LUID of the logon session the token belongs to (0: unknown, refused).</param>
/// <param name="ProcessId">GetNamedPipeClientProcessId (for the log only).</param>
/// <param name="PipeSessionId">GetNamedPipeClientSessionId.</param>
/// <param name="TokenSessionId">TokenSessionId of the caller's token; must equal <paramref name="PipeSessionId"/>.</param>
/// <param name="IntegrityLevel">RID of the token's mandatory label (see <see cref="IntegrityLevels"/>).</param>
/// <param name="IsInteractive">INTERACTIVE (S-1-5-4) is in the token, enabled and not deny-only (console and RDP sessions).</param>
/// <param name="IsNetwork">NETWORK (S-1-5-2) is in the token in any form.</param>
/// <param name="IsService">SERVICE (S-1-5-6) or BATCH (S-1-5-3) is in the token in any form.</param>
/// <param name="IsAnonymous">ANONYMOUS LOGON (S-1-5-7) is the user or in the token.</param>
/// <param name="IsAppContainer">TokenIsAppContainer.</param>
/// <param name="IsRestricted">The token has restricting SIDs (TokenRestrictedSids count &gt; 0), or that could not be read.</param>
/// <param name="IsAdminGroupEnabled">BUILTIN\Administrators (S-1-5-32-544) is enabled, not deny-only (a UAC-filtered token has it deny-only).</param>
public sealed record CallerInfo(
    string Sid,
    long LogonId,
    int ProcessId,
    int PipeSessionId,
    int TokenSessionId,
    int IntegrityLevel,
    bool IsInteractive,
    bool IsNetwork,
    bool IsService,
    bool IsAnonymous,
    bool IsAppContainer,
    bool IsRestricted,
    bool IsAdminGroupEnabled)
{
    /// <summary>An elevated administrator: the Administrators group is live and the token runs at High integrity or above.</summary>
    public bool IsElevatedAdmin => IsAdminGroupEnabled && IntegrityLevel >= IntegrityLevels.High;

    /// <summary>For log lines: SID and session only (no image path, no profile path, nothing from the request).</summary>
    public string Describe() => $"sid={Sid} session={PipeSessionId}";
}
