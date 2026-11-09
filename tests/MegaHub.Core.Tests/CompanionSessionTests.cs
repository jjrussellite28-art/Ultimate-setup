using MegaHub.Core.Config;
using MegaHub.Core.Guard;
using MegaHub.Core.Library;
using CouchHud.Sdk;

namespace MegaHub.Core.Tests;

/// <summary>The start of companions after the game is in front (the focus stays with the game) and their end (exactly the process Mega Hub started).</summary>
public class CompanionSessionTests
{
    private const int Own = 100;
    private const int GamePid = 500;

    private static LibraryItem App(string id, string path = @"C:\Apps\app.exe") => new()
    {
        Id = id,
        Source = "custom",
        Name = id,
        Type = ItemType.App,
        Launch = new LaunchTarget(LaunchKind.Exe, path)
    };

    private static CompanionStart Start(string id, bool close = false, bool minimized = true, string? path = null) =>
        new(new CompanionRule { AppId = id, Mode = "all", CloseWithGame = close, Minimized = minimized }, App(id, path ?? $@"C:\Apps\{id}.exe"), new LaunchTarget(LaunchKind.Exe, path ?? $@"C:\Apps\{id}.exe"));

    private static CompanionPlan Plan(params CompanionStart[] starts) => new(starts, []);

    private sealed class FakeHost : ICompanionHost
    {
        public List<string> Log { get; } = [];
        public List<string> Started { get; } = [];
        public List<long> Minimized { get; } = [];
        public HashSet<string> Running { get; } = [];
        public Dictionary<string, string?> Real { get; } = [];
        public Dictionary<string, int> Pids { get; } = [];
        public HashSet<string> Fail { get; } = [];
        public HashSet<string> Throw { get; } = [];
        // Processes that descend from a started companion (a window of something it started).
        public HashSet<int> Children { get; } = [];
        public HashSet<long> Exempt { get; } = [];
        public string? ImageOverride { get; set; }
        public bool NoImage { get; set; }
        public long StartTime { get; set; } = 2000;
        public ForegroundInfo? Front { get; set; } = new(1, GamePid, 10);
        public Func<string, ForegroundInfo?>? StealOnStart { get; set; }

        public string? Canonical(CompanionStart start) => Real.TryGetValue(start.App.Id, out var real) ? real : start.Start.Target;

        public bool IsRunning(string canonicalPath) => Running.Contains(canonicalPath);

        public CompanionStarted? Start(CompanionStart start, string canonical)
        {
            var name = start.App.Id;
            if (Throw.Contains(name)) throw new InvalidOperationException("boom");
            if (Fail.Contains(name)) return null;
            Started.Add(name);
            var pid = Pids.TryGetValue(name, out var p) ? p : 600 + Started.Count;
            if (StealOnStart?.Invoke(name) is { } stolen) Front = stolen;
            return new CompanionStarted(pid, new ProcessStamp(NoImage ? null : ImageOverride ?? canonical, StartTime + pid), canonical);
        }

        public ForegroundInfo? Foreground() => Front;

        public bool InGameTree(int pid) => pid == GamePid;

        public bool InCompanionTree(int pid, IReadOnlyDictionary<int, long> roots) => roots.ContainsKey(pid) || Children.Contains(pid);

        public bool MayMinimize(ForegroundInfo front) => !Exempt.Contains(front.Window);

        public bool MinimizeWindow(long window)
        {
            Minimized.Add(window);
            // The game gets the front back, as Windows does when the window in front is minimized.
            Front = new ForegroundInfo(1, GamePid, 10);
            return true;
        }

        public Task Delay(TimeSpan time, CancellationToken cancel) => Task.CompletedTask;

        void ICompanionHost.Log(string line) => Log.Add(line);
    }

    // ---- the focus stays with the game

    [Fact]
    public async Task A_companion_that_takes_the_focus_is_minimized_and_the_game_stays_in_front()
    {
        var host = new FakeHost { StealOnStart = _ => new ForegroundInfo(77, 601, 2601) };
        var runs = await CompanionLauncher.StartAsync(Plan(Start("wand")), "game", host, Own);
        Assert.Equal([77L], host.Minimized);
        Assert.Equal(GamePid, host.Front!.Pid);
        Assert.Single(runs);
        Assert.Contains(host.Log, l => l.Contains("took the focus"));
    }

