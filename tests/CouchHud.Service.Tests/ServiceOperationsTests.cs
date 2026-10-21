using System.ServiceProcess;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Tests;

/// <summary>PauseServices and ResumeServices: only the fixed list, the denylist wins, reference counting per account.</summary>
public class ServiceOperationsTests
{
    private static readonly CallerInfo A = Callers.User();
    private static readonly CallerInfo B = Callers.User(Callers.OtherSid) with { PipeSessionId = 2, TokenSessionId = 2 };

    [Fact]
    public async Task Pause_stops_a_running_service_and_resume_starts_it_again()
    {
        using var ops = new OpsHarness();
        ops.Scm.Running.Add("WSearch");
        var pause = await ops.Call<ServicesData>(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        Assert.True(pause.Ok, pause.Code);
        Assert.DoesNotContain("WSearch", ops.Scm.Running);
        Assert.True(ops.State.Paused["WSearch"].StoppedByService);

        var resume = await ops.Call<ServicesData>(A, Operation.ResumeServices, new ServicesArgs(["WSearch"]));
        Assert.True(resume.Ok, resume.Code);
        Assert.Contains("WSearch", ops.Scm.Running);
        Assert.Empty(ops.State.Paused);
        Assert.Equal(["stop WSearch", "start WSearch"], ops.Scm.Calls);
    }

    [Fact]
    public async Task The_service_stays_paused_while_any_account_holds_it()
    {
        using var ops = new OpsHarness();
        ops.Scm.Running.Add("SysMain");
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["SysMain"]));
        await ops.Call(B, Operation.PauseServices, new ServicesArgs(["SysMain"]));
        Assert.Equal(2, ops.State.Paused["SysMain"].Holders.Count);

        Assert.True((await ops.Call(A, Operation.ResumeServices, new ServicesArgs(["SysMain"]))).Ok);
        Assert.DoesNotContain("SysMain", ops.Scm.Running);
        Assert.Equal([Callers.OtherSid], ops.State.Paused["SysMain"].Holders);

