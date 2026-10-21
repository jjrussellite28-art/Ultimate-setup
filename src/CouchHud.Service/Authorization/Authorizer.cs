using System.Text.RegularExpressions;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Authorization;

public enum Verdict
{
    Allow,
    /// <summary>Allowed with a valid grant (the dispatcher asks the grant store); without one the answer is <see cref="ErrorCodes.NeedGrant"/>.</summary>
    NeedGrant,
    Deny
}

/// <summary>The decision and, for a denial, why (for the log; the caller only sees the error code).</summary>
public sealed record AuthorizationDecision(Verdict Verdict, string? Reason = null)
{
    public static readonly AuthorizationDecision Allowed = new(Verdict.Allow);
    public static readonly AuthorizationDecision GrantNeeded = new(Verdict.NeedGrant);

    public static AuthorizationDecision Denied(string reason) => new(Verdict.Deny, reason);
}

/// <summary>
/// The authorization policy as a pure function: who the caller is (<see cref="CallerInfo"/>, from the token) and what it asks for
/// (a request that passed the validator) give allow, need-a-grant or deny. Fail-closed: an unknown caller, an operation without a
/// rule, or anything unexpected is a denial.
/// </summary>
public static class Authorizer
{
    // Only real user accounts: local and domain accounts (S-1-5-21-…) and Entra ID accounts (S-1-12-1-…). Everything else
    // (SYSTEM, LOCAL/NETWORK SERVICE, service SIDs S-1-5-80, virtual accounts, anonymous, well-known groups) is no user of the couch.
    private static readonly Regex UserSid = new(@"^S-1-(5-21|12-1)(-[0-9]{1,10}){1,14}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // Well-known RIDs of local/domain accounts that are not a person: Guest, DefaultAccount, WDAGUtilityAccount. Only for
    // S-1-5-21-x-y-z-RID: an Entra ID SID (S-1-12-1-…) has no RID in that sense, its last part is just part of a GUID.
    private static readonly Regex NonPersonAccount = new(@"^S-1-5-21-[0-9]{1,10}-[0-9]{1,10}-[0-9]{1,10}-(501|503|504)\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Why this caller may not use the service at all, or null when it may. Run before any request is read.</summary>
    public static string? CheckCaller(CallerInfo? caller)
    {
        if (caller is null) return "no caller identity";
        if (caller.IsAnonymous) return "anonymous";
        if (caller.Sid is null || !UserSid.IsMatch(caller.Sid)) return "not a user account";
        if (NonPersonAccount.IsMatch(caller.Sid)) return "guest or built-in non-person account";
        if (caller.LogonId == 0) return "no logon session";
        if (caller.IsNetwork) return "network logon";
        if (caller.IsService) return "service or batch logon";
        if (!caller.IsInteractive) return "not an interactive logon";
        if (caller.IsAppContainer) return "AppContainer";
        if (caller.IsRestricted) return "restricted token";
        if (caller.IntegrityLevel < IntegrityLevels.Medium) return "integrity below medium";
        if (caller.ProcessId <= 0) return "no client process";
        if (caller.PipeSessionId <= 0) return "session 0";
        if (caller.TokenSessionId != caller.PipeSessionId) return "token session differs from pipe session";
        return null;
    }

    /// <summary>
    /// The decision for one request. <paramref name="codeEnrolled"/> is whether a code exists for the caller's account; when that is
    /// unknown, pass true (fail-closed: a release then needs a grant or an administrator). First the table, then every state check
    /// of the rule (<see cref="StateChecks.Run"/>): a denial from either wins, then need-a-grant.
    /// </summary>
    public static AuthorizationDecision Authorize(CallerInfo? caller, ValidatedRequest request, bool codeEnrolled, StateChecks checks)
    {
        var table = AuthorizeTable(caller, request, codeEnrolled);
        if (table.Verdict == Verdict.Deny) return table;
        var state = checks.Run(OperationPolicy.For(request)!, caller!, request);
        if (state.Verdict == Verdict.Deny) return state;
        return table.Verdict == Verdict.NeedGrant || state.Verdict == Verdict.NeedGrant ? AuthorizationDecision.GrantNeeded : AuthorizationDecision.Allowed;
    }

    /// <summary>The table alone, without the state checks: internal, so nothing outside the tests decides on it.</summary>
    internal static AuthorizationDecision AuthorizeTable(CallerInfo? caller, ValidatedRequest request, bool codeEnrolled)
    {
        if (CheckCaller(caller) is { } reason) return AuthorizationDecision.Denied(reason);
        if (OperationPolicy.For(request) is not { } rule) return AuthorizationDecision.Denied("no rule for the operation");
        return rule.Requirement switch
        {
            Requirement.Anyone or Requirement.OwnAccount => AuthorizationDecision.Allowed,
            Requirement.Admin => caller!.IsElevatedAdmin ? AuthorizationDecision.Allowed : AuthorizationDecision.Denied("needs an elevated administrator"),
            Requirement.GrantOrAdmin or Requirement.ParentGrantOrAdmin => caller!.IsElevatedAdmin ? AuthorizationDecision.Allowed : AuthorizationDecision.GrantNeeded,
            Requirement.GrantOrAdminOnceCodeEnrolled => caller!.IsElevatedAdmin || !codeEnrolled ? AuthorizationDecision.Allowed : AuthorizationDecision.GrantNeeded,
            _ => AuthorizationDecision.Denied("unknown requirement")
        };
    }
}
