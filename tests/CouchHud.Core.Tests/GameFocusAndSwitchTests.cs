using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public class GameFrontTests
{
    [Fact]
    public void The_person_always_gets_the_hud()
    {
        var busy = new HudFrontState(GameRunning: true, MouseMode: true, PcMode: true, Browsing: true, Suspended: true);
        Assert.True(GameFront.HudMayTakeFront(HudFrontRequest.Person, busy));
    }

    [Theory]
    [InlineData(HudFrontRequest.GuardRecovery)]
    [InlineData(HudFrontRequest.LauncherStandby)]
    public void Automatic_requests_never_cover_a_game_or_what_the_person_uses(HudFrontRequest request)
    {
        Assert.True(GameFront.HudMayTakeFront(request, new HudFrontState(GameRunning: false)));
        Assert.False(GameFront.HudMayTakeFront(request, new HudFrontState(GameRunning: true)));
        Assert.False(GameFront.HudMayTakeFront(request, new HudFrontState(false, MouseMode: true)));
        Assert.False(GameFront.HudMayTakeFront(request, new HudFrontState(false, PcMode: true)));
        Assert.False(GameFront.HudMayTakeFront(request, new HudFrontState(false, Browsing: true)));
        Assert.False(GameFront.HudMayTakeFront(request, new HudFrontState(false, Suspended: true)));
    }

    [Fact]
    public void A_starting_game_counts_as_running()
    {
        // The guard's context folds a pending start into GameRunning: the standby clients' timer must not cover the starting game.
        var context = new ForegroundContext(Enabled: true, GameRunning: true, MouseMode: false, PcMode: false, Browsing: false, LockPrompt: false, GameStarting: true);
        Assert.False(GameFront.HudMayTakeFront(HudFrontRequest.LauncherStandby, HudFrontState.From(context)));
    }

    private static GameWindow W(long handle, int pid, int width = 1920, int height = 1080, bool visible = true, bool cloaked = false, bool tool = false,
        bool owned = false, bool minimized = false, bool titled = true, int z = 0) =>
        new(handle, pid, visible, cloaked, tool, owned, minimized, width, height, titled, z);

    [Fact]
    public void A_minimized_fullscreen_game_is_found_by_its_real_size()
    {
        // Minimized, its window rect is an icon; the normal placement is the full screen. A small helper window must not win.
        var game = W(1, 10, minimized: true);
        var helper = W(2, 10, width: 300, height: 200, z: 0);
        Assert.Equal(game, GameFront.PickWindow([helper, game], [10]));
    }

    [Fact]
    public void Hidden_cloaked_tool_and_tiny_windows_are_never_picked()
    {
        var windows = new[]
        {
            W(1, 10, visible: false), W(2, 10, cloaked: true), W(3, 10, tool: true), W(4, 10, width: 1, height: 1), W(5, 10, width: 99), W(6, 10, height: 59)
        };
        Assert.Null(GameFront.PickWindow(windows, [10]));
    }

    [Fact]
    public void Only_windows_of_the_game_count()
    {
        var other = W(1, 99, width: 3840, height: 2160);
        var game = W(2, 10, width: 800, height: 600);
        Assert.Equal(game, GameFront.PickWindow([other, game], [10]));
        Assert.Null(GameFront.PickWindow([other], [10]));
        Assert.Null(GameFront.PickWindow([W(0, 10), W(3, 0)], [10, 0]));
    }

    [Fact]
    public void The_main_window_wins_over_its_dialogs_and_untitled_surfaces()
    {
        var main = W(1, 10, width: 1280, height: 720, z: 2);
        var dialog = W(2, 10, width: 1920, height: 1080, owned: true, z: 0);
        var untitled = W(3, 10, width: 1920, height: 1080, titled: false, z: 1);
        Assert.Equal(main, GameFront.PickWindow([dialog, untitled, main], [10]));
    }

    [Fact]
    public void Between_equals_the_bigger_then_the_front_one_wins()
    {
        var small = W(1, 10, width: 800, height: 600, z: 0);
        var big = W(2, 11, width: 1920, height: 1080, z: 3);
        var bigBehind = W(3, 11, width: 1920, height: 1080, z: 5);
        Assert.Equal(big, GameFront.PickWindow([small, bigBehind, big], [10, 11]));
    }
}

