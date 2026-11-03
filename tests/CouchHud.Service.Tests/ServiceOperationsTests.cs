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
        ops.ConsoleSession = 2;
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
    public async Task The_watch_stops_a_held_service_again_that_started_during_the_pause_and_the_resume_starts_it()
    {
        using var ops = new OpsHarness();
        ops.Sessions.Sessions[1] = Callers.UserSid;
        ops.Scm.Running.Add("WSearch");
        // A service (PcaSvc in the VM, VM test 0.7 B, F3) that was not running at the pause: it was held, nothing stopped.
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch", "SysMain"]));
        Assert.False(ops.State.Paused["SysMain"].StoppedByService);
        Assert.Equal(0, ops.Operations.RecheckPausedServices(CancellationToken.None));
        // Delayed start / trigger: both come back.
        ops.Scm.Running.UnionWith(["WSearch", "SysMain"]);
        Assert.Equal(2, ops.Operations.RecheckPausedServices(CancellationToken.None));
        Assert.DoesNotContain("WSearch", ops.Scm.Running);
        Assert.DoesNotContain("SysMain", ops.Scm.Running);
        Assert.True(ops.State.Paused["SysMain"].StoppedByService);
        // Nothing running: nothing to do, no start types touched.
        Assert.Equal(0, ops.Operations.RecheckPausedServices(CancellationToken.None));
        Assert.True((await ops.Call(A, Operation.ResumeServices, new ServicesArgs(["WSearch", "SysMain"]))).Ok);
        Assert.Contains("SysMain", ops.Scm.Running);
        Assert.Contains("WSearch", ops.Scm.Running);
    }

    [Fact]
    public async Task The_watch_touches_nothing_without_a_pause_and_nothing_off_the_list()
    {
        using var ops = new OpsHarness();
        ops.Sessions.Sessions[1] = Callers.UserSid;
        ops.Scm.Running.UnionWith(["WSearch", "SysMain"]);
        Assert.Equal(0, ops.Operations.RecheckPausedServices(CancellationToken.None));
        Assert.Empty(ops.Scm.Calls);
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        await ops.Call(A, Operation.ResumeServices, new ServicesArgs(["WSearch"]));
        ops.Scm.Calls.Clear();
        ops.Scm.Running.Add("WSearch");
        // After the resume the hold is gone: the watch leaves it running.
        Assert.Equal(0, ops.Operations.RecheckPausedServices(CancellationToken.None));
        Assert.Empty(ops.Scm.Calls);
        Assert.Contains("SysMain", ops.Scm.Running);
    }

    [Fact]
    public async Task The_watch_keeps_trying_when_the_stop_fails_and_never_changes_start_types()
    {
        using var ops = new OpsHarness();
        ops.Sessions.Sessions[1] = Callers.UserSid;
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        ops.Scm.Running.Add("WSearch");
        ops.Scm.FailStop.Add("WSearch");
        Assert.Equal(0, ops.Operations.RecheckPausedServices(CancellationToken.None));
        Assert.Equal(0, ops.Operations.RecheckPausedServices(CancellationToken.None));
        Assert.Equal(2, ops.Scm.Calls.Count(c => c == "stop WSearch"));
        Assert.Empty(ops.Scm.StartTypes);
        ops.Scm.FailStop.Clear();
        Assert.Equal(1, ops.Operations.RecheckPausedServices(CancellationToken.None));
    }

    [Fact]
    public async Task The_watch_lets_go_of_a_hold_whose_account_has_no_session_and_starts_the_service_again()
    {
        using var ops = new OpsHarness();
        ops.Sessions.Sessions[1] = Callers.UserSid;
        ops.Scm.Running.Add("WSearch");
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        ops.Sessions.Sessions.Clear();
        ops.Scm.Calls.Clear();
        Assert.Equal(0, ops.Operations.RecheckPausedServices(CancellationToken.None));
        Assert.False(ops.State.Paused.ContainsKey("WSearch"));
        Assert.Contains("WSearch", ops.Scm.Running);
        Assert.DoesNotContain("stop WSearch", ops.Scm.Calls);
    }

    [Fact]
    public async Task The_watch_lets_go_when_no_couchhud_runs_any_more_even_if_the_account_is_signed_in()
    {
        using var ops = new OpsHarness();
        ops.Sessions.Sessions[1] = Callers.UserSid;
        ops.Scm.Running.Add("WSearch");
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        ops.ShellRuns = false;
        // The user starts it by hand afterwards: the watch must not stop it again.
        ops.Operations.RecheckPausedServices(CancellationToken.None);
        Assert.Empty(ops.State.Paused);
        Assert.Contains("WSearch", ops.Scm.Running);
        ops.Scm.Calls.Clear();
        Assert.Equal(0, ops.Operations.RecheckPausedServices(CancellationToken.None));
        Assert.Empty(ops.Scm.Calls);
    }

    [Fact]
    public async Task The_watch_keeps_the_remaining_holder_and_does_nothing_when_the_sessions_cannot_be_read()
    {
        using var ops = new OpsHarness();
        ops.Sessions.Sessions[1] = Callers.UserSid;
        ops.Sessions.Sessions[2] = Callers.OtherSid;
        ops.Scm.Running.Add("WSearch");
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        ops.ConsoleSession = 2;
        await ops.Call(B, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        ops.Sessions.Sessions.Remove(1);
        ops.Operations.RecheckPausedServices(CancellationToken.None);
        Assert.Equal([Callers.OtherSid], ops.State.Paused["WSearch"].Holders);
        Assert.DoesNotContain("WSearch", ops.Scm.Running);
        // Unreadable: no watching, no releasing, no starting.
        ops.Sessions.Unreadable = true;
        ops.Scm.Running.Add("WSearch");
        ops.Scm.Calls.Clear();
        Assert.Equal(0, ops.Operations.RecheckPausedServices(CancellationToken.None));
        Assert.Empty(ops.Scm.Calls);
        Assert.Contains(Callers.OtherSid, ops.State.Paused["WSearch"].Holders);
    }

    [Fact]
    public void The_watch_never_touches_an_entry_off_the_list_or_on_the_denylist()
    {
        using var ops = new OpsHarness();
        ops.Sessions.Sessions[1] = Callers.UserSid;
        ops.Scm.Running.UnionWith(["WinDefend", "NotOnTheList"]);
        ops.Store.Update(s =>
        {
            s.Paused["WinDefend"] = new CouchHud.Service.State.PausedService { Holders = [Callers.UserSid], StoppedByService = true };
            s.Paused["NotOnTheList"] = new CouchHud.Service.State.PausedService { Holders = [Callers.UserSid], StoppedByService = true };
            return true;
        });
        Assert.Equal(0, ops.Operations.RecheckPausedServices(CancellationToken.None));
        Assert.Empty(ops.Scm.Calls);
        Assert.Contains("WinDefend", ops.Scm.Running);
        Assert.Contains("NotOnTheList", ops.Scm.Running);
    }

    [Theory]
    [InlineData(ServiceStartMode.Disabled)]
    [InlineData(ServiceStartMode.Manual)]
    public async Task The_resume_never_starts_a_service_that_is_not_automatic_even_when_the_service_stopped_it(ServiceStartMode mode)
    {
        using var ops = new OpsHarness();
        ops.Scm.Running.Add("WSearch");
        ops.Scm.StartTypes["WSearch"] = mode;
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        Assert.True(ops.State.Paused["WSearch"].StoppedByService);
        Assert.True((await ops.Call(A, Operation.ResumeServices, new ServicesArgs(["WSearch"]))).Ok);
        Assert.DoesNotContain("start WSearch", ops.Scm.Calls);
        Assert.DoesNotContain("WSearch", ops.Scm.Running);
        Assert.Empty(ops.State.Paused);
    }

    [Fact]
    public async Task The_watch_loop_survives_a_failing_tick_logs_it_once_in_a_row_and_stops_on_cancel()
    {
        var log = new RecordingSink();
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var loop = PausedServiceWatch.RunAsync(_ =>
        {
            if (Interlocked.Increment(ref calls) <= 3) throw new InvalidOperationException("boom");
            return 0;
        }, TimeSpan.FromMilliseconds(10), log, stop.Token);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref calls) < 6 && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(Volatile.Read(ref calls) >= 6, "the loop kept ticking after the exceptions");
        stop.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(log.Errors);
        Assert.Contains("InvalidOperationException", log.Errors[0]);
        Assert.DoesNotContain("boom", log.Errors[0]);
    }

    [Fact]
    public async Task A_cancellation_that_is_not_the_stop_is_logged_and_the_loop_goes_on()
    {
        var log = new RecordingSink();
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var loop = PausedServiceWatch.RunAsync(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new OperationCanceledException();
            return 0;
        }, TimeSpan.FromMilliseconds(10), log, stop.Token);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref calls) < 4 && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(Volatile.Read(ref calls) >= 4);
        Assert.Single(log.Errors);
        stop.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class RecordingSink : CouchHud.Privileged.IEventSink
    {
        public List<string> Errors { get; } = [];
        public void Info(string message) { }
        public void Error(string message) => Errors.Add(message);
    }

    [Fact]
    public async Task A_signed_out_account_gives_its_holds_back()
    {
        using var ops = new OpsHarness();
        ops.Scm.Running.UnionWith(["WSearch", "SysMain"]);
        ops.Sessions.Sessions[1] = Callers.UserSid;
        ops.Sessions.Sessions[2] = Callers.OtherSid;
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch", "SysMain"]));
        ops.ConsoleSession = 2;
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
