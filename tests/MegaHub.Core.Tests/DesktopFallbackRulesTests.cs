using MegaHub.Core.Security;

namespace MegaHub.Core.Tests;

public class DesktopFallbackRulesTests
{
    [Fact]
    public async Task Without_a_required_lock_the_desktop_opens_and_nothing_is_asked()
    {
        var tried = 0;
        var asked = 0;
        var enter = await RecoveryRules.LockBeforeDesktopAsync(false, () => { tried++; return Task.FromResult(false); }, () => { asked++; return Task.FromResult(true); });
        Assert.True(enter);
        Assert.Equal(0, tried);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task A_lock_that_works_opens_the_desktop_without_asking()
    {
        var asked = 0;
        var enter = await RecoveryRules.LockBeforeDesktopAsync(true, () => Task.FromResult(true), () => { asked++; return Task.FromResult(true); });
        Assert.True(enter);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task A_lock_that_fails_keeps_the_desktop_closed_when_the_person_stops()
    {
        var tried = 0;
        var enter = await RecoveryRules.LockBeforeDesktopAsync(true, () => { tried++; return Task.FromResult(false); }, () => Task.FromResult(false));
        Assert.False(enter);
        Assert.Equal(1, tried);
    }

    [Fact]
    public async Task It_tries_again_until_the_lock_works_and_only_then_opens_the_desktop()
    {
        var tried = 0;
        var asked = 0;
        var enter = await RecoveryRules.LockBeforeDesktopAsync(true, () => Task.FromResult(++tried == 3), () => { asked++; return Task.FromResult(true); });
        Assert.True(enter);
        Assert.Equal(3, tried);
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task Stopping_after_some_failed_tries_never_opens_the_desktop()
    {
        var tried = 0;
        var enter = await RecoveryRules.LockBeforeDesktopAsync(true, () => { tried++; return Task.FromResult(false); }, () => Task.FromResult(tried < 2));
        Assert.False(enter);
        Assert.Equal(2, tried);
    }
}
