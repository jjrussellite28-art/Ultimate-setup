using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Logging;
using CouchHud.Service.State;

namespace CouchHud.Service.Dispatch;

/// <summary>What the code and grant operations of package P9 work with.</summary>
/// <param name="IsBlankPassword">Whether an account has no password (its unlock proves nothing, review H1); see <see cref="BlankPasswordCheck"/>.</param>
public sealed record CodeParts(CodeStore Codes, GrantStore Grants, SessionEvents Sessions, AdminApprovals Approvals, Func<string, bool> IsBlankPassword,
    IEventSink Log, RecentEvents? Events);

/// <summary>
/// Package P9 ("Grants mit Code"): EnrollPasscode, ChangePasscode, RemovePasscode, SetLockMode, VerifyCode, RequestWindowsGrant ("unlock"),
/// RequestAdminApproval and CollectAdminApproval, and the state checks they own (<see cref="StateCheck.EnrollAllowed"/>,
/// <see cref="StateCheck.CodeExists"/>, <see cref="StateCheck.LockModeChange"/>). Which kinds of grant count is decided by
/// <see cref="OperationPolicy.AcceptedGrants(ValidatedRequest, LockView)"/> from the account's lock (<see cref="CodeStore.View"/>). The account
/// is always the token's SID; no grant, code or hash ever reaches a log line, and no administrator credential ever reaches the service at all
/// (approvals go through UAC, see <see cref="AdminApprovals"/>).
/// </summary>
public sealed class CodeHandlers(CodeParts parts)
{
    public static CodeHandlers Register(Dictionary<Operation, Handler> handlers, StateChecks checks, CodeParts parts)
    {
        var codes = new CodeHandlers(parts);
        handlers[Operation.EnrollPasscode] = (context, _) => ValueTask.FromResult(codes.Store(context, enroll: true));
        handlers[Operation.ChangePasscode] = (context, _) => ValueTask.FromResult(codes.Store(context, enroll: false));
        handlers[Operation.RemovePasscode] = (context, _) => ValueTask.FromResult(codes.Remove(context));
        handlers[Operation.SetLockMode] = (context, _) => ValueTask.FromResult(codes.SetMode(context));
        handlers[Operation.VerifyCode] = (context, _) => ValueTask.FromResult(codes.Verify(context));
        handlers[Operation.RequestWindowsGrant] = (context, _) => ValueTask.FromResult(codes.UnlockGrant(context.Caller));
        handlers[Operation.RequestAdminApproval] = (context, _) => ValueTask.FromResult(codes.RequestApproval(context));
        handlers[Operation.CollectAdminApproval] = (context, _) => ValueTask.FromResult(codes.CollectApproval(context));
        checks.Register(StateCheck.EnrollAllowed, new StateCheckFunc(codes.EnrollAllowed));
        checks.Register(StateCheck.CodeExists, new StateCheckFunc(codes.CodeExists));
        checks.Register(StateCheck.LockModeChange, new StateCheckFunc(codes.LockModeChange));
        return codes;
    }

    /// <summary>The account's lock, for the dispatcher and the update's session (which grants count).</summary>
    public LockView View(string sid) => parts.Codes.View(sid);

    /// <summary>An administrator approved through UAC (SERVICE_USER_DEFINED_CONTROL, from the SCM's thread): quick, in memory.</summary>
    public void OnControl(int control)
    {
        if (control is < AdminApprovals.FirstControl or > AdminApprovals.LastControl) return;
        parts.Log.Info(parts.Approvals.Approve(control)
            ? $"[grant] administrator approval {control} given (UAC)"
            : $"[grant] control {control} matches no pending approval");
    }

    private static IReadOnlyCollection<CodeKind>? KindsOf(string? kind) => kind switch
    {
        CodeKinds.Couch => [CodeKind.Couch],
        CodeKinds.Parent => [CodeKind.Parent],
        CodeKinds.Both => [CodeKind.Couch, CodeKind.Parent],
        _ => null
    };

    private static IReadOnlyCollection<CodeKind>? KindsOf(ValidatedRequest request) => request.Args switch
    {
        PasscodeHashArgs args => KindsOf(args.Kind),
        CodeKindArgs args => KindsOf(args.Kind),
        _ => null
    };

