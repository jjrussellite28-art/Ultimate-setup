using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public class GameSessionTrackerTests
{
    private static readonly string Games = Path.Combine(Path.GetTempPath(), "couchhud-tracker", "Games");

    private sealed class FakeProbe : IProcessProbe
    {
        public List<(int, string?)> Processes { get; } = [];
        public int SteamApp { get; set; }

        public IReadOnlyList<(int Pid, string? Path)> Snapshot() => Processes;

        public int SteamRunningAppId() => SteamApp;
    }

    private static LibraryItem Game(string id, string? dir) => new()
    {
        Id = id,
        Source = id.Split(':')[0],
        Name = id,
        InstallDir = dir,
        Launch = new LaunchTarget(LaunchKind.Url, "x://")
    };

    [Fact]
    public void Tracks_a_game_by_its_install_folder_and_reports_minutes()
    {
        var probe = new FakeProbe();
        var tracker = new GameSessionTracker(probe);
        var started = new List<GameSession>();
        var ended = new List<double>();
        tracker.Started += started.Add;
        tracker.Ended += (_, minutes) => ended.Add(minutes);

        var t0 = DateTimeOffset.Parse("2026-10-17T20:00:00Z");
        var hades = Game("custom:hades", Path.Combine(Games, "Hades"));
        tracker.Launched(hades, t0);
        tracker.Tick(t0.AddSeconds(5));
        Assert.Null(tracker.Current);

        probe.Processes.Add((42, Path.Combine(Games, "Hades", "Hades.exe")));
        probe.Processes.Add((43, Path.Combine(Games, "Hades2", "Hades2.exe"))); // similar name, other folder
        tracker.Tick(t0.AddSeconds(10));
        Assert.Equal(new[] { 42 }, tracker.Current!.Pids);
        Assert.Single(started);

        probe.Processes.Clear();
        tracker.Tick(t0.AddMinutes(30).AddSeconds(10));
        Assert.Null(tracker.Current);
        Assert.Equal(30, Assert.Single(ended), precision: 3);
    }

    [Fact]
    public void Steam_games_count_as_running_when_steam_says_so()
    {
        var probe = new FakeProbe { SteamApp = 1245620 };
        // The value only counts while Steam itself runs (it is stale after a crash or a power cut).
        probe.Processes.Add((4, Path.Combine(Games, "..", "Steam", "steam.exe")));
        var tracker = new GameSessionTracker(probe);
        var t0 = DateTimeOffset.UtcNow;

        tracker.Launched(Game("steam:1245620", Path.Combine(Games, "ELDEN RING")), t0);
        tracker.Tick(t0.AddSeconds(5));
        Assert.NotNull(tracker.Current);

        probe.SteamApp = 0;
        tracker.Tick(t0.AddMinutes(1));
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void A_launch_without_process_is_given_up()
    {
        var probe = new FakeProbe();
        var tracker = new GameSessionTracker(probe) { StartGrace = TimeSpan.FromMinutes(1) };
        var t0 = DateTimeOffset.UtcNow;
        tracker.Launched(Game("custom:x", Path.Combine(Games, "X")), t0);
        tracker.Tick(t0.AddMinutes(2));

        // Appearing later no longer counts as this launch.
        probe.Processes.Add((1, Path.Combine(Games, "X", "x.exe")));
        tracker.Tick(t0.AddMinutes(3));
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void Drive_roots_are_never_game_folders()
    {
        var probe = new FakeProbe();
        probe.Processes.Add((7, Path.Combine(Path.GetPathRoot(Games)!, "anything.exe")));
        var tracker = new GameSessionTracker(probe);
        var t0 = DateTimeOffset.UtcNow;
        tracker.Launched(Game("custom:root", Path.GetPathRoot(Games)), t0);
        tracker.Tick(t0.AddSeconds(5));
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void Adopts_a_library_game_that_runs_without_a_launch_and_lists_the_others()
    {
        var probe = new FakeProbe();
        var hades = Game("custom:hades", Path.Combine(Games, "Hades"));
        var celeste = Game("custom:celeste", Path.Combine(Games, "Celeste"));
        var tracker = new GameSessionTracker(probe) { Library = () => [hades, celeste] };
        var t0 = DateTimeOffset.Parse("2026-10-17T20:00:00Z");
        tracker.Tick(t0);
        Assert.Null(tracker.Current);

        probe.Processes.Add((1, Path.Combine(Games, "Hades", "Hades.exe")));
        probe.Processes.Add((2, Path.Combine(Games, "Celeste", "Celeste.exe")));
        tracker.Tick(t0.AddSeconds(3));
        Assert.Equal("custom:hades", tracker.Current!.Item.Id);
        Assert.Equal(["custom:celeste"], tracker.Others.Select(o => o.Item.Id));

        probe.Processes.RemoveAt(0);
        tracker.Tick(t0.AddSeconds(6)); // the other one takes over
        Assert.Equal("custom:celeste", tracker.Current!.Item.Id);
    }

    [Fact]
    public void A_second_entry_of_the_same_folder_is_not_a_foreign_game()
    {
        var probe = new FakeProbe();
        var a = Game("custom:a", Path.Combine(Games, "Foo"));
        var b = Game("custom:b", Path.Combine(Games, "Foo"));
        var tracker = new GameSessionTracker(probe) { Library = () => [a, b] };
        probe.Processes.Add((1, Path.Combine(Games, "Foo", "foo.exe")));
        tracker.Tick(DateTimeOffset.Parse("2026-10-17T20:00:00Z"));
        Assert.NotNull(tracker.Current);
        Assert.Empty(tracker.Others);
    }
}
