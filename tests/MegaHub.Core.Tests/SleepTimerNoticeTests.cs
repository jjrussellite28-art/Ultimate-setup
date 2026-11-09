using MegaHub.Core.Guard;
using MegaHub.Core.Platform;

namespace MegaHub.Core.Tests;

/// <summary>SleepTimerNotice: the host's notice over a game only for the armed timer, close to its end, at most once in 45 s.</summary>
public class SleepTimerNoticeTests
{
    private DateTimeOffset _now = new(2026, 11, 2, 20, 0, 0, TimeSpan.Zero);

    private SleepTimerNotice Notice() => new(() => _now);

    [Fact]
    public void Without_an_armed_timer_nothing_shows()
    {
        var notice = Notice();
        Assert.False(notice.Decide("shutdown", 60, hudInFront: false).Show);
    }

    [Fact]
    public void The_armed_timer_shows_once_near_its_end_and_then_waits_45_seconds()
    {
        var notice = Notice();
        notice.Arm("shutdown", 600);
        // Far from the end: no notice.
        Assert.False(notice.Decide("shutdown", 60, false).Show);
        _now += TimeSpan.FromSeconds(540);
        Assert.Equal((true, 60), notice.Decide("shutdown", 60, false));
        // One start yields one notice only, however long the page keeps asking.
        _now += TimeSpan.FromSeconds(44);
        Assert.False(notice.Decide("shutdown", 60, false).Show);
        _now += TimeSpan.FromSeconds(2);
        Assert.False(notice.Decide("shutdown", 60, false).Show);
        // A new start arms one more, but a notice waits five minutes after the last.
        _now += SleepTimerNotice.NoticeGap;
        Assert.True(notice.Arm("shutdown", 60));
        _now += TimeSpan.FromSeconds(60);
        Assert.True(notice.Decide("shutdown", 60, false).Show);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(59)]
    [InlineData(-10)]
    public void A_timer_under_a_minute_arms_nothing(int seconds)
    {
        var notice = Notice();
        Assert.False(notice.Arm("shutdown", seconds));
        Assert.False(notice.Decide("shutdown", 60, false).Show);
    }

    [Fact]
    public void Arming_again_within_45_seconds_is_refused_and_a_cancel_clears_the_gap()
    {
        var notice = Notice();
        Assert.True(notice.Arm("shutdown", 600));
        _now += TimeSpan.FromSeconds(10);
        // The page cannot re-arm to a time that ends now.
        Assert.False(notice.Arm("shutdown", 60));
        _now += TimeSpan.FromSeconds(540);
        Assert.True(notice.Decide("shutdown", 60, false).Show);
        // A real restart (cancel, then start) is taken at once.
        notice.Arm(null, 0);
        Assert.True(notice.Arm("standby", 120));
        _now += TimeSpan.FromSeconds(44);
        Assert.False(notice.Arm("standby", 60));
        _now += TimeSpan.FromSeconds(2);
        Assert.True(notice.Arm("standby", 60));
    }

    [Fact]
    public void Looping_cancel_arm_warning_gives_a_notice_only_every_five_minutes()
    {
        var notice = Notice();
        var shown = 0;
        for (var i = 0; i < 20; i++)
        {
            notice.Arm(null, 0);
            notice.Arm("shutdown", 60);
            _now += TimeSpan.FromSeconds(50);
            if (notice.Decide("shutdown", 60, false).Show) shown++;
        }
        // 20 rounds of 50 s = 1000 s: at most one notice per 5 minutes (4 here), not one per round.
        Assert.InRange(shown, 1, 4);
    }

    [Fact]
    public void A_fresh_long_arm_cannot_notify_right_away()
    {
        var notice = Notice();
        Assert.True(notice.Arm("shutdown", 600));
        Assert.False(notice.Decide("shutdown", 60, false).Show);
        _now += TimeSpan.FromSeconds(600 - 91);
        Assert.False(notice.Decide("shutdown", 60, false).Show);
        _now += TimeSpan.FromSeconds(2);
        Assert.True(notice.Decide("shutdown", 60, false).Show);
    }

    [Fact]
    public void A_notice_never_comes_after_the_late_window_even_unused()
    {
        var notice = Notice();
        Assert.True(notice.Arm("shutdown", 60));
        _now += TimeSpan.FromSeconds(60) + SleepTimerNotice.Late + TimeSpan.FromSeconds(1);
        Assert.False(notice.Decide("shutdown", 60, false).Show);
    }

    [Fact]
    public void Another_action_the_HUD_in_front_or_a_timer_long_past_shows_nothing()
    {
        var notice = Notice();
        notice.Arm("standby", 60);
        Assert.False(notice.Decide("shutdown", 60, false).Show);
        Assert.False(notice.Decide("standby", 60, hudInFront: true).Show);
        _now += TimeSpan.FromSeconds(60) + SleepTimerNotice.Late + TimeSpan.FromSeconds(1);
        Assert.False(notice.Decide("standby", 60, false).Show);
    }

    [Fact]
    public void A_cancelled_timer_shows_nothing()
    {
        var notice = Notice();
        notice.Arm("shutdown", 60);
        notice.Arm(null, 0);
        Assert.False(notice.Decide("shutdown", 60, false).Show);
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(60, 60)]
    [InlineData(100_000, 600)]
    public void The_seconds_named_are_clamped(int seconds, int expected)
    {
        var notice = Notice();
        notice.Arm("shutdown", 60);
        Assert.Equal(expected, notice.Decide("shutdown", seconds, false).Seconds);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Shutdown")]
    public void Unknown_actions_are_refused(string? action)
    {
        var notice = Notice();
        Assert.Throws<ArgumentException>(() => notice.Decide(action, 60, false));
        if (action is not null) Assert.Throws<ArgumentException>(() => notice.Arm(action, 60));
    }

    [Fact]
    public void Arming_works_on_a_locked_couch_like_the_warning()
    {
        Assert.Contains("sleepTimerArm", LockPolicy.OpenWhileLocked);
        Assert.Contains("sleepTimerWarning", LockPolicy.OpenWhileLocked);
    }
}
