using CouchHud.Core.Guard;
using CouchHud.Core.Library;

namespace CouchHud.Core.Tests;

/// <summary>When Steam may be touched: quiet moments, no restart for a restore, verification, the user overruling us, quit.</summary>
public sealed class SteamGuardRunnerTests : IDisposable
{
    private const string Steam = @"C:\Steam";
    private const string Sample = "\"UserLocalConfigStore\"\r\n{\r\n\t\"system\"\r\n\t{\r\n\t\t\"OverlayLogCleanupDone\"\t\t\"1\"\r\n\t}\r\n}\r\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-steamrunner-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _reg = new();
    private readonly MemoryVdf _vdf = new();
    private readonly FakeSteam _steam = new();
    private readonly List<string> _log = [];
    private const string Command = "\"CouchHud.exe\" --release-steam-guard";

    public SteamGuardRunnerTests()
    {
        Directory.CreateDirectory(_dir);
        _vdf.Files[SteamGuard.LocalConfigPath(Steam, "100")] = Sample;
        _steam.UserIds = ["100"];
        _steam.Root = Steam;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Local => SteamGuard.LocalConfigPath(Steam, "100");

    private SteamGuardRunner Runner(IVdfStore? vdf = null) => new(_reg, vdf ?? _vdf, _steam, _dir, Command, _log.Add)
    {
        Settle = TimeSpan.FromSeconds(20),
        ShutdownWait = TimeSpan.FromSeconds(45),
        VerifyDelay = TimeSpan.FromSeconds(1)
    };

    private bool Applied => VdfPatch.Get(_vdf.Files[Local], "UserLocalConfigStore/Controller_CheckGuideButton") == "0";

    private bool RunEntry => _reg.ReadString(SteamGuardRunner.RunKey, SteamGuardRunner.RunValue) is not null;

    [Fact]
    public void WithSteamOffTheValuesGoInAndNothingIsStarted()
    {
        _steam.Running = false;
        Runner().Sync(want: true);
        Assert.True(Applied);
        Assert.True(RunEntry);
        Assert.Equal(0, _steam.Starts);
        Assert.Equal(0, _steam.ShutdownRequests);
    }

    [Fact]
    public void AQuietSteamIsEndedWrittenAndStartedQuietlyAgain()
    {
        var runner = Runner();
        runner.Sync(want: true);
        Assert.Equal(1, _steam.ShutdownRequests);
        Assert.True(Applied);
        Assert.Equal(1, _steam.Starts);
        Assert.True(_steam.Running);
        Assert.False(File.Exists(Path.Combine(_dir, SteamGuardRunner.MarkerFile)));
        Assert.False(runner.Deferred);
    }

    [Fact]
    public void AGameThatStartsWhileSteamSettlesStopsTheRestart()
    {
        _steam.Age = TimeSpan.FromSeconds(3);
        _steam.OnSleep = () => _steam.Game = true;
        var runner = Runner();
        runner.Sync(want: true);
        Assert.Equal(0, _steam.ShutdownRequests);
        Assert.False(Applied);
        Assert.True(runner.Deferred);
    }

    [Theory]
    [InlineData("game")]
    [InlineData("client")]
    [InlineData("foreground")]
    [InlineData("download")]
    [InlineData("login")]
    public void NothingHappensAtAnUnquietMoment(string what)
    {
        switch (what)
        {
            case "game": _steam.Game = true; break;
            case "client": _steam.Client = true; break;
            case "foreground": _steam.Foreground = true; break;
            case "download": _steam.Download = true; break;
            default: _steam.Login = true; break;
        }
        var runner = Runner();
        runner.Sync(want: true);
        Assert.Equal(0, _steam.ShutdownRequests);
        Assert.False(Applied);
        Assert.True(runner.Deferred);
        Assert.False(runner.Quiet());

        // The moment passes: the next pass does it.
        _steam.Game = _steam.Client = _steam.Foreground = _steam.Download = _steam.Login = false;
        _steam.Now += TimeSpan.FromMinutes(5);
        Assert.True(runner.RetryDue);
        runner.Sync(want: true);
        Assert.True(Applied);
    }

    [Fact]
    public void AGameThatStartsRightBeforeAskingIsSeenAgain()
    {
        // Quiet at the first look, busy after the settle wait.
        _steam.Age = TimeSpan.FromSeconds(1);
        var sleeps = 0;
        _steam.OnSleep = () =>
        {
            if (++sleeps == 5) _steam.Foreground = true;
        };
        Runner().Sync(want: true);
        Assert.Equal(0, _steam.ShutdownRequests);
    }

    [Fact]
    public void ASteamThatDoesNotEndIsLeftAloneAndStartedAgainWhenItEndsLate()
    {
        _steam.EndsOnShutdown = false;
        var runner = Runner();
        runner.Sync(want: true);
        Assert.Equal(1, _steam.ShutdownRequests);
        Assert.False(Applied);
        Assert.True(runner.Deferred);
        Assert.True(File.Exists(Path.Combine(_dir, SteamGuardRunner.MarkerFile)));

        // It ends by itself some time later (the -shutdown was only slow): whoever finds it off starts it quietly again.
        _steam.Running = false;
        runner.Sync(want: true);
        Assert.True(Applied);
        Assert.Equal(1, _steam.Starts);
        Assert.False(File.Exists(Path.Combine(_dir, SteamGuardRunner.MarkerFile)));
    }

    [Fact]
    public void ASteamTheUserStartedAgainClearsTheMark()
    {
        _steam.EndsOnShutdown = false;
        var runner = Runner();
        runner.Sync(want: true);
        Assert.True(File.Exists(Path.Combine(_dir, SteamGuardRunner.MarkerFile)));
        _steam.Running = true; // still (or again) running: nothing of ours to start
        runner.Sync(want: false);
        Assert.Equal(0, _steam.Starts);
        Assert.False(File.Exists(Path.Combine(_dir, SteamGuardRunner.MarkerFile)));
    }

    [Fact]
    public void ASteamCouchHudStartsAndEndsItselfNeedsNoRestartCycle()
    {
        // Before the own start: Steam is off, the settings go in, nothing is ended or started by the guard.
        _steam.Running = false;
        var runner = Runner();
        runner.Sync(want: true, allowCycle: false, quick: true);
        Assert.True(Applied);
        Assert.Equal(0, _steam.ShutdownRequests);
        Assert.Equal(0, _steam.Starts);

        // CouchHud starts Steam (not the guard), later asks it to end itself; the settings follow once it is gone.
        _steam.Running = true;
        runner.Sync(want: false, allowCycle: false, quick: true); // Steam still running: waits
        Assert.True(Applied);
        _steam.Running = false;
        runner.Sync(want: false, allowCycle: false, quick: true);
        Assert.False(Applied);
        Assert.False(RunEntry);
        Assert.Equal(0, _steam.ShutdownRequests);
        Assert.Equal(0, _steam.Starts);
        Assert.Equal(Sample, _vdf.Files[Local]);
    }

    [Fact]
    public void PuttingBackNeverRestartsSteam()
    {
        _steam.Running = false;
        var runner = Runner();
        runner.Sync(want: true);
        Assert.True(Applied);

        _steam.Running = true;
        runner.Sync(want: false);
        Assert.Equal(0, _steam.ShutdownRequests);
        Assert.True(Applied);          // still ours: it waits
        Assert.True(RunEntry);         // and the Run entry stays
        Assert.True(runner.Deferred);

        _steam.Running = false;        // Steam ends by itself
        runner.Sync(want: false);
        Assert.False(Applied);
        Assert.False(RunEntry);
        Assert.Equal(0, _steam.Starts);
        Assert.Equal(Sample, _vdf.Files[Local]);
    }

    [Fact]
    public void QuitWithSteamRunningLeavesStateAndRunEntry()
    {
        _steam.Running = false;
        var runner = Runner();
        runner.Sync(want: true);
        _steam.Running = true;
        var slept = _steam.Slept;

        runner.Sync(want: false, allowCycle: false, quick: true);
        Assert.True(Applied);
        Assert.True(RunEntry);
        Assert.True(runner.HasPending);
        Assert.Equal(slept, _steam.Slept); // nothing waited
        Assert.Equal(0, _steam.ShutdownRequests);
    }

    [Fact]
    public void QuitWithSteamOffPutsBackAtOnceWithoutWaiting()
    {
        _steam.Running = false;
        var runner = Runner();
        runner.Sync(want: true);
        var slept = _steam.Slept;

        runner.Sync(want: false, allowCycle: false, quick: true);
        Assert.False(Applied);
        Assert.False(RunEntry);
        Assert.Equal(slept, _steam.Slept);
    }

    [Fact]
    public void AFailingWriteIsNotReportedAsDoneAndBacksOff()
    {
        _steam.Running = false;
        var runner = Runner(new ThrowingVdf(_vdf));
        runner.Sync(want: true);
        Assert.False(Applied);
        Assert.True(runner.Deferred);
        Assert.False(runner.RetryDue);
        _steam.Now += TimeSpan.FromMinutes(2);
        Assert.True(runner.RetryDue);
    }

    [Fact]
    public void ATargetThatKeepsFailingIsGivenUpForTheSession()
    {
        _steam.Running = false;
        var runner = Runner(new ThrowingVdf(_vdf));
        runner.MaxFailures = 3;
        for (var i = 0; i < 3; i++) runner.Sync(want: true);
        Assert.False(runner.Deferred);
        // Even a working store is not tried again in this session ...
        var again = Runner();
        again.MaxFailures = 3;
        Assert.False(again.Deferred);
        runner.Sync(want: true);
        Assert.False(Applied);
        // ... until the mode changes.
        runner.Sync(want: false);
        var healthy = new SteamGuardRunner(_reg, _vdf, _steam, _dir, Command, _log.Add);
        healthy.Sync(want: true);
        Assert.True(Applied);
    }

    [Fact]
    public void AValueTheUserChangedBackIsRespectedAndNotForcedAgain()
    {
        _steam.Running = false;
        var runner = Runner();
        runner.Sync(want: true);
        Assert.True(Applied);

        // Steam is running now and the user switched the Guide option back on in Steam.
        _vdf.Files[Local] = VdfPatch.Set(_vdf.Files[Local], "UserLocalConfigStore/Controller_CheckGuideButton", "1");
        _steam.Running = true;
        runner.Sync(want: true);
        Assert.Equal(0, _steam.ShutdownRequests);
        Assert.Equal("1", VdfPatch.Get(_vdf.Files[Local], "UserLocalConfigStore/Controller_CheckGuideButton"));

        // Not again at the next event either; a new couch session (after a restore) starts afresh.
        runner.Sync(want: true);
        Assert.Equal(0, _steam.ShutdownRequests);
        runner.Sync(want: false);
        _steam.Running = false;
        runner.Sync(want: false);
        runner.Sync(want: true);
        Assert.True(Applied);
    }

    [Fact]
    public void ASteamThatStartedBetweenCheckAndWriteAndWroteItsOwnStateKeepsTheRestorePending()
    {
        _steam.Running = false;
        var runner = Runner();
        runner.Sync(want: true);
        Assert.True(Applied);
        var applied = _vdf.Files[Local];

        // While the restore is verified, Steam (just started with our values in memory) writes them back.
        _steam.OnSleep = () =>
        {
            _steam.Running = true;
            _vdf.Files[Local] = applied;
            _steam.OnSleep = null;
        };
        runner.Sync(want: false);
        Assert.True(runner.HasPending);
        Assert.True(RunEntry);
        Assert.True(runner.Deferred);

        // Later, with Steam gone, it is put back for real.
        _steam.Running = false;
        runner.Sync(want: false);
        Assert.False(Applied);
        Assert.False(RunEntry);
        Assert.False(runner.HasPending);
    }

    [Fact]
    public void TheRunEntryPathWaitsForSteamToEndAndNeverEndsIt()
    {
        _steam.Running = false;
        Runner().Sync(want: true);
        _steam.Running = true;
        _steam.OnSleep = () =>
        {
            if (_steam.Slept > TimeSpan.FromMinutes(1)) _steam.Running = false; // Steam ends after a minute
        };
        var runner = Runner();
        runner.ReleaseWhenSteamEnds(TimeSpan.FromHours(2), TimeSpan.FromSeconds(20));
        Assert.False(Applied);
        Assert.False(RunEntry);
        Assert.Equal(0, _steam.ShutdownRequests);
        Assert.Equal(0, _steam.Starts);
    }

    [Fact]
    public void TheRunEntryPathGivesUpAfterItsLifetime()
    {
        _steam.Running = false;
        Runner().Sync(want: true);
        _steam.Running = true;
        Runner().ReleaseWhenSteamEnds(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(20));
        Assert.True(Applied);
        Assert.True(RunEntry); // the next sign-in tries again
    }

    [Fact]
    public void LeftoverTempFilesOfACrashedWriteAreRemovedWhenSteamIsOff()
    {
        var root = Path.Combine(_dir, "steam");
        var file = SteamGuard.LocalConfigPath(root, "100");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file + ".couchhud.tmp", "x");
        _steam.Root = root;
        _steam.Running = false;
        Runner().Sync(want: false);
        Assert.False(File.Exists(file + ".couchhud.tmp"));
    }

