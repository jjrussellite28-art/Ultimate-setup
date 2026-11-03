using System.Text;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Tests;

/// <summary>Audit L-1 and L-2: a standard account acts on the whole machine only from the console, or alone on the machine, or with an approval.</summary>
public class SessionPolicyTests
{
    private static ValidatedRequest Req(Operation op, object? args = null) => new(op, null, args);

    private static readonly ValidatedRequest Pause = Req(Operation.PauseServices, new ServicesArgs(["GamingServices"]));
    private static readonly ValidatedRequest WindowsUpdate = Req(Operation.InstallWindowsUpdates, new InstallWindowsUpdatesArgs());

    private static StateChecks Checks(SessionFacts? sessions) => StateChecks.BuiltIn(n => n == "GamingServices", AssumeCodeEnrolled.Instance, sessions);

    private static SessionFacts Facts(int? console, params string[] others) => new(() => console, _ => others.ToHashSet(StringComparer.OrdinalIgnoreCase));

    // ---- L-1 -------------------------------------------------------------------------------------

    [Fact]
    public void The_console_session_pauses_without_an_approval() =>
        Assert.Equal(Verdict.Allow, Authorizer.Authorize(Callers.User(), Pause, false, Checks(Facts(console: 1))).Verdict);

    [Fact]
    public void A_session_that_is_not_the_console_needs_an_approval_and_only_that_kind()
    {
        var decision = Authorizer.Authorize(Callers.User(), Pause, false, Checks(Facts(console: 2)));
        Assert.Equal(Verdict.NeedGrant, decision.Verdict);
        Assert.True(decision.ApprovalOnly);
    }

    [Fact]
    public void An_elevated_administrator_pauses_from_any_session() =>
        Assert.Equal(Verdict.Allow, Authorizer.Authorize(Callers.ElevatedAdmin() with { PipeSessionId = 5, TokenSessionId = 5 }, Pause, false, Checks(Facts(console: 2))).Verdict);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void Without_a_known_console_session_it_fails_closed(int? console) =>
        Assert.Equal(Verdict.NeedGrant, Authorizer.Authorize(Callers.User(), Pause, false, Checks(Facts(console))).Verdict);

    [Fact]
    public void Without_any_session_facts_it_fails_closed() =>
        Assert.Equal(Verdict.NeedGrant, Authorizer.Authorize(Callers.User(), Pause, false, Checks(null)).Verdict);

    [Fact]
    public void A_console_function_that_throws_fails_closed()
    {
        var facts = new SessionFacts(() => throw new InvalidOperationException(), _ => new HashSet<string>());
        Assert.Equal(Verdict.NeedGrant, Authorizer.Authorize(Callers.User(), Pause, false, Checks(facts)).Verdict);
    }

    [Fact]
    public void Resuming_what_the_account_paused_needs_no_console()
    {
        // Resume only undoes the caller's own hold (towards safer): not restricted to the console.
        Assert.DoesNotContain(StateCheck.ConsoleSessionOrApproval, OperationPolicy.Table[Operation.ResumeServices].Checks);
        Assert.Contains(StateCheck.ConsoleSessionOrApproval, OperationPolicy.Table[Operation.PauseServices].Checks);
    }

    // ---- L-2 -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Operation.InstallWindowsUpdates)]
    [InlineData(Operation.InstallCouchHudUpdate)]
    public void Both_installations_carry_the_session_check(Operation op) =>
        Assert.Contains(StateCheck.OtherSessionsNeedApproval, OperationPolicy.Table[op].Checks);

    [Fact]
    public void Alone_on_the_machine_the_update_needs_no_approval() =>
        Assert.Equal(Verdict.Allow, Authorizer.Authorize(Callers.User(), WindowsUpdate, false, Checks(Facts(console: 1))).Verdict);

    [Fact]
    public void The_same_account_in_another_session_is_not_another_account() =>
        Assert.Equal(Verdict.Allow, Authorizer.Authorize(Callers.User(), WindowsUpdate, false, Checks(Facts(console: 1, Callers.UserSid.ToLowerInvariant()))).Verdict);

    [Fact]
    public void Another_account_signed_in_needs_an_approval_and_only_that_kind()
    {
        var decision = Authorizer.Authorize(Callers.User(), WindowsUpdate, false, Checks(Facts(console: 1, Callers.OtherSid)));
        Assert.Equal(Verdict.NeedGrant, decision.Verdict);
        Assert.True(decision.ApprovalOnly);
    }

    [Fact]
    public void An_elevated_administrator_updates_whoever_is_signed_in() =>
        Assert.Equal(Verdict.Allow, Authorizer.Authorize(Callers.ElevatedAdmin(), WindowsUpdate, false, Checks(Facts(console: 1, Callers.OtherSid))).Verdict);