public class NewGameFrontStepTests
{
    [Fact]
    public void The_person_coming_back_to_the_hud_ends_it_before_anything_else()
    {
        Assert.Equal(NewGameFrontStep.Stop, GameFront.NewGameStep(true, true, false, true));
        Assert.Equal(NewGameFrontStep.Stop, GameFront.NewGameStep(true, false, false, false));
    }

    [Fact]
    public void It_waits_for_the_new_game_and_for_its_window()
    {
        Assert.Equal(NewGameFrontStep.Wait, GameFront.NewGameStep(false, false, false, true));
        Assert.Equal(NewGameFrontStep.Wait, GameFront.NewGameStep(false, true, false, false));
    }

    [Fact]
    public void The_gate_stops_it_and_never_activates()
    {
        Assert.Equal(NewGameFrontStep.Stop, GameFront.NewGameStep(false, true, true, true));
        Assert.Equal(NewGameFrontStep.Stop, GameFront.NewGameStep(false, true, true, false));
    }

    [Fact]
    public void Pc_mode_the_couch_browser_or_the_mouse_mode_stop_it()
    {
        Assert.Equal(NewGameFrontStep.Stop, GameFront.NewGameStep(false, true, false, true, screenElsewhere: true));
        Assert.Equal(NewGameFrontStep.Stop, GameFront.NewGameStep(false, false, false, false, screenElsewhere: true));
        Assert.Equal(NewGameFrontStep.Activate, GameFront.NewGameStep(false, true, false, true, screenElsewhere: false));
    }

    [Fact]
    public void Only_the_new_game_with_a_window_and_an_open_gate_is_activated()
    {
        Assert.Equal(NewGameFrontStep.Activate, GameFront.NewGameStep(false, true, false, true));
        foreach (var person in new[] { false, true })
            foreach (var isNew in new[] { false, true })
                foreach (var refuses in new[] { false, true })
                    foreach (var window in new[] { false, true })
                        Assert.Equal(!person && isNew && !refuses && window, GameFront.NewGameStep(person, isNew, refuses, window) == NewGameFrontStep.Activate);
    }
}

public class NewGameWatchStepTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromMilliseconds(30);

    [Fact]
    public void Takes_back_only_the_new_games_front_within_two_seconds_of_its_first_window()
    {
        Assert.Equal(NewGameWatchStep.TakeBack, GameFront.WatchStep(false, true, true, Soon, true));
        Assert.Equal(NewGameWatchStep.TakeBack, GameFront.WatchStep(false, true, true, GameFront.TakeBackWindow, true));
        Assert.Equal(NewGameWatchStep.Stop, GameFront.WatchStep(false, true, true, GameFront.TakeBackWindow + TimeSpan.FromMilliseconds(1), true));
        Assert.Equal(NewGameWatchStep.Wait, GameFront.WatchStep(false, true, true, Soon, false));
    }

    [Fact]
    public void Waits_for_the_game_and_its_window_and_ignores_other_games()
    {
        Assert.Equal(NewGameWatchStep.Wait, GameFront.WatchStep(false, false, false, TimeSpan.Zero, false));
        Assert.Equal(NewGameWatchStep.Wait, GameFront.WatchStep(false, false, true, Soon, true));
        Assert.Equal(NewGameWatchStep.Wait, GameFront.WatchStep(false, true, false, TimeSpan.Zero, true));
    }

    [Fact]
    public void Never_over_pc_mode_the_browser_or_the_mouse_mode()
    {
        Assert.Equal(NewGameWatchStep.Stop, GameFront.WatchStep(true, true, true, Soon, true));
        Assert.Equal(NewGameWatchStep.Stop, GameFront.WatchStep(true, false, false, TimeSpan.Zero, false));
    }
}

public class GameSwitchTests
{
    private static LibraryItem Game(string id, string? dir = null) => new()
    {
        Id = id, Source = "custom", Name = id.ToUpperInvariant(), Type = ItemType.Game, InstallDir = dir ?? $@"D:\Games\{id}",
        Launch = new LaunchTarget(LaunchKind.Exe, $@"D:\Games\{id}\{id}.exe")
    };