    [Fact]
    public async Task A_window_of_something_the_companion_started_is_minimized_too()
    {
        var host = new FakeHost { StealOnStart = _ => new ForegroundInfo(88, 999, 5000) };
        host.Children.Add(999);
        await CompanionLauncher.StartAsync(Plan(Start("discord")), "game", host, Own);
        Assert.Equal([88L], host.Minimized);
    }

    [Fact]
    public async Task A_stranger_that_is_merely_new_is_never_touched()
    {
        // Not the companion and not a process of its tree (a launcher stub whose child is out of reach included): left alone.
        var host = new FakeHost { StealOnStart = _ => new ForegroundInfo(88, 999, 5000) };
        await CompanionLauncher.StartAsync(Plan(Start("discord")), "game", host, Own);
        Assert.Empty(host.Minimized);
        Assert.Equal(999, host.Front!.Pid);
    }

    [Fact]
    public async Task The_games_own_windows_and_megahub_are_never_minimized()
    {
        var host = new FakeHost { StealOnStart = _ => new ForegroundInfo(5, GamePid, 10) };
        host.Children.Add(GamePid);
        await CompanionLauncher.StartAsync(Plan(Start("wand")), "game", host, Own);
        Assert.Empty(host.Minimized);
        host.Front = new ForegroundInfo(9, Own, 1);
        host.Children.Add(Own);
        host.StealOnStart = _ => new ForegroundInfo(9, Own, 1);
        await CompanionLauncher.StartAsync(Plan(Start("discord")), "game", host, Own);
        Assert.Empty(host.Minimized);
    }

    [Fact]
    public async Task Nothing_is_minimized_when_the_game_is_not_in_front()
    {
        // Back in the HUD (or elsewhere): a companion may show, there is no game focus to protect.
        var host = new FakeHost { Front = new ForegroundInfo(9, Own, 1) };
        host.StealOnStart = _ => new ForegroundInfo(77, 601, 2601);
        await CompanionLauncher.StartAsync(Plan(Start("wand")), "game", host, Own);
        Assert.Empty(host.Minimized);
    }

    [Fact]
    public async Task An_exempt_window_is_left_alone_even_when_it_is_the_companions()
    {
        var host = new FakeHost { StealOnStart = _ => new ForegroundInfo(77, 601, 2601) };
        host.Exempt.Add(77);
        await CompanionLauncher.StartAsync(Plan(Start("wand")), "game", host, Own);
        Assert.Empty(host.Minimized);
    }

