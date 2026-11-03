using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Dispatch;

namespace CouchHud.Service.Tests;

public class PauseWatchFollowUpTests
{
    private static readonly CallerInfo A = Callers.User();

    [Theory]
    [InlineData(@"C:\Program Files\CouchHud\CouchHud.exe", @"C:\Program Files\CouchHud", true)]
    [InlineData(@"c:\program files\couchhud\COUCHHUD.EXE", @"C:\Program Files\CouchHud", true)]
    [InlineData(@"C:\Program Files\CouchHud\CouchHud.exe", @"C:\Program Files\CouchHud\", true)]
    [InlineData(@"C:\Program Files\CouchHud\CouchHud.exe", @"C:\Program Files\CouchHud/", true)]
    [InlineData(@"C:\Program Files\CouchHud\.\CouchHud.exe", @"C:\Program Files\CouchHud", true)]
    [InlineData(@"C:\Users\kid\Downloads\CouchHud.exe", @"C:\Program Files\CouchHud", false)]
    [InlineData(@"C:\Program Files\CouchHud\sub\CouchHud.exe", @"C:\Program Files\CouchHud", false)]
    [InlineData(@"C:\Program Files\CouchHud2\CouchHud.exe", @"C:\Program Files\CouchHud", false)]
    [InlineData(@"C:\Program Files\CouchHud\CouchHud.Service.exe", @"C:\Program Files\CouchHud", false)]
    [InlineData(@"C:\Program Files\CouchHud\Other.exe", @"C:\Program Files\CouchHud", false)]
    [InlineData(null, @"C:\Program Files\CouchHud", false)]
    [InlineData("", @"C:\Program Files\CouchHud", false)]
    [InlineData(@"C:\Program Files\CouchHud\CouchHud.exe", "", false)]
    [InlineData("bad\0path", @"C:\Program Files\CouchHud", false)]
    public void Only_the_same_full_path_is_the_shell(string? image, string appDir, bool expected) =>
        Assert.Equal(expected, OperationsAWiring.IsShellImage(image, appDir));

    [Fact]
    public async Task A_start_that_fails_on_the_watch_keeps_the_hold_and_is_tried_again_without_a_new_stop()
    {
        using var ops = new OpsHarness();
        ops.Sessions.Sessions[1] = Callers.UserSid;
        ops.Scm.Running.Add("WSearch");
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        ops.ShellRuns = false;
        ops.Scm.FailStart.Add("WSearch");
        ops.Scm.Calls.Clear();

        ops.Operations.RecheckPausedServices(CancellationToken.None);
        Assert.True(ops.State.Paused.ContainsKey("WSearch"));
        Assert.True(ops.State.Paused["WSearch"].StoppedByService);
        // Again: only the start is tried, never a stop (no flip-flop), even if the service happened to run in between.
        ops.Scm.Running.Add("WSearch");
        ops.Operations.RecheckPausedServices(CancellationToken.None);
        Assert.DoesNotContain("stop WSearch", ops.Scm.Calls);
        Assert.Contains("WSearch", ops.Scm.Running);

        ops.Scm.Running.Remove("WSearch");
        ops.Scm.FailStart.Clear();
        ops.Operations.RecheckPausedServices(CancellationToken.None);
        Assert.Contains("WSearch", ops.Scm.Running);
        Assert.Empty(ops.State.Paused);
        Assert.DoesNotContain("stop WSearch", ops.Scm.Calls);
    }

    [Fact]
    public async Task A_start_that_fails_after_a_logoff_still_drops_the_hold_and_says_so()
    {
        using var ops = new OpsHarness();
        ops.Sessions.Sessions[1] = Callers.UserSid;
        ops.Scm.Running.Add("WSearch");
        await ops.Call(A, Operation.PauseServices, new ServicesArgs(["WSearch"]));
        ops.Scm.FailStart.Add("WSearch");
        ops.Operations.OnSessionLogoff(1, CancellationToken.None);
        Assert.Empty(ops.State.Paused);
        Assert.Contains(ops.OpsLog.Lines, e => e.StartsWith("E ") && e.Contains("WSearch") && e.Contains("hold is dropped"));
    }
}