    private static LibraryItem App(string id) => new()
    {
        Id = id, Source = "custom", Name = id, Type = ItemType.App, Launch = new LaunchTarget(LaunchKind.Exe, $@"C:\Tools\{id}.exe")
    };

    private static GameSession Running(LibraryItem item) => new(item, DateTimeOffset.UtcNow, [500]);

    private static GameSwitchDecision Decide(LibraryItem target, GameSession? current, bool pending = false, bool parental = false, string? from = null, bool switching = false) =>
        GameSwitch.Decide(target, current, pending, parental, from, switching);

    [Fact]
    public void Nothing_running_starts_as_always()
    {
        Assert.Equal(GameSwitchAction.Launch, Decide(Game("a"), null).Action);
        Assert.Equal(GameSwitchAction.Launch, Decide(Game("a"), null, pending: true).Action);
        Assert.Equal(GameSwitchAction.Launch, Decide(App("notes"), null, parental: true).Action);
    }

    [Fact]
    public void A_question_about_a_game_that_ended_meanwhile_just_starts()
    {
        Assert.Equal(GameSwitchAction.Launch, Decide(Game("b"), null, from: "a").Action);
    }

    [Fact]
    public void Parental_controls_wait_for_a_start_under_way()
    {
        Assert.Equal(new GameSwitchDecision(GameSwitchAction.Refuse, GameSwitchRefusal.StartPending), Decide(Game("b"), null, pending: true, parental: true));
    }

    [Fact]
    public void Starting_the_running_game_resumes_it()
    {
        var a = Game("a");
        Assert.Equal(GameSwitchAction.Resume, Decide(a, Running(a)).Action);
        Assert.Equal(GameSwitchAction.Resume, Decide(a, Running(a), parental: true).Action);
        // The same game under another entry (same install folder) is the same game.
        var twin = Game("a2", dir: @"D:\Games\a");
        Assert.Equal(GameSwitchAction.Resume, Decide(twin, Running(a)).Action);
    }

    [Fact]
    public void Another_game_asks_first()
    {
        Assert.Equal(GameSwitchAction.Confirm, Decide(Game("b"), Running(Game("a"))).Action);
        Assert.Equal(GameSwitchAction.Confirm, Decide(Game("b"), Running(Game("a")), parental: true).Action);
    }

    [Fact]
    public void Only_a_yes_for_the_game_that_runs_switches()
    {
        var a = Running(Game("a"));
        Assert.Equal(GameSwitchAction.Switch, Decide(Game("b"), a, from: "a").Action);
        Assert.Equal(GameSwitchAction.Switch, Decide(Game("b"), a, parental: true, from: "a").Action);
        // The question named a game that is no longer the one running: nothing is closed.
        Assert.Equal(new GameSwitchDecision(GameSwitchAction.Refuse, GameSwitchRefusal.Changed), Decide(Game("b"), Running(Game("c")), from: "a"));
    }

    [Fact]
    public void Apps_start_beside_a_game_except_with_parental_controls()
    {
        var a = Running(Game("a"));
        Assert.Equal(GameSwitchAction.Launch, Decide(App("notes"), a).Action);
        // An app never closes the game, not even with a yes for it.
        Assert.Equal(GameSwitchAction.Launch, Decide(App("notes"), a, from: "a").Action);
        Assert.Equal(new GameSwitchDecision(GameSwitchAction.Refuse, GameSwitchRefusal.GameRunning), Decide(App("notes"), a, parental: true));
        Assert.Equal(new GameSwitchDecision(GameSwitchAction.Refuse, GameSwitchRefusal.GameRunning), Decide(App("notes"), a, parental: true, from: "a"));
    }

    [Fact]
    public void One_switch_at_a_time()
    {
        Assert.Equal(new GameSwitchDecision(GameSwitchAction.Refuse, GameSwitchRefusal.Busy), Decide(Game("b"), Running(Game("a")), from: "a", switching: true));
    }
}

public class SafeTerminationSpareTests
{
    private static readonly string Games = Path.Combine(Path.GetTempPath(), "couchhud-safe", "Games");
    private static readonly string SteamDir = Path.Combine(Path.GetTempPath(), "couchhud-safe", "Steam");

