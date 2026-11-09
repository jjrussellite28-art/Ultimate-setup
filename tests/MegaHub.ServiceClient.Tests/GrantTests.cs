using System.Text;
using MegaHub.Privileged;
using MegaHub.Service.Contracts;

namespace MegaHub.ServiceClient.Tests;

/// <summary>P9 on the shell's side: the grant cache, which grant goes where, the code calls and the needGrant retry.</summary>
public class GrantCacheTests
{
    private static string G(byte fill) => Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray());

    private static GrantData Bound(byte fill, string kind, params Operation[] ops) => new(G(fill), kind, 30, [.. ops.Select(o => o.ToString())]);

    [Fact]
    public void Keeps_only_well_formed_grants_and_drops_them_before_they_run_out()
    {
        var now = DateTimeOffset.UnixEpoch;
        var cache = new GrantCache(() => now);
        Assert.False(cache.Set(new GrantData("short", GrantKindNames.Couch, 120)));
        Assert.False(cache.Set(new GrantData(G(1), "admin", 120)));
        Assert.False(cache.Set(new GrantData(G(1), GrantKindNames.Couch, 0)));
        // An approval without operations, or an unlock grant with some, is no grant of the service.
        Assert.False(cache.Set(new GrantData(G(1), GrantKindNames.Parent, 30)));
        Assert.False(cache.Set(new GrantData(G(1), GrantKindNames.Couch, 120, ["SetAutoLogon"])));
        Assert.False(cache.Set(new GrantData(G(1), GrantKindNames.Parent, 30, ["Hello"])));
        Assert.True(cache.Set(new GrantData(G(1), GrantKindNames.Couch, 120)));
        Assert.Equal(G(1), cache.Of(GrantKind.Couch));
        now += TimeSpan.FromSeconds(120) - GrantCache.Margin;
        Assert.Null(cache.Of(GrantKind.Couch));
    }

    [Fact]
    public void An_approval_goes_only_with_its_operations_each_once()
    {
        var cache = new GrantCache();
        Assert.True(cache.Set(Bound(3, GrantKindNames.WindowsAdmin, Operation.ReleaseLockPolicies, Operation.ReleaseLockPolicies, Operation.CleanupPolicyKeys)));
        Assert.Null(cache.Approval(Operation.SetAutoLogon));
        Assert.Equal(G(3), cache.For(Operation.ReleaseLockPolicies));
        Assert.Equal(G(3), cache.For(Operation.ReleaseLockPolicies));
        Assert.Null(cache.Approval(Operation.ReleaseLockPolicies));
        Assert.Equal(G(3), cache.For(Operation.CleanupPolicyKeys));
        Assert.False(cache.Has(GrantKind.WindowsAdmin));
    }

    [Fact]
    public void The_parents_code_never_goes_with_the_couch_or_unlock_grant()
    {
        var cache = new GrantCache();
        cache.Set(new GrantData(G(1), GrantKindNames.Couch, 120));
        cache.Set(new GrantData(G(2), GrantKindNames.WindowsUnlock, 120));
        Assert.Null(cache.ForCode(Operation.ChangePasscode, CodeKinds.Parent));
        Assert.Null(cache.ForCode(Operation.ChangePasscode, CodeKinds.Both));
        Assert.Equal(G(1), cache.ForCode(Operation.ChangePasscode, CodeKinds.Couch));
        Assert.Equal(G(1), cache.For(Operation.ReleaseLockPolicies));
        cache.Set(Bound(4, GrantKindNames.Parent, Operation.ChangePasscode));
        Assert.Equal(G(4), cache.ForCode(Operation.ChangePasscode, CodeKinds.Both));
    }

    [Fact]
    public void The_unlock_drops_its_grants_on_lock_and_everything_goes_with_clear()
    {
        var cache = new GrantCache();
        cache.Set(new GrantData(G(1), GrantKindNames.Couch, 120));
        cache.Set(new GrantData(G(2), GrantKindNames.WindowsUnlock, 120));
        cache.Set(Bound(3, GrantKindNames.Parent, Operation.SetAutoLogon));
        cache.ClearUnlock();
        Assert.False(cache.Has(GrantKind.Couch));
        Assert.False(cache.Has(GrantKind.WindowsUnlock));
        Assert.True(cache.Has(GrantKind.Parent));
        cache.Clear();
        Assert.False(cache.Has(GrantKind.Parent));
    }

    [Fact]
    public void A_refused_approval_is_forgotten_the_unlock_grant_stays()
    {
        var cache = new GrantCache();
        cache.Set(new GrantData(G(1), GrantKindNames.Couch, 120));
        cache.Set(Bound(3, GrantKindNames.Parent, Operation.SetAutoLogon, Operation.SetLockMode));
        cache.Forget(G(3));
        cache.Forget(G(1));
        Assert.Null(cache.Approval(Operation.SetLockMode));
        Assert.Equal(G(1), cache.For(Operation.ReleaseLockPolicies));
    }

    [Theory]
    [InlineData(Operation.ApplyLockPolicies)]
    [InlineData(Operation.ResumeServices)]
    [InlineData(Operation.VerifyCode)]
    public void Operations_without_a_grant_get_none(Operation op)
    {
        var cache = new GrantCache();
        cache.Set(new GrantData(G(1), GrantKindNames.Couch, 120));
        Assert.Null(cache.For(op));
    }

    [Fact]
    public void Grants_stay_out_of_to_string() =>
        Assert.DoesNotContain(G(7), new GrantData(G(7), GrantKindNames.Parent, 30, ["SetAutoLogon"]).ToString());

    [Fact]
    public void The_approval_runs_sc_control_from_the_system_folder_with_the_services_code_only()
    {
        var launch = EmergencyRepair.AdminApproval(@"C:\Windows\System32", WindowsServiceControl.DefaultServiceName, 173);
        Assert.Equal(@"C:\Windows\System32\sc.exe", launch.FileName);
        Assert.Equal("control MegaHub 173", launch.Arguments);
        Assert.Equal(@"C:\Windows\System32", launch.WorkingDirectory);
        Assert.Throws<ArgumentOutOfRangeException>(() => EmergencyRepair.AdminApproval(@"C:\Windows\System32", "MegaHub", 127));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmergencyRepair.AdminApproval(@"C:\Windows\System32", "MegaHub", 256));
        Assert.Throws<ArgumentException>(() => EmergencyRepair.AdminApproval("System32", "MegaHub", 173));
        Assert.Throws<ArgumentException>(() => EmergencyRepair.AdminApproval(@"C:\Windows\System32", "MegaHub & calc", 173));
    }
}

