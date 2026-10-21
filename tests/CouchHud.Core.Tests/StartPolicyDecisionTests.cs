using CouchHud.Core.Guard;

namespace CouchHud.Core.Tests;

public class StartPolicyDecisionTests
{
    [Theory]
    [InlineData(true, false, true, false, false, false)]
    [InlineData(true, false, true, false, true, false)]
    [InlineData(false, false, true, false, false, true)]
    [InlineData(true, true, true, false, false, true)]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(true, false, true, true, false, true)]
    public void Releases_only_when_the_start_does_not_lock(bool locked, bool dev, bool lockOn, bool pcMode, bool hold, bool expected) =>
        Assert.Equal(expected, StartPolicyDecision.ReleaseAtStart(locked, dev, lockOn, pcMode, hold));
}

public class ForegroundRulesTests
{
    [Theory]
    [InlineData(0, 10, false)]
    [InlineData(10, 10, false)]
    [InlineData(42, 10, true)]
    public void Only_a_foreign_window_counts(int foreground, int own, bool expected) =>
        Assert.Equal(expected, ForegroundRules.ForeignInFront(foreground, own));
}