    private sealed class FakeControl : IProcessControl
    {
        public int OwnPid => 100;
        public int OwnSession => 1;
        public string WindowsDir => @"C:\Windows";
        public List<LiveProcess> Live { get; } = [];
        public List<int> Killed { get; } = [];
        public IReadOnlyList<LiveProcess> List() => Live.ToList();
        public bool RequestClose(int pid, long start) => true;

        public bool Kill(int pid, long start)
        {
            var p = Live.FirstOrDefault(x => x.Pid == pid && x.Start == start);
            if (p is null) return false;
            Killed.Add(pid);
            Live.Remove(p);
            return true;
        }
    }

    private static LiveProcess P(int pid, long start, int parent, string dir, string name) => new(pid, parent, start, Path.Combine(dir, name + ".exe"), 1, name);

    private static Dictionary<int, ProcessStamp> Stamps(params LiveProcess[] ps) => ps.ToDictionary(p => p.Pid, p => new ProcessStamp(p.Path, p.Start));

    [Theory]
    [InlineData("steam", true)]
    [InlineData("steamwebhelper", true)]
    [InlineData("EpicGamesLauncher", true)]
    [InlineData("valheim", false)]
    [InlineData("crashpad_handler", false)]
    public void Knows_the_game_clients(string name, bool client)
    {
        Assert.Equal(client, SafeTermination.IsGameClient(P(1, 1, 0, SteamDir, name)));
    }

    [Fact]
    public async Task A_client_the_game_started_survives_the_switch_with_its_children()
    {
        // The game started Steam (it was not running yet); Steam then started its web helper. Only the game and its own helper end.
        var ctl = new FakeControl();
        var game = P(500, 1000, 1, Games, "valheim");
        var crash = P(501, 1001, 500, Games, "crashpad_handler");
        var steam = P(600, 1002, 500, SteamDir, "steam");
        var web = P(601, 1003, 600, SteamDir, "steamwebhelper");
        ctl.Live.AddRange([game, crash, steam, web]);
        var outcome = await SafeTermination.QuitAsync(ctl, Stamps(game), false, TimeSpan.FromMilliseconds(30), poll: TimeSpan.FromMilliseconds(5), spare: SafeTermination.IsGameClient);
        Assert.Equal([500, 501], ctl.Killed.Order());
        Assert.Contains(steam, ctl.Live);
        Assert.Contains(web, ctl.Live);
        Assert.Equal(2, outcome.Found);
    }

    [Fact]
    public async Task A_client_among_the_remembered_processes_is_spared_too()
    {
        var ctl = new FakeControl();
        var game = P(500, 1000, 1, Games, "valheim");
        var steam = P(600, 900, 1, SteamDir, "steam");
        ctl.Live.AddRange([game, steam]);
        await SafeTermination.QuitAsync(ctl, Stamps(game, steam), false, TimeSpan.FromMilliseconds(30), poll: TimeSpan.FromMilliseconds(5), spare: SafeTermination.IsGameClient);
        Assert.Equal([500], ctl.Killed);
    }

    [Fact]
    public async Task Sparing_does_not_open_the_door_for_a_reused_pid()
    {
        var ctl = new FakeControl();
        var old = P(500, 1000, 1, Games, "valheim");
        ctl.Live.Add(P(500, 9000, 1, Games, "other"));
        var outcome = await SafeTermination.QuitAsync(ctl, Stamps(old), true, TimeSpan.FromMilliseconds(30), poll: TimeSpan.FromMilliseconds(5), spare: SafeTermination.IsGameClient);
        Assert.Empty(ctl.Killed);
        Assert.Equal(0, outcome.Found);
    }

    [Fact]
    public void Without_spare_nothing_changes()
    {
        var ctl = new FakeControl();
        var game = P(500, 1000, 1, Games, "valheim");
        var steam = P(600, 1002, 500, SteamDir, "steam");
        ctl.Live.AddRange([game, steam]);
        Assert.Equal([500, 600], SafeTermination.Resolve(Stamps(game), ctl.Live, ctl).Select(p => p.Pid).Order());
    }
}