    [Fact]
    public void The_focus_rule_only_ever_hits_the_companions_tree()
    {
        Assert.False(CompanionFocus.Stole(new ForegroundInfo(1, Own, 5000), Own, false, true));
        Assert.False(CompanionFocus.Stole(new ForegroundInfo(1, 7, 5000), Own, true, true));
        Assert.True(CompanionFocus.Stole(new ForegroundInfo(1, 7, 0), Own, false, true));
        Assert.False(CompanionFocus.Stole(new ForegroundInfo(1, 7, 99999), Own, false, false));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\LogonUI.exe", null, true, true, true)]
    [InlineData(@"C:\Windows\explorer.exe", null, true, true, true)]
    [InlineData(@"C:\Apps\x.exe", "#32770", true, true, true)]
    [InlineData(@"C:\Apps\x.exe", null, false, true, true)]
    [InlineData(@"C:\Apps\x.exe", null, true, false, true)]
    [InlineData(null, null, true, true, true)]
    [InlineData(@"C:\Apps\consent.exe", null, true, true, true)]
    [InlineData(@"C:\Apps\wand.exe", "Chrome_WidgetWin_1", true, true, false)]
    public void Windows_that_are_never_minimized(string? image, string? cls, bool sameSession, bool sameIntegrity, bool exempt) =>
        Assert.Equal(exempt, CompanionFocus.Exempt(image, cls, sameSession, sameIntegrity, @"C:\Windows"));

    // ---- start

    [Fact]
    public async Task An_app_that_runs_already_is_not_started_again_nor_recorded_for_ending()
    {
        var host = new FakeHost();
        host.Running.Add(@"C:\Apps\wand.exe");
        var runs = await CompanionLauncher.StartAsync(Plan(Start("wand", close: true), Start("discord", close: true)), "game", host, Own);
        Assert.Equal(["discord"], host.Started);
        Assert.Equal(["discord"], runs.Select(r => r.AppId));
    }

    [Fact]
    public async Task The_running_test_and_the_start_use_the_same_real_path()
    {
        // The library holds a link or a short spelling; what runs is judged on the real path: it is what "already running" looks at too.
        var host = new FakeHost();
        host.Real["wand"] = @"C:\Program Files\Wand\Wand.exe";
        host.Running.Add(@"C:\Program Files\Wand\Wand.exe");
        var runs = await CompanionLauncher.StartAsync(Plan(Start("wand", path: @"C:\PROGRA~1\Wand\Wand.exe")), "game", host, Own);
        Assert.Empty(runs);
        Assert.Empty(host.Started);
    }

    [Fact]
    public async Task A_program_that_could_not_be_verified_is_not_started()
    {
        var host = new FakeHost();
        host.Real["a"] = null;
        var runs = await CompanionLauncher.StartAsync(Plan(Start("a"), Start("b")), "game", host, Own);
        Assert.Equal(["b"], runs.Select(r => r.AppId));
        Assert.Contains(host.Log, l => l.Contains("a: program not verified"));
    }

    [Fact]
    public async Task A_failed_or_throwing_start_does_not_stop_the_others()
    {
        var host = new FakeHost();
        host.Fail.Add("a");
        host.Throw.Add("b");
        var runs = await CompanionLauncher.StartAsync(Plan(Start("a"), Start("b"), Start("c")), "game", host, Own);
        Assert.Equal(["c"], runs.Select(r => r.AppId));
        Assert.Contains(host.Log, l => l.Contains("a: not started"));
        Assert.Contains(host.Log, l => l.Contains("b: not started"));
    }

    [Fact]
    public async Task The_gate_is_asked_before_every_single_start_and_ends_the_round()
    {
        var host = new FakeHost();
        var asked = new List<string>();
        // The lock closed, the profile changed or the rule went between the first and the second start: the rest is not started.
        var runs = await CompanionLauncher.StartAsync(Plan(Start("a"), Start("b"), Start("c")), "game", host, Own, s =>
        {
            asked.Add(s.App.Id);
            return s.App.Id == "a";
        });
        Assert.Equal(["a", "b"], asked);
        Assert.Equal(["a"], host.Started);
        Assert.Equal(["a"], runs.Select(r => r.AppId));
    }

    [Fact]
    public async Task A_run_is_endable_only_with_the_option_a_known_start_time_and_the_checked_image()
    {
        var host = new FakeHost();
        var runs = await CompanionLauncher.StartAsync(Plan(Start("wand", close: true), Start("discord", close: false)), "game", host, Own);
        Assert.True(runs[0].CloseWithGame);
        Assert.False(runs[1].CloseWithGame);
        Assert.Equal(@"C:\Apps\wand.exe", runs[0].Image);
        // Windows would not tell the start time: the process cannot be told from a later one, so it is never ended.
        var blind = new FakeHost { StartTime = -9000 };
        Assert.False((await CompanionLauncher.StartAsync(Plan(Start("y", close: true)), "game", blind, Own)).Single().CloseWithGame);
        // The process runs another program than the file that was checked (a stub that replaced itself): never ended.
        var swapped = new FakeHost { ImageOverride = @"C:\Windows\System32\cmd.exe" };
        var run = (await CompanionLauncher.StartAsync(Plan(Start("z", close: true)), "game", swapped, Own)).Single();
        Assert.False(run.CloseWithGame);
        Assert.Contains(swapped.Log, l => l.Contains("not the checked file"));
        // The image could not be read at all: the same.
        var unreadable = new FakeHost { NoImage = true };
        Assert.False((await CompanionLauncher.StartAsync(Plan(Start("q", close: true)), "game", unreadable, Own)).Single().CloseWithGame);
    }

    // ---- who is ended when

    private static CompanionRun Run(string app, string game, int pid, long start, bool close = true, string? image = null) =>
        new(app, game, pid, start, image ?? $@"C:\Apps\{app}.exe", close);

    [Fact]
    public void A_game_that_ends_releases_only_its_own_companions_with_the_option()
    {
        var registry = new CompanionRegistry();
        registry.Add([Run("wand", "g1", 601, 10), Run("discord", "g1", 602, 11, close: false), Run("obs", "g2", 603, 12)]);
        var close = registry.Ended("g1", [], []);
        Assert.Equal([601], close.Select(r => r.Pid));
        // Forgotten either way; the other game's companion is untouched.
        Assert.Equal([603], registry.All().Select(r => r.Pid));
    }

    [Fact]
    public void A_companion_another_running_game_wants_stays_and_is_handed_over()
    {
        var registry = new CompanionRegistry();
        registry.Add([Run("wand", "g1", 601, 10)]);
        var rules = new[] { new CompanionRule { AppId = "wand", Mode = "all" } };
        Assert.Empty(registry.Ended("g1", ["g2"], rules));
        Assert.Equal("g2", registry.All().Single().GameId);
        // When that game ends, it goes too.
        Assert.Single(registry.Ended("g2", [], rules));
    }

    [Fact]
    public void A_rule_list_with_nulls_does_not_break_the_end_of_a_game()
    {
        // A hand-edited config.json can hold a null game list, a null entry or an empty id.
        var registry = new CompanionRegistry();
        registry.Add([Run("wand", "g1", 601, 10), Run("discord", "g1", 602, 11)]);
        CompanionRule?[] rules =
        [
            null,
            new CompanionRule { AppId = "wand", Mode = "games", GameIds = null! },
            new CompanionRule { AppId = "discord", Mode = "games", GameIds = [null!, "g2"] },
            new CompanionRule { AppId = "", Mode = "all" }
        ];
        var close = registry.Ended("g1", ["g2"], rules);
        // wand: nobody else wants it (its game list is broken): ended. discord: g2 is named, so it stays and moves to g2.
        Assert.Equal([601], close.Select(r => r.Pid));
        Assert.Equal("g2", registry.All().Single().GameId);
        Assert.False(CompanionRules.Applies(new CompanionRule { AppId = "x", Mode = "games", GameIds = null! }, "g1"));
    }

    private sealed class FakeControl : IProcessControl
    {
        public int OwnPid => Own;
        public int OwnSession => 1;
        public string WindowsDir => @"C:\Windows";
        public List<LiveProcess> Live { get; } = [];
        public List<int> Closed { get; } = [];
        public List<int> Killed { get; } = [];
        public List<(int Pid, string? Image)> KillImages { get; } = [];
        public IReadOnlyList<LiveProcess> List() => Live.ToList();
        public bool RequestClose(int pid, long start) { Closed.Add(pid); return true; }

        public bool Kill(int pid, long start)
        {
            var p = Live.FirstOrDefault(x => x.Pid == pid && x.Start == start);
            if (p is null) return false;
            Killed.Add(pid);
            Live.Remove(p);
            return true;
        }

        // The image the ending names is the one read when the list was made (the Win32 side compares it on the handle that ends).
        public bool Kill(int pid, long start, string? image)
        {
            KillImages.Add((pid, image));
            return Kill(pid, start);
        }
    }

    private static LiveProcess Proc(int pid, long start, int parent = 0, string name = "wand", string? path = null) =>
        new(pid, parent, start, path ?? $@"C:\Apps\{name}.exe", 1, name);

    private static Task<SafeTermination.Outcome> Stop(FakeControl ctl, IReadOnlyList<CompanionRun> runs, params int[] gamePids) =>
        CompanionStop.StopAsync(runs, ctl, gamePids.ToHashSet(), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5));