    private sealed class FakeSteam : ISteamEnvironment
    {
        public string? Root { get; set; }
        public IReadOnlyList<string> UserIds { get; set; } = [];
        public bool Running { get; set; } = true;
        public bool Game { get; set; }
        public bool Client { get; set; }
        public bool Foreground { get; set; }
        public bool Download { get; set; }
        public bool Login { get; set; }
        public bool EndsOnShutdown { get; set; } = true;
        public TimeSpan Age { get; set; } = TimeSpan.FromMinutes(10);
        public DateTime Now { get; set; } = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        public Action? OnSleep { get; set; }
        public int ShutdownRequests { get; private set; }
        public int Starts { get; private set; }
        public TimeSpan Slept { get; private set; }

        public string? SteamRoot => Root;
        public bool SteamRunning => Running;
        public bool ProcessesRunning => Running;
        public TimeSpan? SteamAge => Running ? Age : null;
        public bool GameBusy => Game;
        public bool ClientBusy => Client;
        public bool SteamInForeground => Foreground;
        public bool DownloadActive => Download;
        public bool LoginOpen => Login;

        public void RequestShutdown()
        {
            ShutdownRequests++;
            if (EndsOnShutdown) Running = false;
        }

        public void StartSilent()
        {
            Starts++;
            Running = true;
        }