        Assert.True((await ops.Call(B, Operation.ResumeServices, new ServicesArgs(["SysMain"]))).Ok);
        Assert.Contains("SysMain", ops.Scm.Running);
        Assert.Single(ops.Scm.Calls, c => c == "start SysMain");
    }

    [Fact]
    public async Task Pausing_twice_from_one_account_counts_once()
    {
        using var ops = new OpsHarness();
        ops.Scm.Running.Add("WSearch");
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        Assert.Single(ops.State.Paused["WSearch"].Holders);
        Assert.True((await ops.Call(A, Operation.ResumeServices, new ServicesArgs(["WSearch"]))).Ok);
        Assert.Contains("WSearch", ops.Scm.Running);
        // A second resume: nothing held any more.
        Assert.Equal(ErrorCodes.Denied, (await ops.Call(A, Operation.ResumeServices, new ServicesArgs(["WSearch"]))).Code);
        Assert.Single(ops.Scm.Calls, c => c == "start WSearch");
    }

    [Fact]
    public async Task Resume_by_an_account_that_did_not_pause_is_refused()
    {
        using var ops = new OpsHarness();
        ops.Scm.Running.Add("WSearch");
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        Assert.Equal(ErrorCodes.Denied, (await ops.Call(B, Operation.ResumeServices, new ServicesArgs(["WSearch"]))).Code);
        Assert.Equal(ErrorCodes.Denied, (await ops.Call(Callers.ElevatedAdmin() with { Sid = Callers.OtherSid }, Operation.ResumeServices, new ServicesArgs(["WSearch"]))).Code);
        Assert.DoesNotContain("WSearch", ops.Scm.Running);
    }

    [Fact]
    public async Task Only_what_the_service_stopped_and_what_starts_with_Windows_is_started_again()
    {
        using var ops = new OpsHarness();
        // Not running when paused: the service did not stop it.
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["Spooler"]));
        Assert.False(ops.State.Paused["Spooler"].StoppedByService);
        // Running, but started by hand normally.
        ops.Scm.Running.Add("SysMain");
        ops.Scm.StartTypes["SysMain"] = ServiceStartMode.Manual;
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["SysMain"]));

        Assert.True((await ops.Call(A, Operation.ResumeServices, new ServicesArgs(["Spooler", "SysMain"]))).Ok);
        Assert.DoesNotContain(ops.Scm.Calls, c => c.StartsWith("start", StringComparison.Ordinal));
        Assert.Empty(ops.State.Paused);
    }

    [Theory]
    [InlineData("WinDefend")]   // on the test's list, but denied
    [InlineData("wuauserv")]
    [InlineData("CouchHud")]
    [InlineData("Dnscache")]    // not on the list
    public async Task A_service_off_the_list_or_on_the_denylist_is_refused(string name)
    {
        using var ops = new OpsHarness();
        ops.Scm.Running.Add(name);
        var response = await ops.Call(Callers.ElevatedAdmin(), Operation.PauseServices, new ServicesArgs([name]));
        Assert.False(response.Ok);
        Assert.Empty(ops.Scm.Calls);
        Assert.Empty(ops.State.Paused);
    }

    [Fact]
    public async Task A_stop_that_fails_leaves_the_state_as_before()
    {
        using var ops = new OpsHarness();
        ops.Scm.Running.Add("WSearch");
        ops.Scm.FailStop.Add("WSearch");
        Assert.Equal(ErrorCodes.Failed, (await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]))).Code);
        Assert.Empty(ops.State.Paused);
    }

    [Fact]
    public async Task A_start_that_fails_keeps_the_hold_so_the_resume_can_be_tried_again()
    {
        using var ops = new OpsHarness();
        ops.Scm.Running.Add("WSearch");
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        ops.Scm.FailStart.Add("WSearch");
        Assert.Equal(ErrorCodes.Failed, (await ops.Call(A, Operation.ResumeServices, new ServicesArgs(["WSearch"]))).Code);
        Assert.Equal([Callers.UserSid], ops.State.Paused["WSearch"].Holders);
        ops.Scm.FailStart.Clear();
        Assert.True((await ops.Call(A, Operation.ResumeServices, new ServicesArgs(["WSearch"]))).Ok);
        Assert.Contains("WSearch", ops.Scm.Running);
    }

    [Fact]
    public async Task A_signed_out_account_gives_its_holds_back()
    {
        using var ops = new OpsHarness();
        ops.Scm.Running.UnionWith(["WSearch", "SysMain"]);
        ops.Sessions.Sessions[1] = Callers.UserSid;
        ops.Sessions.Sessions[2] = Callers.OtherSid;
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch", "SysMain"]));
        await ops.Call(B, Operation.PauseServices, new ServicesArgs(["SysMain"]));

        // A signs out of session 1: WSearch was only A's and starts again, SysMain stays paused for B.
        ops.Operations.OnSessionLogoff(1, CancellationToken.None);
        Assert.Contains("WSearch", ops.Scm.Running);
        Assert.DoesNotContain("SysMain", ops.Scm.Running);
        Assert.Equal([Callers.OtherSid], ops.State.Paused["SysMain"].Holders);
        Assert.False(ops.State.Paused.ContainsKey("WSearch"));
    }

    [Fact]
    public async Task An_account_with_another_session_keeps_its_holds_and_unreadable_sessions_change_nothing()
    {
        using var ops = new OpsHarness();
        ops.Scm.Running.Add("WSearch");
        ops.Sessions.Sessions[1] = Callers.UserSid;
        ops.Sessions.Sessions[3] = Callers.UserSid;
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        ops.Operations.OnSessionLogoff(1, CancellationToken.None);
        Assert.True(ops.State.Paused.ContainsKey("WSearch"));
        ops.Sessions.Unreadable = true;
        ops.Operations.OnSessionLogoff(3, CancellationToken.None);
        Assert.True(ops.State.Paused.ContainsKey("WSearch"));
        Assert.DoesNotContain("WSearch", ops.Scm.Running);
    }

    [Fact]
    public async Task Touch_keyboard_and_pen_input_is_never_paused_by_the_service()
    {
        using var ops = new OpsHarness();
        ops.Scm.Running.Add("TabletInputService");
        Assert.False(Authorization.ServicePolicy.IsAllowed("TabletInputService", _ => true));
        Assert.False((await ops.Call(A, Operation.PauseServices, new ServicesArgs(["TabletInputService"]))).Ok);
        Assert.Empty(ops.Scm.Calls);
        // The shell's own suggestion list keeps it.
        Assert.True(Core.Startup.GamingServices.IsAllowed("TabletInputService"));
    }

    [Fact]
    public async Task Pauses_survive_a_service_restart_but_not_a_machine_restart()
    {
        using var ops = new OpsHarness();
        ops.Scm.Running.Add("WSearch");
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        ops.Restart();
        Assert.True(ops.State.Paused.ContainsKey("WSearch"));
        ops.BootTime += (long)TimeSpan.FromHours(3).TotalMilliseconds;
        ops.Restart();
        Assert.Empty(ops.State.Paused);
    }
}