    [Fact]
    public async Task The_process_we_started_is_asked_to_close_then_ended()
    {
        var ctl = new FakeControl();
        ctl.Live.Add(Proc(601, 10));
        var outcome = await Stop(ctl, [Run("wand", "g", 601, 10)]);
        Assert.Equal([601], ctl.Closed);
        Assert.Equal([601], ctl.Killed);
        Assert.Equal(1, outcome.Found);
        // The ending carries the program image it saw, so the handle that ends can compare it.
        Assert.Equal((601, @"C:\Apps\wand.exe"), ctl.KillImages.Single());
    }

    [Fact]
    public async Task What_the_companion_started_is_ended_with_it_by_parent_and_start_time_only()
    {
        var ctl = new FakeControl();
        ctl.Live.Add(Proc(601, 10));
        ctl.Live.Add(Proc(610, 20, parent: 601, name: "helper"));
        // Started before its supposed parent: the parent id belongs to a reused number, not to us.
        ctl.Live.Add(Proc(611, 5, parent: 601, name: "older"));
        await Stop(ctl, [Run("wand", "g", 601, 10)]);
        Assert.Contains(610, ctl.Killed);
        Assert.DoesNotContain(611, ctl.Killed);
    }

    [Fact]
    public async Task A_reused_process_id_is_never_ended()
    {
        // The companion ended by itself and Windows gave its number to something else (other start time, other program).
        var ctl = new FakeControl();
        ctl.Live.Add(Proc(601, 9999, name: "important", path: @"C:\Work\important.exe"));
        var outcome = await Stop(ctl, [Run("wand", "g", 601, 10)]);
        Assert.Empty(ctl.Closed);
        Assert.Empty(ctl.Killed);
        Assert.Equal(0, outcome.Found);
        // Same start time but another program: not ended either.
        var other = new FakeControl();
        other.Live.Add(Proc(601, 10, path: @"C:\Work\other.exe"));
        await Stop(other, [Run("wand", "g", 601, 10)]);
        Assert.Empty(other.Killed);
    }