public class GrantFlowTests
{
    [Fact]
    public async Task NeedGrant_asks_once_and_retries_once()
    {
        var calls = 0;
        var asked = 0;
        var result = await GrantFlow.WithGrantAsync(
            () => Task.FromResult(++calls == 1 ? new OpResult(FailureKind.NeedGrant, ErrorCodes.NeedGrant) : OpResult.Success),
            () => { asked++; return Task.FromResult(true); });
        Assert.True(result.Ok);
        Assert.Equal(2, calls);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task A_cancelled_prompt_keeps_the_refusal()
    {
        var calls = 0;
        var result = await GrantFlow.WithGrantAsync(
            () => { calls++; return Task.FromResult(new OpResult(FailureKind.NeedGrant, ErrorCodes.NeedGrant)); },
            () => Task.FromResult(false));
        Assert.Equal(FailureKind.NeedGrant, result.Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_long_operation_that_needs_an_approval_asks_once_and_starts_again()
    {
        var calls = 0;
        var asked = 0;
        var result = await GrantFlow.WithGrantJobAsync(
            () => Task.FromResult(++calls == 1 ? new JobResult(new OpResult(FailureKind.NeedGrant, ErrorCodes.NeedGrant), null) : new JobResult(OpResult.Success, null, true)),
            () => { asked++; return Task.FromResult(true); });
        Assert.True(result.Started);
        Assert.Equal(2, calls);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task A_long_operation_whose_approval_is_declined_keeps_the_refusal()
    {
        var calls = 0;
        var result = await GrantFlow.WithGrantJobAsync(
            () => { calls++; return Task.FromResult(new JobResult(new OpResult(FailureKind.NeedGrant, ErrorCodes.NeedGrant), null)); },
            () => Task.FromResult(false));
        Assert.Equal(FailureKind.NeedGrant, result.Result.Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Other_answers_never_ask()
    {
        var asked = false;
        var result = await GrantFlow.WithGrantAsync(() => Task.FromResult(new OpResult(FailureKind.Refused, ErrorCodes.Denied)), () => { asked = true; return Task.FromResult(true); });
        Assert.False(asked);
        Assert.Equal(FailureKind.Refused, result.Failure);
    }
}

public class UninstallReleaseTests
{
    private static Task<string> Run(OpResult release, params string?[] statuses)
    {
        var queue = new Queue<string?>(statuses);
        var now = DateTimeOffset.UnixEpoch;
        return UninstallRelease.RunAsync(() => Task.FromResult(release), () => Task.FromResult(queue.Count > 0 ? queue.Dequeue() : "running"),
            d => { now += d; return Task.CompletedTask; }, () => now, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Done_at_once() => Assert.Equal("done", await Run(OpResult.Success));

    [Fact]
    public async Task A_timed_out_request_is_followed_by_status_until_done() =>
        Assert.Equal("done", await Run(new OpResult(FailureKind.Timeout, ErrorCodes.Timeout), "running", "running", "done"));

    [Fact]
    public async Task Failed_is_reported() =>
        Assert.Equal("failed", await Run(new OpResult(FailureKind.Timeout, ErrorCodes.Timeout), "running", "failed"));

    [Fact]
    public async Task The_wait_is_capped() =>
        Assert.Equal("timeout", await Run(new OpResult(FailureKind.Timeout, ErrorCodes.Timeout)));

    [Fact]
    public async Task No_service_is_said_so()
    {
        Assert.Equal("noService", await Run(new OpResult(FailureKind.NotInstalled, "notInstalled")));
        Assert.Equal("noService", await Run(OpResult.NoService));
    }

    [Fact]
    public async Task A_service_that_is_there_but_not_reached_is_not_called_missing()
    {
        // VM test 0.5: the cleanup started from a network logon (PowerShell Direct) logged "noService" for a running service whose pipe
        // refuses network callers on purpose. The log names the real reason now.
        Assert.Equal("notRunning", await Run(new OpResult(FailureKind.NotRunning, "notRunning")));
        Assert.Equal("unreachable", await Run(new OpResult(FailureKind.Unreachable, "unreachable")));
    }

    [Fact]
    public async Task A_refusal_or_a_squatter_is_no_release() =>
        Assert.Equal("refused", await Run(new OpResult(FailureKind.Squatted, "squatted")));
}

public class ServiceNamesTests
{
    [Fact]
    public void The_clients_copies_match_the_installers_names()
    {
        Assert.Equal(ServiceInstall.ServiceName, WindowsServiceControl.DefaultServiceName);
        Assert.Equal(ServiceInstall.RepairScript, EmergencyRepair.RepairScriptName);
    }
}

/// <summary>The code and grant operations over a fake pipe server.</summary>
public class CodeRouterTests
{
    private static readonly string Grant = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());

    private static ServiceOps Ops(FakePipeServer server)
    {
        var scm = FakeScm.RunningHere();
        var client = new ServiceClient(scm, server.Options());
        return new ServiceOps(client, new ServiceProbe(scm, client, new Version(0, 4, 1), (_, _) => Task.CompletedTask), null, (_, _) => Task.CompletedTask);
    }

    private static readonly string[] Seq = ["a", "b", "x", "y", "up", "down"];

    [Fact]
    public async Task Windows_update_is_never_sent_the_unlocks_grant_only_an_approval()
    {
        await using var server = new FakePipeServer(_ => throw new InvalidOperationException());
        var ops = Ops(server);
        Assert.True(ops.Cache.Set(new GrantData(Grant, GrantKindNames.Couch, 120)));
        // The unlock's grant goes to the Mega Hub update (it keeps the session), not to Windows Update.
        Assert.Null(await ops.Grants!(Operation.InstallWindowsUpdates, default));
        Assert.Equal(Grant, await ops.Grants!(Operation.InstallMegaHubUpdate, default));
        var approval = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
        Assert.True(ops.Cache.Set(new GrantData(approval, GrantKindNames.Parent, 30, ["InstallWindowsUpdates"])));
        Assert.Equal(approval, await ops.Grants!(Operation.InstallWindowsUpdates, default));
        Assert.Null(await ops.Grants!(Operation.InstallWindowsUpdates, default));
    }

    [Fact]
    public async Task Pause_services_is_never_sent_the_unlocks_grant_only_an_approval()
    {
        await using var server = new FakePipeServer(_ => throw new InvalidOperationException());
        var ops = Ops(server);
        Assert.True(ops.Cache.Set(new GrantData(Grant, GrantKindNames.Couch, 120)));
        // The service answers ApprovalOnly to the unlock's grant: it must not go on the wire at all.
        Assert.Null(await ops.Grants!(Operation.PauseServices, default));
        Assert.Equal(Grant, await ops.Grants!(Operation.InstallMegaHubUpdate, default));
        var approval = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());
        Assert.True(ops.Cache.Set(new GrantData(approval, GrantKindNames.Parent, 30, ["PauseServices"])));
        Assert.Equal(approval, await ops.Grants!(Operation.PauseServices, default));
        Assert.Null(await ops.Grants!(Operation.PauseServices, default));
    }

    [Fact]
    public async Task A_right_couch_code_puts_its_grant_into_the_cache_and_release_carries_it()
    {
        await using var server = new FakePipeServer(request => FakePipeServer.OpOf(request) switch
        {
            nameof(Operation.VerifyCode) => FakePipeServer.Ok(new CodeCheckData(CodeCheckResults.Granted, 0, new GrantData(Grant, GrantKindNames.Couch, 120))),
            _ => FakePipeServer.Ok()
        });
        var ops = Ops(server);
        var check = await ops.VerifyCodeAsync(CodeKinds.Couch, Seq);
        Assert.True(check.Granted);
        Assert.Equal(GrantKind.Couch, check.Kind);
        await ops.ReleaseLockPoliciesAsync(["lock"]);
        var validator = new RequestValidator(_ => true);
        Assert.Equal(Grant, validator.Validate(server.Request(1)).Request!.Grant);
    }

    [Fact]
    public async Task A_parents_code_without_operations_is_only_checked()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok(new CodeCheckData(CodeCheckResults.Matched)));
        var ops = Ops(server);
        var check = await ops.VerifyCodeAsync(CodeKinds.Parent, Seq);
        Assert.True(check.Right);
        Assert.False(check.Granted);
        Assert.Null(new RequestValidator(_ => true).Validate(server.Request(0)).Request!.GetArgs<VerifyCodeArgs>().Ops);
    }

    [Fact]
    public async Task A_parents_grant_names_its_operations_and_goes_only_with_them()
    {
        await using var server = new FakePipeServer(request => FakePipeServer.OpOf(request) switch
        {
            nameof(Operation.VerifyCode) => FakePipeServer.Ok(new CodeCheckData(CodeCheckResults.Granted, 0, new GrantData(Grant, GrantKindNames.Parent, 30, ["SetAutoLogon"]))),
            _ => FakePipeServer.Ok()
        });
        var ops = Ops(server);
        Assert.True((await ops.VerifyCodeAsync(CodeKinds.Parent, Seq, [Operation.SetAutoLogon])).Granted);
        await ops.ReleaseLockPoliciesAsync(["lock"]);
        await ops.SetAutoLogonAsync("pw", false);
        var validator = new RequestValidator(_ => true);
        Assert.Equal(["SetAutoLogon"], validator.Validate(server.Request(0)).Request!.GetArgs<VerifyCodeArgs>().Ops);
        Assert.Equal(Grant, validator.Validate(server.Request(2)).Request!.Grant);
    }

    [Fact]
    public async Task A_grant_of_the_wrong_kind_or_shape_from_the_service_is_a_contradiction()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok(new CodeCheckData(CodeCheckResults.Granted, 0, new GrantData(Grant, GrantKindNames.Parent, 30, ["SetAutoLogon"]))));
        var ops = Ops(server);
        var check = await ops.VerifyCodeAsync(CodeKinds.Couch, Seq);
        Assert.Equal(FailureKind.Inconsistent, check.Result.Failure);
        // A grant where only a check was asked for.
        Assert.Equal(FailureKind.Inconsistent, (await ops.VerifyCodeAsync(CodeKinds.Parent, Seq)).Result.Failure);
        Assert.False(ops.Cache.Has(GrantKind.Parent));
    }

    [Fact]
    public async Task Wrong_and_wait_come_through()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok(new CodeCheckData(CodeCheckResults.LockedOut, 42)));
        var check = await Ops(server).VerifyCodeAsync(CodeKinds.Parent, Seq);
        Assert.False(check.Right);
        Assert.Equal(CodeCheckResults.LockedOut, check.Outcome);
        Assert.Equal(42, check.WaitSeconds);
    }

