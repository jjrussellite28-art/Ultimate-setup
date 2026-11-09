using MegaHub.Core.Guard;

namespace MegaHub.Core.Tests;

/// <summary>The sleep timer's warning over a game (Hud.SleepTimerWarning): reachable on a locked couch like the timer itself, the host's own texts.</summary>
public class SleepTimerWarningTests
{
    [Fact]
    public void The_warning_works_on_a_locked_couch_like_the_power_entry()
    {
        Assert.Contains("power", LockPolicy.OpenWhileLocked);
        Assert.Contains("sleepTimerWarning", LockPolicy.OpenWhileLocked);
    }

    [Theory]
    [InlineData("shutdown")]
    [InlineData("standby")]
    public void Both_actions_have_a_notice_with_the_seconds(string action)
    {
        var text = Localization.Strings.Get($"power.timerWarn.{action}", 60);
        Assert.Contains("60", text);
        Assert.NotEqual($"power.timerWarn.{action}", text);
        Assert.NotEqual("power.timerWarnTitle", Localization.Strings.Get("power.timerWarnTitle"));
    }
}
