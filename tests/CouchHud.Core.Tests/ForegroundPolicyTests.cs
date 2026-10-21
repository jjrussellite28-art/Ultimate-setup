using CouchHud.Core.Guard;

namespace CouchHud.Core.Tests;

public sealed class ForegroundPolicyTests
{
    private static readonly ForegroundContext Couch = new(Enabled: true, GameRunning: false, MouseMode: false, PcMode: false, Browsing: false, LockPrompt: false);

    [Theory]
    [InlineData("steam", "steam")]
    [InlineData("steamwebhelper.exe", "steam")]
    [InlineData("EpicGamesLauncher", "epic")]
    [InlineData("EpicWebHelper", "epic")]
    [InlineData("EADesktop", "ea")]
    [InlineData("upc", "ubisoft")]
    [InlineData("GalaxyClient Helper", "gog")]
    [InlineData("Battle.net", "battlenet")]
    [InlineData("XboxPcApp", "xbox")]
    [InlineData("Playnite.FullscreenApp", "playnite")]
    [InlineData("STEAMWEBHELPER", "steam")]
    [InlineData("OneDrive", "nuisance")]
    public void ClassifiesClientsAndNuisances(string process, string family) =>
        Assert.Equal(family, ForegroundPolicy.Classify(process));

    [Theory]
    [InlineData("notepad")]
    [InlineData("MyUpdaterTool")]
    [InlineData("EALauncher")]
    [InlineData("UbisoftGameLauncher")]
    [InlineData("chrome")]
    [InlineData("CouchHud")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherProgramsAreNotListed(string? process) => Assert.Null(ForegroundPolicy.Classify(process));

    [Fact]
    public void ClientInFrontOnTheCouchIsMinimized()
    {
        var d = ForegroundPolicy.Decide(Couch, "steamwebhelper", "Steam", startedByCouchHud: () => false);
        Assert.Equal(ForegroundAction.Minimize, d.Action);
        Assert.Equal(ForegroundReason.Suppressed, d.Reason);
    }

    [Theory]
    [InlineData("Steam Big Picture Mode")]
    [InlineData("Steam Big Picture-Modus")]
    public void BigPictureIsClosedAndMinimized(string title) =>
        Assert.Equal(ForegroundAction.CloseAndMinimize, ForegroundPolicy.Decide(Couch, "steamwebhelper", title, () => false).Action);

    [Fact]
    public void BigPictureTitleOfAnotherProgramDoesNotCount() =>
        Assert.Equal(ForegroundAction.Allow, ForegroundPolicy.Decide(Couch, "notepad", "Big Picture", () => false).Action);

    public static IEnumerable<object[]> Exceptions()
    {
        yield return [Couch with { Enabled = false }, ForegroundReason.Disabled];
        yield return [Couch with { GameRunning = true }, ForegroundReason.GameRunning];
        yield return [Couch with { MouseMode = true }, ForegroundReason.MouseMode];
        yield return [Couch with { PcMode = true }, ForegroundReason.PcMode];
        yield return [Couch with { Browsing = true }, ForegroundReason.Browsing];
        yield return [Couch with { LockPrompt = true }, ForegroundReason.LockPrompt];
    }

    [Theory]
    [MemberData(nameof(Exceptions))]
    public void ExceptionsLeaveClientsAlone(ForegroundContext ctx, ForegroundReason reason)
    {
        var d = ForegroundPolicy.Decide(ctx, "EpicGamesLauncher", "Epic Games Launcher", startedByCouchHud: () => false);
        Assert.Equal(ForegroundAction.Allow, d.Action);
        Assert.Equal(reason, d.Reason);
    }

    [Fact]
    public void ProgramsCouchHudStartedAreAllowed()
    {
        var d = ForegroundPolicy.Decide(Couch, "steam", "Steam", startedByCouchHud: () => true);
        Assert.Equal(ForegroundReason.StartedByCouchHud, d.Reason);
        Assert.False(d.Suppress);
    }

    [Fact]
    public void OwnWindowsAreNeverTouched() =>
        Assert.Equal(ForegroundReason.OwnWindow, ForegroundPolicy.Decide(Couch, "steam", "x", () => false, ownProcess: true).Reason);

    [Fact]
    public void NuisanceIsSuppressedLikeAClientButOnlyOnTheCouch()
    {
        Assert.True(ForegroundPolicy.Decide(Couch, "GoogleUpdate", "Update", () => false).Suppress);
        Assert.False(ForegroundPolicy.Decide(Couch with { GameRunning = true }, "GoogleUpdate", "Update", () => false).Suppress);
    }

    [Fact]
    public void ThrottleAllowsOneActionPerKeyAndSecond()
    {
        var t = new ActionThrottle(TimeSpan.FromSeconds(1));
        var now = DateTimeOffset.UnixEpoch;
        Assert.True(t.TryAcquire(1, now));
        Assert.False(t.TryAcquire(1, now.AddMilliseconds(900)));
        Assert.True(t.TryAcquire(2, now.AddMilliseconds(900)));
        Assert.True(t.TryAcquire(1, now.AddMilliseconds(1000)));
    }
}

public sealed class GameBarGuardTests
{
    private const string Key = GameBarGuard.Key;

    [Theory]
    [InlineData(true, false, true, true, true)]
    [InlineData(false, false, true, true, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, false, true, false, false)]
    public void AppliesOnlyOnTheCouchInFullMode(bool full, bool pc, bool suppress, bool tap, bool expected) =>
        Assert.Equal(expected, GameBarGuard.ShouldApply(full, pc, suppress, tap));

    [Fact]
    public void MissingValueIsDeletedAgainOnRestore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "couchhud-gb-" + Guid.NewGuid().ToString("N"));
        try
        {
            var reg = new FakeRegistry();
            var guard = new GuardSnapshot(reg, new FileVdfStore(), Path.Combine(dir, "guard.json"));
            Assert.Empty(guard.Apply([GameBarGuard.Change]));
            Assert.Equal(0, reg.ReadDword(Key, GameBarGuard.Value));
            Assert.Empty(guard.Restore());
            Assert.Null(reg.ReadDword(Key, GameBarGuard.Value));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OriginalOneComesBackAfterCrashRecovery()
    {
        var dir = Path.Combine(Path.GetTempPath(), "couchhud-gb-" + Guid.NewGuid().ToString("N"));
        try
        {
            var reg = new FakeRegistry();
            reg.WriteDword(Key, GameBarGuard.Value, 1);
            var state = Path.Combine(dir, "guard.json");
            new GuardSnapshot(reg, new FileVdfStore(), state).Apply([GameBarGuard.Change]);
            // New process after a crash.
            Assert.Empty(new GuardSnapshot(reg, new FileVdfStore(), state).RestoreLeftovers());
            Assert.Equal(1, reg.ReadDword(Key, GameBarGuard.Value));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Parental_controls_keep_clients_down_while_a_game_runs()
    {
        var running = new ForegroundContext(Enabled: true, GameRunning: true, MouseMode: false, PcMode: false, Browsing: false, LockPrompt: false);
        Assert.Equal(ForegroundAction.Allow, ForegroundPolicy.Decide(running, "steamwebhelper", "Steam", () => false).Action);
        var strict = running with { StrictClients = true, Released = ["steam"] };
        Assert.Equal(ForegroundAction.Minimize, ForegroundPolicy.Decide(strict, "steamwebhelper", "Steam", () => true).Action);
        // The game itself (not a listed client) is never touched.
        Assert.Equal(ForegroundAction.Allow, ForegroundPolicy.Decide(strict, "eldenring", "Elden Ring", () => false).Action);
    }
}
