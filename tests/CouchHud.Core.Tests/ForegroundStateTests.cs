using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public sealed class ForegroundStateTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly ForegroundContext Couch = new(true, false, false, false, false, false);

    [Fact]
    public void ReleasedFamilyMayComeToTheFrontUntilTheUserIsBack()
    {
        var state = new ForegroundState();
        state.Release("steam", T0);
        var ctx = Couch with { Released = state.Released(T0.AddMinutes(1)) };
        Assert.Equal(ForegroundReason.FamilyReleased, ForegroundPolicy.Decide(ctx, "steamwebhelper", "Steam", () => false).Reason);
        // Another family is not released.
        Assert.True(ForegroundPolicy.Decide(ctx, "EpicGamesLauncher", "Epic", () => false).Suppress);
        state.ReleaseAll();
        Assert.Empty(state.Released(T0.AddMinutes(1)));
    }

    [Fact]
    public void ReleaseExpiresAfterTenMinutes()
    {
        var state = new ForegroundState();
        state.Release("epic", T0);
        Assert.Contains("epic", state.Released(T0.AddMinutes(9)));
        Assert.Empty(state.Released(T0.AddMinutes(10)));
    }

    [Fact]
    public void SuspendedGuardLeavesEverythingAlone() =>
        Assert.Equal(ForegroundReason.Suspended, ForegroundPolicy.Decide(Couch with { Suspended = true }, "steam", "Steam", () => false).Reason);

    [Fact]
    public void StartedByCouchHudIsNotEvaluatedForUnlistedProcesses()
    {
        var called = false;
        ForegroundPolicy.Decide(Couch, "notepad", "x", () => called = true);
        Assert.False(called);
    }

    [Fact]
    public void GivesUpAfterTwoAttemptsInTenSecondsAndStaysQuiet()
    {
        var state = new ForegroundState();
        Assert.Equal(BackoffAction.Act, state.Attempt("steam", T0));
        Assert.Equal(BackoffAction.Act, state.Attempt("steam", T0.AddSeconds(3)));
        Assert.Equal(BackoffAction.GiveUp, state.Attempt("steam", T0.AddSeconds(6)));
        Assert.Equal(BackoffAction.Quiet, state.Attempt("steam", T0.AddSeconds(7)));
        Assert.Equal(BackoffAction.Act, state.Attempt("steam", T0.AddMinutes(6)));
    }

    [Fact]
    public void SlowRepeatsDoNotCountAsFighting()
    {
        var state = new ForegroundState();
        Assert.Equal(BackoffAction.Act, state.Attempt("steam", T0));
        Assert.Equal(BackoffAction.Act, state.Attempt("steam", T0.AddSeconds(11)));
        Assert.Equal(BackoffAction.Act, state.Attempt("steam", T0.AddSeconds(22)));
    }

    [Fact]
    public void FailedMinimizeGivesUpAtOnceAndOnlyNotifiesOnce()
    {
        var state = new ForegroundState();
        Assert.Equal(BackoffAction.GiveUp, state.Failed("steam", T0));
        Assert.Equal(BackoffAction.Quiet, state.Failed("steam", T0.AddSeconds(1)));
    }

    [Theory]
    [InlineData("steam", LaunchKind.Url, "steam://rungameid/1", "steam")]
    [InlineData("custom", LaunchKind.Url, "com.epicgames.launcher://apps/x?action=launch", "epic")]
    [InlineData("custom", LaunchKind.Url, "uplay://launch/1/0", "ubisoft")]
    [InlineData("custom", LaunchKind.Url, "shell:AppsFolder\\Microsoft.Xbox.Foo_8wekyb!App", "xbox")]
    [InlineData("custom", LaunchKind.Exe, "C:\\Program Files (x86)\\Steam\\steam.exe", "steam")]
    [InlineData("custom", LaunchKind.Exe, "C:\\Games\\game.exe", null)]
    [InlineData("custom", LaunchKind.Url, "https://www.youtube.com/tv", null)]
    [InlineData("gog", LaunchKind.Exe, "C:\\x.exe", "gog")]
    public void LaunchMapsToClientFamily(string source, LaunchKind kind, string target, string? family) =>
        Assert.Equal(family, ForegroundPolicy.FamilyOfLaunch(source, kind, target));

    [Fact]
    public void BigPictureIsClosedOnlyForExactTitles()
    {
        Assert.True(ForegroundPolicy.IsBigPicture("steam", "Steam Big Picture Mode"));
        Assert.False(ForegroundPolicy.IsBigPicture("steam", "Big Picture Tipps - Steam Community"));
    }

    [Fact]
    public void PendingLaunchCountsUntilStartGraceIsOver()
    {
        var tracker = new GameSessionTracker(new EmptyProbe());
        Assert.False(tracker.IsPending(T0));
        tracker.Launched(new LibraryItem { Id = "a", Source = "steam", Name = "a", Launch = new LaunchTarget(LaunchKind.Url, "steam://rungameid/1") }, T0);
        Assert.True(tracker.IsPending(T0.AddMinutes(2)));
        Assert.False(tracker.IsPending(T0.AddMinutes(4)));
    }

    [Theory]
    [InlineData(false, false, true, true, true, true)]
    [InlineData(true, false, true, true, true, false)]
    [InlineData(false, true, true, true, true, false)]
    [InlineData(false, false, false, true, true, false)]
    [InlineData(false, false, true, false, true, false)]
    [InlineData(false, false, true, true, false, false)]
    public void GameBarMigrationRunsOnceForOldBlindChanges(bool migrated, bool pending, bool zero, bool used, bool tap, bool expected) =>
        Assert.Equal(expected, GameBarGuard.NeedsMigration(migrated, pending, zero, used, tap));

    private sealed class EmptyProbe : IProcessProbe
    {
        public IReadOnlyList<(int Pid, string? Path)> Snapshot() => [];
        public int SteamRunningAppId() => 0;
    }
}