public class GameSwitchSequenceTests
{
    private static LibraryItem Game(string id) => new()
    {
        Id = id, Source = "custom", Name = id.ToUpperInvariant(), Type = ItemType.Game, InstallDir = $@"D:\Games\{id}",
        Launch = new LaunchTarget(LaunchKind.Exe, $@"D:\Games\{id}\{id}.exe")
    };

    private sealed class Fake : IGameSwitchSteps
    {
        public List<string> Log { get; } = [];
        public bool OldRunning = true;
        public bool OldExitsOnClose = true;
        public string? Obstacle;
        public Exception? CheckThrows;
        public bool KillStep;
        public Func<Task>? DuringClose;
        public Exception? CloseThrows;
        public Action? AfterClose;
        public Exception? StartThrows;

        public void RequireLaunchable() => Log.Add("require");
        public void Thaw() => Log.Add("thaw");

        public async Task CloseOldAsync(Action<string> callOff, CancellationToken cancel)
        {
            Log.Add("close");
            if (DuringClose is not null) await DuringClose();
            if (CloseThrows is not null) throw CloseThrows;
            if (cancel.IsCancellationRequested) return;
            // The last look right before force, like the shell's mayEnd.
            if (KillStep)
            {
                if (await CheckAsync() is { } why) { callOff(why); return; }
                Log.Add("kill");
            }
            if (OldExitsOnClose) OldRunning = false;
            AfterClose?.Invoke();
        }

        public Task<string?> CheckAsync()
        {
            if (CheckThrows is not null) throw CheckThrows;
            return Task.FromResult(Obstacle);
        }

        public Task<bool> OldStillRunningAsync() => Task.FromResult(OldRunning);

        public LibraryItem StartNew()
        {
            Log.Add("start");
            if (StartThrows is not null) throw StartThrows;
            Assert.False(OldRunning, "the new game starts only after the old one is gone");
            return Game("b");
        }
    }

    private static Task<LibraryItem> Run(IGameSwitchSteps f, int settleMs = 50) =>
        GameSwitchSequence.RunAsync(f, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(settleMs), TimeSpan.FromMilliseconds(5));

    [Fact]
    public async Task Closes_the_old_game_and_then_starts_the_new_one()
    {
        var f = new Fake();
        var item = await Run(f);
        Assert.Equal("b", item.Id);
        Assert.Equal(["require", "thaw", "close", "start"], f.Log);
    }