    [Fact]
    public async Task An_approval_is_asked_for_named_operations_and_its_grant_picked_up()
    {
        await using var server = new FakePipeServer(request => FakePipeServer.OpOf(request) switch
        {
            nameof(Operation.RequestAdminApproval) => FakePipeServer.Ok(new AdminApprovalData(173, 60)),
            _ => FakePipeServer.Ok(new AdminApprovalResult("approved", new GrantData(Grant, GrantKindNames.WindowsAdmin, 30, ["SetAutoLogon"])))
        });
        var ops = Ops(server);
        var (asked, control) = await ops.RequestAdminApprovalAsync([Operation.SetAutoLogon]);
        Assert.True(asked.Ok);
        Assert.Equal(173, control);
        Assert.Equal(["SetAutoLogon"], new RequestValidator(_ => true).Validate(server.Request(0)).Request!.GetArgs<AdminApprovalArgs>().Ops);
        var (collected, state) = await ops.CollectAdminApprovalAsync();
        Assert.True(collected.Ok);
        Assert.Equal("approved", state);
        Assert.Equal(Grant, ops.Cache.For(Operation.SetAutoLogon));
    }

    [Theory]
    [InlineData(127)]
    [InlineData(256)]
    [InlineData(0)]
    public async Task A_control_code_out_of_the_user_defined_range_is_a_contradiction(int control)
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok(new AdminApprovalData(control, 60)));
        var (result, code) = await Ops(server).RequestAdminApprovalAsync([]);
        Assert.Equal(FailureKind.Inconsistent, result.Failure);
        Assert.Null(code);
    }

    [Fact]
    public async Task A_grant_with_a_pending_state_or_of_another_kind_is_a_contradiction()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok(new AdminApprovalResult("pending", new GrantData(Grant, GrantKindNames.WindowsAdmin, 30, ["SetAutoLogon"]))));
        var ops = Ops(server);
        Assert.Equal(FailureKind.Inconsistent, (await ops.CollectAdminApprovalAsync()).Result.Failure);
        Assert.False(ops.Cache.Has(GrantKind.WindowsAdmin));
    }

    [Fact]
    public async Task A_squatted_pipe_gets_no_request()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok(new AdminApprovalData(173, 60)));
        var scm = new FakeScm(new ScmInfo(ScmState.Running, Environment.ProcessId + 7));
        var client = new ServiceClient(scm, server.Options());
        var ops = new ServiceOps(client, new ServiceProbe(scm, client, new Version(0, 4, 1), (_, _) => Task.CompletedTask), null, (_, _) => Task.CompletedTask);
        var (result, control) = await ops.RequestAdminApprovalAsync([Operation.SetAutoLogon]);
        Assert.Equal(FailureKind.Squatted, result.Failure);
        Assert.Null(control);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task Changing_the_parents_code_sends_only_an_approval()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok());
        var ops = Ops(server);
        ops.Cache.Set(new GrantData(Grant, GrantKindNames.Couch, 120));
        await ops.ChangePasscodeAsync(Hash, CodeKinds.Both);
        await ops.ChangePasscodeAsync(Hash, CodeKinds.Couch);
        await ops.RemovePasscodeAsync(CodeKinds.Parent);
        var validator = new RequestValidator(_ => true);
        Assert.Null(validator.Validate(server.Request(0)).Request!.Grant);
        Assert.Equal(Grant, validator.Validate(server.Request(1)).Request!.Grant);
        Assert.Null(validator.Validate(server.Request(2)).Request!.Grant);
    }

    [Fact]
    public async Task The_update_carries_the_unlocks_grant()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Fail(ErrorCodes.Denied));
        var ops = Ops(server);
        ops.Cache.Set(new GrantData(Grant, GrantKindNames.Couch, 120));
        var header = new SetupStreamArgs("MegaHub-Setup-0.4.2.exe", 10, "AA==", "AA==");
        await ops.InstallMegaHubUpdateAsync(header, new MemoryStream(new byte[10]), null);
        var validator = new RequestValidator(_ => true);
        Assert.Equal(Grant, validator.Validate(server.Request(0)).Request!.Grant);
    }

    [Fact]
    public async Task Status_says_which_codes_the_service_holds()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok(new StatusData("0.4.1", false, "none", 5, "running", true, false, LockModes.Code)));
        var ops = Ops(server);
        Assert.Equal("running", await ops.ReleaseStatusAsync());
        Assert.Equal(new HeldCodes(true, false, LockModes.Code), await ops.CodeStatusAsync());
    }

    private const string Hash = "pbkdf2-sha256$100000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
}

