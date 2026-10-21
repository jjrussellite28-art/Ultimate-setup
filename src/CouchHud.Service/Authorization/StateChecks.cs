using CouchHud.Service.Contracts;

namespace CouchHud.Service.Authorization;

/// <summary>One state check (see <see cref="StateCheck"/>): allow, need a grant, or deny with a reason.</summary>
public interface IStateCheck
{
    AuthorizationDecision Check(CallerInfo caller, ValidatedRequest request);
}

/// <summary>A check from a function (for the built-in ones and for tests).</summary>
public sealed class StateCheckFunc(Func<CallerInfo, ValidatedRequest, AuthorizationDecision> check) : IStateCheck
{
    public AuthorizationDecision Check(CallerInfo caller, ValidatedRequest request) => check(caller, request);
}

/// <summary>
/// The registered implementations of <see cref="StateCheck"/>. Runs every check of a rule; one that is not registered, that throws
/// or that denies ends it with a denial. Later packages (P5, P6, P9) register theirs; until then those operations stay closed.
/// </summary>
public sealed class StateChecks
{
    private readonly Dictionary<StateCheck, IStateCheck> _checks = [];

    public StateChecks Register(StateCheck kind, IStateCheck check)
    {
        if (kind == StateCheck.None) throw new ArgumentException("None needs no implementation", nameof(kind));
        _checks[kind] = check ?? throw new ArgumentNullException(nameof(check));
        return this;
    }

    public bool IsRegistered(StateCheck kind) => _checks.ContainsKey(kind);

    /// <summary>The checks available now: the service list, a code that is not enrolled yet, and Windows Update without ids.</summary>
    public static StateChecks BuiltIn(Func<string, bool> serviceOnList, ICodeState codes) => new StateChecks()
        .Register(StateCheck.ServiceAllowlist, new StateCheckFunc((_, request) =>
            request.Args is ServicesArgs { Names: { Length: > 0 } names } && names.All(n => ServicePolicy.IsAllowed(n, serviceOnList))
                ? AuthorizationDecision.Allowed
                : AuthorizationDecision.Denied("service not on the list")))
        // Without the code part (CodeHandlers registers the check per kind) only an account without any code may enroll.
        .Register(StateCheck.EnrollAllowed, new StateCheckFunc((caller, _) =>
            codes.IsEnrolled(caller.Sid) ? AuthorizationDecision.Denied("a code exists already") : AuthorizationDecision.Allowed))
        // Without the Windows Update part (HandlersB registers the check against the service's own last search) only "all" is possible.
        .Register(StateCheck.UpdateIdsFromSearch, new StateCheckFunc((_, request) =>
            request.Args is InstallWindowsUpdatesArgs { Ids: null } ? AuthorizationDecision.Allowed : AuthorizationDecision.Denied("update ids not from a search")));

    /// <summary>Allow, need-a-grant (when any check asks for one and none denies) or the first denial.</summary>
    public AuthorizationDecision Run(Rule rule, CallerInfo caller, ValidatedRequest request)
    {
        var needGrant = false;
        foreach (var kind in rule.Checks)
        {
            if (kind == StateCheck.None) continue;
            if (!_checks.TryGetValue(kind, out var check)) return AuthorizationDecision.Denied($"state check {kind} not available");
            AuthorizationDecision decision;
            try
            {
                decision = check.Check(caller, request);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return AuthorizationDecision.Denied($"state check {kind} failed");
            }
            if (decision.Verdict == Verdict.Deny) return decision;
            if (decision.Verdict == Verdict.NeedGrant) needGrant = true;
        }
        return needGrant ? AuthorizationDecision.GrantNeeded : AuthorizationDecision.Allowed;
    }
}
