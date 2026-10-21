using CouchHud.Core.Guard;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public sealed class LockPolicyTests
{
    [Fact]
    public void LockedCouchStartsGamesOfEverySource() =>
        Assert.True(LockPolicy.MayLaunchLocked(ItemType.Game));

    [Fact]
    public void ALockRefusalIsItsOwnKindOfError()
    {
        // The bridge logs it as one line (no stack trace); everything that catches InvalidOperationException still sees it.
        InvalidOperationException error = new LockedException("Gesperrt");
        Assert.IsType<LockedException>(error);
        Assert.Equal("Gesperrt", error.Message);
    }

    [Fact]
    public void AppsAndWebsitesNeedTheUnlock() =>
        Assert.False(LockPolicy.MayLaunchLocked(ItemType.App));

    [Fact]
    public void ReleasedClientFamilyEndsWithTheRunningGame()
    {
        var start = new ForegroundContext(true, GameRunning: false, MouseMode: false, PcMode: false, Browsing: false, LockPrompt: false,
            Released: ["steam"]);
        // Start phase: Steam may show a login or an update.
        Assert.False(ForegroundPolicy.Decide(start, "steam", "Steam", () => false).Suppress);
        // The game runs and the couch is locked: strict, the release no longer counts.
        var running = start with { GameRunning = true, StrictClients = true };
        Assert.True(ForegroundPolicy.Decide(running, "steam", "Steam", () => false).Suppress);
    }
}