/// <summary>The shell's follow-up of the service-state reset: after a timeout the policies are set again only once the job has ended.</summary>
public class ResetFollowUpTests
{
    private static async Task<(string Outcome, List<string> Log)> Run(string outcome, TimeSpan cap, params string?[] statuses)
    {
        var queue = new Queue<string?>(statuses);
        var log = new List<string>();
        var now = DateTimeOffset.UnixEpoch;
        var result = await ResetFollowUp.RunAsync(outcome,
            () => { log.Add("status"); return Task.FromResult(queue.Count > 0 ? queue.Dequeue() : "running"); },
            d => { now += d; return Task.CompletedTask; }, () => now, () => log.Add("reapply"), cap);
        return (result, log);
    }

    [Fact]
    public async Task After_a_timeout_the_job_is_polled_and_the_policies_are_set_again_only_after_its_end()
    {
        var (outcome, log) = await Run("timeout", TimeSpan.FromSeconds(30), "running", "running", "done");
        Assert.Equal("done", outcome);
        Assert.Equal(["status", "status", "status", "reapply"], log);
    }

    [Fact]
    public async Task A_job_still_running_at_the_cap_stays_a_timeout_never_a_success()
    {
        var (outcome, log) = await Run("timeout", TimeSpan.FromSeconds(6));
        Assert.Equal("timeout", outcome);
        Assert.Equal("reapply", log[^1]);
        Assert.DoesNotContain("done", outcome);
    }