    /// <summary>
    /// A first code for the caller's own account, never over an existing one (that is ChangePasscode). Free only while the account's couch is
    /// not locked at all (mode off or never set, no code: trust on first use, also the shell's one-time migration of its local hash). Once the
    /// account is protected (a code, or the Windows or code mode, review N1) it needs proof: the Windows unlock for a couch code in the Windows
    /// mode, otherwise a parent's or an administrator's approval (the kinds come from the policy), or an elevated administrator.
    /// </summary>
    internal AuthorizationDecision EnrollAllowed(CallerInfo caller, ValidatedRequest request)
    {
        if (request.Args is not PasscodeHashArgs args || KindsOf(args.Kind) is not { } kinds) return AuthorizationDecision.Denied("no code kind");
        if (!parts.Codes.IsAvailable) return AuthorizationDecision.Denied("codes not available");
        if (kinds.Any(k => parts.Codes.Has(caller.Sid, k))) return AuthorizationDecision.Denied("a code of that kind exists already");
        if (parts.Codes.View(caller.Sid).Protected && !caller.IsElevatedAdmin) return AuthorizationDecision.GrantNeeded;
        return AuthorizationDecision.Allowed;
    }

    internal AuthorizationDecision CodeExists(CallerInfo caller, ValidatedRequest request)
    {
        if (KindsOf(request) is not { } kinds) return AuthorizationDecision.Denied("no code kind");
        if (!parts.Codes.IsAvailable) return AuthorizationDecision.Denied("codes not available");
        return kinds.Any(k => parts.Codes.Has(caller.Sid, k)) ? AuthorizationDecision.Allowed : AuthorizationDecision.Denied("no code of that kind");
    }

    /// <summary>
    /// Raising the lock mode is free; lowering it needs proof (the kinds come from the policy by the current mode). A code may come along only
    /// when the code lock is switched on from no lock at all, and never over a code somebody approved.
    /// </summary>
    internal AuthorizationDecision LockModeChange(CallerInfo caller, ValidatedRequest request)
    {
        if (request.Args is not LockModeArgs { Mode: var mode } args || LockModes.Rank(mode) < 0) return AuthorizationDecision.Denied("no mode");
        if (!parts.Codes.IsAvailable) return AuthorizationDecision.Denied("codes not available");
        if (args.Hash is not null)
        {
            if (LockModes.Rank(parts.Codes.StoredMode(caller.Sid)) > 0) return AuthorizationDecision.Denied("a code comes along only from no lock");
            // Like EnrollAllowed: never next to any code somebody approved (couch or parents'), whatever kind comes along (review F1).
            if (KindsOf(args.Kind) is null || parts.Codes.HasApproved(caller.Sid, CodeKind.Couch) || parts.Codes.HasApproved(caller.Sid, CodeKind.Parent))
                return AuthorizationDecision.Denied("an approved code exists");
        }
        var current = parts.Codes.View(caller.Sid).Mode;
        if (LockModes.Rank(mode) >= LockModes.Rank(current) || caller.IsElevatedAdmin) return AuthorizationDecision.Allowed;
        return AuthorizationDecision.GrantNeeded;
    }

    /// <summary>
    /// The lock mode. From the code lock to the Windows lock the couch code goes in the same step (review M-a): in the Windows mode nothing but
    /// the Windows unlock (or a parent or an administrator) opens the couch.
    /// </summary>
    private byte[] SetMode(RequestContext context)
    {
        var args = context.Request.GetArgs<LockModeArgs>();
        var mode = args.Mode;
        var before = parts.Codes.View(context.Caller.Sid);
        var dropCouch = mode == LockModes.Windows && before.HasCouch;
        // A lock switched on from no lock: what was enrolled freely at the open couch goes, unless it comes along now (review 3, item 2).
        var clearOpen = LockModes.Rank(parts.Codes.StoredMode(context.Caller.Sid)) <= 0 && LockModes.Rank(mode) > 0;
        var kinds = args.Hash is null ? null : KindsOf(args.Kind);
        switch (parts.Codes.SetMode(context.Caller.Sid, mode, removeCouch: dropCouch, clearOpen: clearOpen, kinds: kinds, hash: args.Hash))
        {
            case CodeSet.Saved:
                break;
            case CodeSet.Exists:
                return Dispatcher.Fail(ErrorCodes.Denied);
            default:
                return Dispatcher.Fail(ErrorCodes.Unavailable);
        }
        if (before.Mode != mode || dropCouch || kinds is not null)
        {
            parts.Log.Info($"[codes] lock mode {before.Mode} -> {mode}{(dropCouch ? ", couch code removed" : "")}{(kinds is not null ? $", {args.Kind} code set" : "")} for {context.Caller.Describe()}{Approval(context)}");
            parts.Events?.Add(context.Caller.Sid, "lock", $"mode {mode}");
        }
        return ProtocolJson.Serialize(Response.Success());
    }