    [Fact]
    public async Task Without_the_option_or_without_a_start_time_nothing_is_ended()
    {
        var ctl = new FakeControl();
        ctl.Live.Add(Proc(601, 10));
        ctl.Live.Add(Proc(602, 0, name: "blind"));
        var outcome = await Stop(ctl, [Run("wand", "g", 601, 10, close: false), Run("blind", "g", 602, 0)]);
        Assert.Empty(ctl.Closed);
        Assert.Empty(ctl.Killed);
        Assert.Equal(0, outcome.Found);
    }

    [Fact]
    public async Task The_game_and_system_processes_are_never_ended_even_if_a_companion_started_them()
    {
        var ctl = new FakeControl();
        ctl.Live.Add(Proc(601, 10));
        // A game process whose parent is the companion (a launcher that started the game), and a Windows component.
        ctl.Live.Add(Proc(GamePid, 20, parent: 601, name: "game", path: @"D:\Games\game.exe"));
        ctl.Live.Add(Proc(700, 30, parent: 601, name: "helper"));
        ctl.Live.Add(new LiveProcess(701, 601, 31, @"C:\Windows\System32\conhost.exe", 1, "conhost"));
        await Stop(ctl, [Run("wand", "g", 601, 10)], GamePid);
        Assert.DoesNotContain(GamePid, ctl.Killed);
        Assert.DoesNotContain(701, ctl.Killed);
        Assert.Contains(601, ctl.Killed);
        Assert.Contains(700, ctl.Killed);
    }

    [Fact]
    public async Task A_run_that_is_the_games_own_process_is_dropped()
    {
        var ctl = new FakeControl();
        ctl.Live.Add(Proc(GamePid, 10, name: "game", path: @"D:\Games\game.exe"));
        var outcome = await Stop(ctl, [Run("odd", "g", GamePid, 10, image: @"D:\Games\game.exe")], GamePid);
        Assert.Empty(ctl.Killed);
        Assert.Equal(0, outcome.Found);
    }

    [Fact]
    public async Task MegaHub_itself_and_other_sessions_are_protected()
    {
        var ctl = new FakeControl();
        ctl.Live.Add(Proc(Own, 5, name: "MegaHub"));
        ctl.Live.Add(new LiveProcess(601, 0, 10, @"C:\Apps\wand.exe", 2, "wand"));
        await Stop(ctl, [Run("couch", "g", Own, 5, image: @"C:\Apps\MegaHub.exe"), Run("wand", "g", 601, 10)]);
        Assert.Empty(ctl.Killed);
    }
}