        public void Sleep(TimeSpan time)
        {
            Slept += time;
            Now += time;
            Age += time;
            OnSleep?.Invoke();
        }
    }

    private sealed class ThrowingVdf(MemoryVdf inner) : IVdfStore
    {
        public bool Exists(string file) => inner.Exists(file);
        public VdfLookup Lookup(string file, string keyPath) => inner.Lookup(file, keyPath);
        public void Write(string file, string keyPath, string value) => throw new IOException("locked");
        public void WriteRaw(string file, string keyPath, string rawToken) => throw new IOException("locked");
        public void Delete(string file, string keyPath, int? pruneFrom = null) => inner.Delete(file, keyPath, pruneFrom);
        public void DeleteFileIfBlank(string file) => inner.DeleteFileIfBlank(file);
    }

    internal sealed class MemoryVdf : IVdfStore
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Exists(string file) => Files.ContainsKey(file);

        public VdfLookup Lookup(string file, string keyPath) =>
            Files.TryGetValue(file, out var text) ? VdfPatch.Lookup(text, keyPath) : new VdfLookup(VdfLookupKind.Missing, null, null, 0);

        public void Write(string file, string keyPath, string value) =>
            Files[file] = VdfPatch.Set(Files.GetValueOrDefault(file, ""), keyPath, value);

        public void WriteRaw(string file, string keyPath, string rawToken) =>
            Files[file] = VdfPatch.SetRaw(Files.GetValueOrDefault(file, ""), keyPath, rawToken);

        public void Delete(string file, string keyPath, int? pruneFrom = null)
        {
            if (Files.TryGetValue(file, out var text)) Files[file] = VdfPatch.Delete(text, keyPath, pruneFrom);
        }

        public void DeleteFileIfBlank(string file)
        {
            if (Files.TryGetValue(file, out var text) && string.IsNullOrWhiteSpace(text)) Files.Remove(file);
        }
    }
}