    /// <summary>Removes one of the caller's codes (the table and the state check made sure it exists and the right approval came along).</summary>
    private byte[] Remove(RequestContext context)
    {
        var sid = context.Caller.Sid;
        var args = context.Request.GetArgs<CodeKindArgs>();
        if (CodeStore.KindOf(args.Kind) is not { } kind) return Dispatcher.Fail(ErrorCodes.BadArgument);
        switch (parts.Codes.Remove(sid, kind))
        {
            case CodeSet.Saved:
                parts.Log.Info($"[codes] {args.Kind} code removed for {context.Caller.Describe()}{Approval(context)}");
                parts.Events?.Add(sid, "code", $"{args.Kind} code removed");
                return ProtocolJson.Serialize(Response.Success());
            case CodeSet.Missing:
                return Dispatcher.Fail(ErrorCodes.NotFound);
            default:
                return Dispatcher.Fail(ErrorCodes.Unavailable);
        }
    }

    /// <summary>
    /// Stores a code (couch, parents' or both). A code set with a parent's or an administrator's approval replaces whatever the other kind
    /// was, unless both are set in the same step (review M4): a code planted next to it does not survive the parents' reset.
    /// </summary>
    private byte[] Store(RequestContext context, bool enroll)
    {
        var sid = context.Caller.Sid;
        var args = context.Request.GetArgs<PasscodeHashArgs>();
        if (KindsOf(args.Kind) is not { } kinds) return Dispatcher.Fail(ErrorCodes.BadArgument);
        var approved = context.Grant is GrantKinds.Parent or GrantKinds.WindowsAdmin || context.Caller.IsElevatedAdmin;
        // Enrolled without any grant: the free first code at an open couch (it does not survive a lock being switched on, review 3).
        var open = enroll && context.Grant == GrantKinds.None && !context.Caller.IsElevatedAdmin;
        var result = parts.Codes.Set(sid, kinds, args.Hash, enroll, clearOther: approved && kinds.Count == 1, open: open);
        var what = $"{args.Kind} code {(enroll ? "enrolled" : "changed")}";
        switch (result)
        {
            case CodeSet.Saved:
                parts.Log.Info($"[codes] {what} for {context.Caller.Describe()}{Approval(context)}{(approved && kinds.Count == 1 ? ", the other kind cleared" : "")}");
                parts.Events?.Add(sid, "code", what);
                return ProtocolJson.Serialize(Response.Success());
            case CodeSet.Exists:
                return Dispatcher.Fail(ErrorCodes.Denied);
            case CodeSet.Missing:
                return Dispatcher.Fail(ErrorCodes.NotFound);
            case CodeSet.BadHash:
                return Dispatcher.Fail(ErrorCodes.BadArgument);
            default:
                return Dispatcher.Fail(ErrorCodes.Unavailable);
        }
    }

    /// <summary>
    /// The buttons against the account's code. A right couch code gives a couch grant (the 2-minute unlock); a right parents' code gives a
    /// parent's grant only for the operations the request names (each once, review M3), without any it only answers "matched".
    /// </summary>
    private byte[] Verify(RequestContext context)
    {
        var caller = context.Caller;
        var args = context.Request.GetArgs<VerifyCodeArgs>();
        if (CodeStore.KindOf(args.Kind) is not { } kind) return Dispatcher.Fail(ErrorCodes.BadArgument);
        var verdict = parts.Codes.Verify(caller.Sid, kind, args.Sequence);
        switch (verdict.Result)
        {
            case CodeCheck.Granted when kind == CodeKind.Parent && args.Ops is null:
                return Answer(new CodeCheckData(CodeCheckResults.Matched));
            case CodeCheck.Granted:
                var grantKind = kind == CodeKind.Parent ? GrantKinds.Parent : GrantKinds.Couch;
                if (parts.Grants.Issue(caller, grantKind, GrantStore.OpsOf(args.Ops)) is not { } issued) return Dispatcher.Fail(ErrorCodes.Denied);
                parts.Log.Info($"[grant] {GrantStore.NameOf(grantKind)} for {caller.Describe()} (code{(args.Ops is { } ops ? ", for " + string.Join('/', ops) : "")})");
                parts.Events?.Add(caller.Sid, "grant", GrantStore.NameOf(grantKind));
                return Answer(new CodeCheckData(CodeCheckResults.Granted, 0, issued.ToData()));
            case CodeCheck.Wrong:
                parts.Log.Info($"[codes] wrong {args.Kind} code for {caller.Describe()}");
                parts.Events?.Add(caller.Sid, "code", $"wrong {args.Kind} code");
                return Answer(new CodeCheckData(CodeCheckResults.Wrong, Seconds(verdict.Wait)));
            case CodeCheck.LockedOut:
                return Answer(new CodeCheckData(CodeCheckResults.LockedOut, Math.Max(1, Seconds(verdict.Wait))));
            case CodeCheck.NoCode:
                return Answer(new CodeCheckData(CodeCheckResults.NoCode));
            default:
                return Dispatcher.Fail(ErrorCodes.Unavailable);
        }
    }