    [Fact]
    public async Task A_running_job_at_the_cap_is_not_reapplied_when_a_slower_poll_follows()
    {
        var reapplied = 0;
        var now = DateTimeOffset.UnixEpoch;
        var result = await ResetFollowUp.RunAsync("timeout", () => Task.FromResult<string?>("running"), d => { now += d; return Task.CompletedTask; },
            () => now, () => reapplied++, TimeSpan.FromSeconds(6), reapplyIfRunning: false);
        Assert.Equal("timeout", result);
        Assert.Equal(0, reapplied);
    }

    [Fact]
    public async Task The_slow_poll_sets_again_when_the_job_ends_late()
    {
        var reapplied = 0;
        var now = DateTimeOffset.UnixEpoch;
        var queue = new Queue<string?>(["running", "running", "done"]);
        var result = await ResetFollowUp.RunAsync("timeout", () => Task.FromResult(queue.Dequeue()), d => { now += d; return Task.CompletedTask; },
            () => now, () => reapplied++, TimeSpan.FromMinutes(12), ResetFollowUp.SlowPoll);
        Assert.Equal("done", result);
        Assert.Equal(1, reapplied);
        Assert.Equal(DateTimeOffset.UnixEpoch + 2 * ResetFollowUp.SlowPoll, now);
    }