    [Fact]
    public async Task A_refused_new_start_closes_nothing()
    {
        var g = new ThrowingRequire();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(g));
        Assert.Empty(g.Closed);
    }

    private sealed class ThrowingRequire : IGameSwitchSteps
    {
        public List<string> Closed { get; } = [];
        public void RequireLaunchable() => throw new InvalidOperationException("no");
        public void Thaw() => Closed.Add("thaw");
        public Task CloseOldAsync(Action<string> callOff, CancellationToken cancel) { Closed.Add("close"); return Task.CompletedTask; }
        public Task<string?> CheckAsync() => Task.FromResult<string?>(null);
        public Task<bool> OldStillRunningAsync() => Task.FromResult(false);
        public LibraryItem StartNew() => throw new InvalidOperationException();
    }

    [Fact]
    public async Task Nothing_left_to_close_still_waits_for_the_tracker_and_starts()
    {
        var f = new Fake { CloseThrows = new InvalidOperationException("nothing"), OldRunning = false };
        Assert.Equal("b", (await Run(f)).Id);
        Assert.Equal("start", f.Log[^1]);
    }

    [Fact]
    public async Task An_old_game_the_tracker_keeps_seeing_never_lets_the_new_one_start()
    {
        var f = new Fake { OldExitsOnClose = false };
        await Assert.ThrowsAsync<GameSwitchStillRunningException>(() => Run(f, settleMs: 30));
        Assert.DoesNotContain("start", f.Log);
    }

    [Fact]
    public async Task The_look_right_before_force_can_call_the_switch_off_and_nothing_is_killed()
    {
        var f = new Fake { KillStep = true, Obstacle = "parents said no", OldExitsOnClose = false };
        var ex = await Assert.ThrowsAsync<GameSwitchCalledOffException>(() => Run(f));
        Assert.Equal("parents said no", ex.Message);
        Assert.False(ex.OldClosed);
        Assert.DoesNotContain("kill", f.Log);
        Assert.DoesNotContain("start", f.Log);
    }

    [Fact]
    public async Task A_call_off_after_the_old_game_exited_says_so()
    {
        var f = new Fake { OldExitsOnClose = true };
        // The watcher calls off while the old game is closing, then it is gone.
        f.DuringClose = async () => { f.Obstacle = "time is up"; await Task.Delay(60); f.OldRunning = false; };
        var ex = await Assert.ThrowsAsync<GameSwitchCalledOffException>(() => Run(f));
        Assert.True(ex.OldClosed);
        Assert.DoesNotContain("start", f.Log);
    }

    [Fact]
    public async Task A_watcher_that_throws_calls_the_switch_off()
    {
        var f = new Fake { CheckThrows = new IOException("disk gone"), OldExitsOnClose = false };
        f.DuringClose = () => Task.Delay(60);
        var ex = await Assert.ThrowsAsync<GameSwitchCalledOffException>(() => Run(f));
        Assert.Equal("disk gone", ex.Message);
        Assert.DoesNotContain("start", f.Log);
    }

    [Fact]
    public async Task Another_game_that_takes_over_after_the_watcher_stopped_is_not_joined_by_a_second()
    {
        var f = new Fake();
        // After the old game is gone and the watcher has stopped, something else starts: the last look before the start sees it.
        f.DuringClose = () => { f.AfterClose = () => f.Obstacle = "another game runs"; return Task.CompletedTask; };
        var ex = await Assert.ThrowsAsync<GameSwitchCalledOffException>(() => Run(f));
        Assert.True(ex.OldClosed);
        Assert.Equal("another game runs", ex.Message);
        Assert.DoesNotContain("start", f.Log);
    }

    [Fact]
    public async Task A_start_that_fails_after_the_close_is_reported_as_called_off_with_the_old_game_closed()
    {
        var f = new Fake { StartThrows = new InvalidOperationException("grant used up") };
        var ex = await Assert.ThrowsAsync<GameSwitchCalledOffException>(() => Run(f));
        Assert.True(ex.OldClosed);
        Assert.Equal("grant used up", ex.Message);
    }

    [Fact]
    public async Task A_failure_of_the_close_is_not_replaced_by_the_watcher()
    {
        var f = new Fake { CloseThrows = new TimeoutException("boom"), CheckThrows = new IOException("watch") };
        f.DuringClose = () => Task.Delay(30);
        await Assert.ThrowsAsync<TimeoutException>(() => Run(f));
    }
}

public class SafeTerminationMayEndTests
{
    private sealed class Ctl : IProcessControl
    {
        public int OwnPid => 100;
        public int OwnSession => 1;
        public string WindowsDir => @"C:\Windows";
        public List<LiveProcess> Live { get; } = [];
        public List<int> Killed { get; } = [];
        public IReadOnlyList<LiveProcess> List() => Live.ToList();
        public bool RequestClose(int pid, long start) => true;
        public bool Kill(int pid, long start) { Killed.Add(pid); Live.RemoveAll(p => p.Pid == pid); return true; }
    }

    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "couchhud-mayend");

    [Fact]
    public async Task A_no_from_mayEnd_ends_nothing_and_a_yes_ends_the_game()
    {
        var game = new LiveProcess(500, 1, 1000, Path.Combine(Dir, "g.exe"), 1, "g");
        var stamps = new Dictionary<int, ProcessStamp> { [500] = new(game.Path, 1000) };

        var ctl = new Ctl();
        ctl.Live.Add(game);
        var asked = 0;
        var no = await SafeTermination.QuitAsync(ctl, stamps, true, TimeSpan.Zero, mayEnd: () => { asked++; return false; });
        Assert.Empty(ctl.Killed);
        Assert.Equal(1, asked);
        Assert.Equal(new SafeTermination.Outcome(1, 0), no);

        var yes = await SafeTermination.QuitAsync(ctl, stamps, true, TimeSpan.Zero, mayEnd: () => true);
        Assert.Equal([500], ctl.Killed);
        Assert.Equal(1, yes.Killed);
    }
}