    [Fact]
    public void Unreadable_sessions_fail_closed()
    {
        var unreadable = new SessionFacts(() => 1, _ => null);
        Assert.Equal(Verdict.NeedGrant, Authorizer.Authorize(Callers.User(), WindowsUpdate, false, Checks(unreadable)).Verdict);
        var throwing = new SessionFacts(() => 1, _ => throw new InvalidOperationException());
        Assert.Equal(Verdict.NeedGrant, Authorizer.Authorize(Callers.User(), WindowsUpdate, false, Checks(throwing)).Verdict);
        Assert.Equal(Verdict.NeedGrant, Authorizer.Authorize(Callers.User(), WindowsUpdate, false, Checks(null)).Verdict);
    }

    // ---- through the dispatcher: which grants count ----------------------------------------------

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    private const string PauseJson = """{"v":1,"op":"PauseServices","args":{"names":["GamingServices"]},"grant":"GRANT"}""";

    [Theory]
    [InlineData(GrantKinds.Couch, false)]
    [InlineData(GrantKinds.WindowsUnlock, false)]
    [InlineData(GrantKinds.Parent, true)]
    [InlineData(GrantKinds.WindowsAdmin, true)]
    public async Task Only_a_parents_or_an_administrators_grant_opens_the_pause_from_another_session(GrantKinds kind, bool accepted)
    {
        var grant = Convert.ToBase64String(new byte[32]);
        var grants = new FakeGrants(grant, kind);
        var harness = new Harness(grants: grants, onServiceList: n => n == "GamingServices", sessions: Facts(console: 2),
            extraHandlers: h => h[Operation.PauseServices] = Harness.Ok);

        var reply = await harness.OneAsync(Callers.User(), Json(PauseJson.Replace("GRANT", grant)));

        Assert.Equal(accepted, reply.Ok);
        if (!accepted) Assert.Equal(ErrorCodes.NeedGrant, reply.Code);
        Assert.Equal(GrantKinds.None, grants.LastAccepted & ~GrantKinds.ParentOrAdmin);
    }

    [Fact]
    public async Task From_another_session_without_a_grant_the_pause_answers_needGrant()
    {
        var harness = new Harness(onServiceList: n => n == "GamingServices", sessions: Facts(console: 2), extraHandlers: h => h[Operation.PauseServices] = Harness.Ok);
        var reply = await harness.OneAsync(Callers.User(), Json("""{"v":1,"op":"PauseServices","args":{"names":["GamingServices"]}}"""));
        Assert.Equal(ErrorCodes.NeedGrant, reply.Code);
    }

    [Fact]
    public async Task At_the_console_the_pause_runs_without_a_grant()
    {
        var harness = new Harness(onServiceList: n => n == "GamingServices", sessions: Facts(console: 1), extraHandlers: h => h[Operation.PauseServices] = Harness.Ok);
        var reply = await harness.OneAsync(Callers.User(), Json("""{"v":1,"op":"PauseServices","args":{"names":["GamingServices"]}}"""));
        Assert.True(reply.Ok);
    }

    [Fact]
    public async Task With_another_account_signed_in_the_windows_update_answers_needGrant()
    {
        var harness = new Harness(sessions: Facts(console: 1, Callers.OtherSid), extraHandlers: h => h[Operation.InstallWindowsUpdates] = Harness.Ok,
            extraChecks: c => c.Register(StateCheck.UpdateIdsFromSearch, Harness.AllowAll));
        var reply = await harness.OneAsync(Callers.User(), Json("""{"v":1,"op":"InstallWindowsUpdates","args":{}}"""));
        Assert.Equal(ErrorCodes.NeedGrant, reply.Code);
    }

    [Fact]
    public void The_approvals_can_be_named_for_both_operations()
    {
        Assert.Contains(nameof(Operation.PauseServices), RequestValidator.GrantableOperations);
        Assert.Contains(nameof(Operation.InstallWindowsUpdates), RequestValidator.GrantableOperations);
    }
}

public class KeepSessionApprovalTests
{
    [Theory]
    [InlineData(GrantKinds.Parent, true)]
    [InlineData(GrantKinds.WindowsAdmin, true)]
    [InlineData(GrantKinds.Couch, false)]
    [InlineData(GrantKinds.None, false)]
    public void An_approval_the_dispatcher_already_used_keeps_the_session_and_is_not_asked_twice(GrantKinds approved, bool keeps)
    {
        // A code is enrolled (the couch may be locked) and there is no grant in the request: only the approval can keep the session.
        var policy = new CouchHud.Service.Setup.KeepSessionPolicy(AssumeCodeEnrolled.Instance, NoGrants.Instance);
        var request = new ValidatedRequest(Operation.InstallCouchHudUpdate, null, null);
        Assert.Equal(keeps, policy.MayKeep(Callers.User(), request, approved));
    }
}