    [Fact]
    public async Task A_late_failure_is_reported_as_failed()
    {
        var (outcome, _) = await Run("timeout", TimeSpan.FromSeconds(30), "running", "failed");
        Assert.Equal("failed", outcome);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("failed")]
    [InlineData("refused")]
    public async Task Other_outcomes_are_not_polled_but_the_policies_are_set_again(string given)
    {
        var (outcome, log) = await Run(given, TimeSpan.FromSeconds(30));
        Assert.Equal(given, outcome);
        Assert.Equal(["reapply"], log);
    }

    [Fact]
    public async Task The_set_again_runs_even_when_the_status_call_throws()
    {
        var reapplied = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ResetFollowUp.RunAsync("timeout",
            () => throw new InvalidOperationException("pipe"), _ => Task.CompletedTask, () => DateTimeOffset.UnixEpoch, () => reapplied = true, TimeSpan.FromSeconds(30)));
        Assert.True(reapplied);
    }

    [Fact]
    public async Task The_second_look_waits_ten_seconds_and_then_sets_again()
    {
        TimeSpan? waited = null;
        var reapplied = 0;
        await ResetFollowUp.VerifyAsync(d => { waited = d; return Task.CompletedTask; }, () => reapplied++);
        Assert.Equal(TimeSpan.FromSeconds(10), waited);
        Assert.Equal(1, reapplied);
    }
}
