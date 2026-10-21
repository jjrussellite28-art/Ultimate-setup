using CouchHud.Core.Library;

namespace CouchHud.Core.Tests;

/// <summary>Only the clients CouchHud started itself are closed again; a download or update is never cut off.</summary>
public sealed class ClientOwnershipTests
{
    [Fact]
    public void AClientCouchHudStartedIsClosedOnQuitAndInPcMode()
    {
        var own = new ClientOwnership();
        own.NoteStarted("steam");
        Assert.Equal(CloseDecision.Close, own.Decide("steam", running: true, downloading: false));
    }

    [Fact]
    public void AClientThatRanBeforeCouchHudIsNeverTouched()
    {
        var own = new ClientOwnership();
        own.NoteRunningAtStart(["steam"]);
        own.NoteStarted("steam"); // a "start" of something that already ran does not make it ours
        Assert.Equal(CloseDecision.NotOurs, own.Decide("steam", running: true, downloading: false));
        Assert.Empty(own.Started);
    }

    [Fact]
    public void AClientTheUserStartedLaterIsNotOurs()
    {
        var own = new ClientOwnership();
        Assert.Equal(CloseDecision.NotOurs, own.Decide("epic", running: true, downloading: false));
    }

    [Fact]
    public void ADownloadIsNotCutOff()
    {
        var own = new ClientOwnership();
        own.NoteStarted("steam");
        // Quit leaves it open, the PC mode leaves it open with a notice: the decision is the same, the caller words it.
        Assert.Equal(CloseDecision.Downloading, own.Decide("steam", running: true, downloading: true));
    }

    [Fact]
    public void ARunningGameKeepsTheClientOpen()
    {
        var own = new ClientOwnership();
        own.NoteStarted("steam");
        Assert.Equal(CloseDecision.GameRunning, own.Decide("steam", running: true, downloading: false, gameRunning: true));
    }

    [Theory]
    [InlineData("epic")]
    [InlineData("gog")]
    [InlineData("ubisoft")]
    [InlineData("ea")]
    [InlineData("battlenet")]
    public void ClientsWithoutADownloadCheckAreLeftOpen(string id)
    {
        var own = new ClientOwnership();
        own.NoteStarted(id);
        Assert.Equal(CloseDecision.Unsafe, own.Decide(id, running: true, downloading: false));
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheInstanceWeStartedStaysOursWhileItRuns()
    {
        var own = new ClientOwnership();
        own.NoteStarted("steam");
        own.Observe("steam", true, 100, false, T0);
        own.Observe("steam", true, 100, false, T0.AddMinutes(30));
        Assert.True(own.IsOurs("steam"));
    }

    [Fact]
    public void AUserWhoEndedAndStartedSteamAgainTakesItOverFromUs()
    {
        var own = new ClientOwnership();
        own.NoteStarted("steam");
        own.Observe("steam", true, 100, false, T0);
        own.Observe("steam", false, null, false, T0.AddSeconds(5));
        // Back after a while by the shell, not by a Steam process: the user's.
        own.Observe("steam", true, 200, false, T0.AddSeconds(20));
        Assert.False(own.IsOurs("steam"));
        Assert.Equal(CloseDecision.NotOurs, own.Decide("steam", running: true, downloading: false));
    }

    [Fact]
    public void AnUpdateRestartByTheClientItselfStaysOurs()
    {
        var own = new ClientOwnership();
        own.NoteStarted("steam");
        own.Observe("steam", true, 100, false, T0);
        own.Observe("steam", false, null, false, T0.AddSeconds(3));
        own.Observe("steam", true, 200, startedByClient: true, T0.AddSeconds(15));
        Assert.True(own.IsOurs("steam"));
        // And the new instance is the one that counts from now on.
        own.Observe("steam", true, 200, false, T0.AddMinutes(10));
        Assert.True(own.IsOurs("steam"));
    }

    [Fact]
    public void ARestartAfterALongGapIsTheUsers()
    {
        var own = new ClientOwnership();
        own.NoteStarted("steam");
        own.Observe("steam", true, 100, false, T0);
        own.Observe("steam", false, null, false, T0.AddSeconds(5));
        own.Observe("steam", true, 200, startedByClient: true, T0.AddMinutes(5)); // too late to be an update restart
        Assert.False(own.IsOurs("steam"));
    }

    [Fact]
    public void ADifferentProcessWithoutAGapSeenIsNotOurs()
    {
        var own = new ClientOwnership();
        own.NoteStarted("steam");
        own.Observe("steam", true, 100, false, T0);
        own.Observe("steam", true, 200, startedByClient: true, T0.AddSeconds(2)); // between two looks: not sure, so not ours
        Assert.False(own.IsOurs("steam"));
    }

    [Fact]
    public void AClientGoneForGoodIsForgottenAfterTheGrace()
    {
        var own = new ClientOwnership();
        own.NoteStarted("steam");
        own.Observe("steam", true, 100, false, T0);
        own.Observe("steam", false, null, false, T0.AddSeconds(1));
        Assert.True(own.IsOurs("steam"));
        own.Observe("steam", false, null, false, T0.AddSeconds(90));
        Assert.False(own.IsOurs("steam"));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)] // light mode
    [InlineData(true, true, false, false)]   // PC mode
    [InlineData(true, false, true, false)]   // dev run
    public void ClientsAreStartedOnlyInFullIntegrationOnTheCouch(bool full, bool pc, bool dev, bool expected) =>
        Assert.Equal(expected, Launchers.MayStartClients(full, pc, dev));

    [Fact]
    public void AClientThatIsGoneNeedsNothing()
    {
        var own = new ClientOwnership();
        own.NoteStarted("steam");
        Assert.Equal(CloseDecision.NotOurs, own.Decide("steam", running: false, downloading: false));
    }

    [Fact]
    public void AClientTheUserClosedAndCouchHudStartedAgainIsOursAgain()
    {
        var own = new ClientOwnership();
        own.NoteRunningAtStart(["steam"]);
        own.NoteEnded("steam"); // the user closed theirs
        own.NoteStarted("steam");
        Assert.Equal(CloseDecision.Close, own.Decide("steam", running: true, downloading: false));
    }

    [Fact]
    public void AfterClosingItIsNotOursAnyMore()
    {
        var own = new ClientOwnership();
        own.NoteStarted("gog");
        own.NoteEnded("gog");
        Assert.Equal(CloseDecision.NotOurs, own.Decide("gog", running: true, downloading: false));
    }

    [Fact]
    public void LightModeLetsGoOfEverything()
    {
        var own = new ClientOwnership();
        own.NoteRunningAtStart(["epic"]);
        own.NoteStarted("steam");
        own.Forget();
        Assert.Empty(own.Started);
        Assert.Equal(CloseDecision.NotOurs, own.Decide("steam", running: true, downloading: false));
    }

    [Fact]
    public void OnlyTheClientsOfCouchHudAreListedForClosing()
    {
        var own = new ClientOwnership();
        own.NoteRunningAtStart(["epic"]);
        own.NoteStarted("steam");
        own.NoteStarted("epic");
        own.NoteStarted("gog");
        Assert.Equal(["gog", "steam"], own.Started.Order().ToArray());
    }
}