    /// <summary>
    /// The caller's own session was unlocked a moment ago (seen by the service, see <see cref="SessionEvents"/>). Only for an account whose
    /// lock is the Windows sign-in (in the code mode the code is the lock, review H1), and never for an account without a password (anyone
    /// unlocks that with a click). Checked before the unlock is taken, so a refused request does not use it up.
    /// </summary>
    private byte[] UnlockGrant(CallerInfo caller)
    {
        if (parts.Codes.View(caller.Sid).Mode != LockModes.Windows) return Dispatcher.Fail(ErrorCodes.Denied);
        if (parts.IsBlankPassword(caller.Sid))
        {
            parts.Log.Error($"[grant] no Windows-unlock grant for {caller.Describe()}: the account has no password");
            return Dispatcher.Fail(ErrorCodes.Denied);
        }
        if (!parts.Sessions.TryTake(caller)) return Dispatcher.Fail(ErrorCodes.NotFound);
        if (parts.Grants.Issue(caller, GrantKinds.WindowsUnlock) is not { } issued) return Dispatcher.Fail(ErrorCodes.Denied);
        parts.Log.Info($"[grant] {GrantKindNames.WindowsUnlock} for {caller.Describe()}");
        parts.Events?.Add(caller.Sid, "grant", GrantKindNames.WindowsUnlock);
        return ProtocolJson.Serialize(Response<GrantData>.Success(issued.ToData()));
    }

    /// <summary>A pending administrator approval for the caller's session (see <see cref="AdminApprovals"/>): the control code for <c>sc.exe control</c>.</summary>
    private byte[] RequestApproval(RequestContext context)
    {
        var caller = context.Caller;
        if (parts.Approvals.IsOpen(caller.TokenSessionId)) return Dispatcher.Fail(ErrorCodes.Busy);
        var ops = GrantStore.OpsOf(context.Request.GetArgs<AdminApprovalArgs>().Ops) ?? [];
        if (parts.Approvals.Register(caller, ops) is not { } control) return Dispatcher.Fail(ErrorCodes.Busy);
        parts.Log.Info($"[grant] administrator approval {control} asked by {caller.Describe()} for {(ops.Count == 0 ? "a confirmation only" : string.Join('/', ops))}");
        return ProtocolJson.Serialize(Response<AdminApprovalData>.Success(new AdminApprovalData(control, (int)AdminApprovals.Lifetime.TotalSeconds)));
    }

    /// <summary>The caller's own approval, once an administrator gave it: a WindowsAdmin grant for exactly the operations asked for.</summary>
    private byte[] CollectApproval(RequestContext context)
    {
        var caller = context.Caller;
        var (state, ops) = parts.Approvals.Collect(caller);
        switch (state)
        {
            case AdminApprovals.State.Pending:
                return Answer(new AdminApprovalResult("pending"));
            case AdminApprovals.State.None:
                return Answer(new AdminApprovalResult("none"));
        }
        parts.Events?.Add(caller.Sid, "grant", "administrator approval");
        if (ops.Count == 0)
        {
            parts.Log.Info($"[grant] administrator confirmation for {caller.Describe()}");
            return Answer(new AdminApprovalResult("approved"));
        }
        if (parts.Grants.Issue(caller, GrantKinds.WindowsAdmin, ops) is not { } issued) return Dispatcher.Fail(ErrorCodes.Denied);
        parts.Log.Info($"[grant] {GrantKindNames.WindowsAdmin} for {caller.Describe()}, for {string.Join('/', ops)} (UAC)");
        return Answer(new AdminApprovalResult("approved", issued.ToData()));
    }

    private static string Approval(RequestContext context) =>
        context.Caller.IsElevatedAdmin ? " (elevated administrator)" : context.Grant == GrantKinds.None ? "" : $" ({GrantStore.NameOf(context.Grant)} grant)";

    private static byte[] Answer(CodeCheckData data) => ProtocolJson.Serialize(Response<CodeCheckData>.Success(data));

    private static byte[] Answer(AdminApprovalResult data) => ProtocolJson.Serialize(Response<AdminApprovalResult>.Success(data));

    private static int Seconds(TimeSpan wait) => wait <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(wait.TotalSeconds);
}
