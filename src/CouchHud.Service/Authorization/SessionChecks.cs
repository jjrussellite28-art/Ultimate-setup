namespace CouchHud.Service.Authorization;

/// <summary>
/// What the service knows about the machine's Windows sessions, as functions (real ones from WTS in the service, fakes in tests). A
/// function that cannot tell answers null; <see cref="Unknown"/> never tells anything.
/// </summary>
/// <param name="ActiveConsoleSession">The session on the physical console now; null when there is none or it cannot be read.</param>
/// <param name="OtherSignedInSids">The account SIDs signed in to every session except the given one (the caller's); null when they cannot be read.</param>
public sealed record SessionFacts(Func<int?> ActiveConsoleSession, Func<int, IReadOnlySet<string>?> OtherSignedInSids)
{
    public static readonly SessionFacts Unknown = new(() => null, _ => null);
}

/// <summary>
/// The two checks that keep a local standard account from acting on the whole machine (audit findings L-1 and L-2). Both are fail-closed: what
/// cannot be read counts as "ask for an approval". An approval is a parent's or an administrator's (<see cref="AuthorizationDecision.ApprovalNeeded"/>),
/// the existing grant mechanism; an elevated administrator needs none.
/// </summary>
public static class SessionChecks
{
    /// <summary>
    /// PauseServices: stops services for every account of the machine, so only the account at the console (the couch's) may do it on its
    /// own. Any other session (RDP, a second account's disconnected one) needs an approval.
    /// </summary>
    public static AuthorizationDecision ConsoleSessionOrApproval(CallerInfo caller, SessionFacts sessions)
    {
        if (caller.IsElevatedAdmin) return AuthorizationDecision.Allowed;
        int? console;
        try
        {
            console = sessions.ActiveConsoleSession();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            console = null;
        }
        return console is { } id && id > 0 && id == caller.PipeSessionId ? AuthorizationDecision.Allowed : AuthorizationDecision.ApprovalNeeded;
    }

    /// <summary>
    /// Windows Update installation and the CouchHUD update: the setup of the second disconnects the other sessions, and both change the
    /// machine for everybody. With another account signed in anywhere, only an approval lets a standard account start them.
    /// </summary>
    public static AuthorizationDecision OtherSessionsNeedApproval(CallerInfo caller, SessionFacts sessions)
    {
        if (caller.IsElevatedAdmin) return AuthorizationDecision.Allowed;
        IReadOnlySet<string>? others;
        try
        {
            others = sessions.OtherSignedInSids(caller.PipeSessionId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            others = null;
        }
        if (others is null) return AuthorizationDecision.ApprovalNeeded;
        return others.Any(sid => !string.Equals(sid, caller.Sid, StringComparison.OrdinalIgnoreCase)) ? AuthorizationDecision.ApprovalNeeded : AuthorizationDecision.Allowed;
    }
}
