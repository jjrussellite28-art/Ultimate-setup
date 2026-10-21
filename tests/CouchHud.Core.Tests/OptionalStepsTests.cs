using System.Runtime.InteropServices;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Tests;

public class OptionalStepsTests
{
    private const int ENotImpl = unchecked((int)0x80004001);

    [Fact]
    public void A_failing_step_is_logged_and_the_rest_still_runs()
    {
        var log = new List<string>();
        var steps = new OptionalSteps(log.Add);
        var ran = new List<string>();

        // What a start without explorer looks like: the taskbar COM object answers E_NOTIMPL, others are not there at all.
        steps.Run("taskbar", () => throw new COMException("E_NOTIMPL", ENotImpl));
        steps.Run("toasts", () => throw new NotImplementedException());
        steps.Run("tray", () => throw new InvalidCastException("no such interface"));
        steps.Run("audio", () => ran.Add("audio"));
        steps.Run("guard", () => ran.Add("guard"));

        Assert.Equal(["audio", "guard"], ran);
        Assert.Equal(["taskbar", "toasts", "tray"], steps.Failed);
        Assert.Equal(3, log.Count);
        Assert.Contains("taskbar", log[0]);
        Assert.Contains("COMException", log[0]);
    }

    [Fact]
    public void A_step_that_works_reports_true_and_leaves_no_trace()
    {
        var log = new List<string>();
        var steps = new OptionalSteps(log.Add);
        Assert.True(steps.Run("fine", () => { }));
        Assert.Empty(log);
        Assert.Empty(steps.Failed);
    }

    [Fact]
    public void A_step_with_a_result_falls_back_when_it_throws()
    {
        var steps = new OptionalSteps(_ => { });
        Assert.Equal(7, steps.Run("value", () => 7, -1));
        Assert.Equal(-1, steps.Run<int>("broken", () => throw new COMException("x", ENotImpl), -1));
        Assert.Equal(["broken"], steps.Failed);
    }
}
