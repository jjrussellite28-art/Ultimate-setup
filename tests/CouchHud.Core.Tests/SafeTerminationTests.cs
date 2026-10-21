using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public class SafeTerminationTests
{
    private const string Win = @"C:\Windows";
    private static readonly string Games = Path.Combine(Path.GetTempPath(), "couchhud-safe", "Games");

    private sealed class FakeControl : IProcessControl
    {
        public int OwnPid => 100;
        public int OwnSession => 1;
        public string WindowsDir => Win;
        public List<LiveProcess> Live { get; } = [];
        public List<int> Closed { get; } = [];
        public List<int> Killed { get; } = [];
        // Called when a close was asked: lets a test end the process.
        public Action<int>? OnClose { get; set; }

        public IReadOnlyList<LiveProcess> List() => Live.ToList();

        public bool RequestClose(int pid, long start)
        {
            Closed.Add(pid);
            OnClose?.Invoke(pid);
            return true;
        }

        public bool Kill(int pid, long start)
        {
            var p = Live.FirstOrDefault(x => x.Pid == pid && x.Start == start);
            if (p is null) return false;
            Killed.Add(pid);
            Live.Remove(p);
            return true;
        }
    }

    private static LiveProcess Game(int pid, long start, int parent = 0, string name = "game") =>
        new(pid, parent, start, Path.Combine(Games, name + ".exe"), 1, name);

    private static Dictionary<int, ProcessStamp> Stamps(params LiveProcess[] ps) => ps.ToDictionary(p => p.Pid, p => new ProcessStamp(p.Path, p.Start));

    private static Task<SafeTermination.Outcome> Quit(FakeControl ctl, Dictionary<int, ProcessStamp> stamps, bool force = false) =>
        SafeTermination.QuitAsync(ctl, stamps, force, TimeSpan.FromMilliseconds(30), poll: TimeSpan.FromMilliseconds(5));

    [Fact]
    public async Task Ends_the_remembered_process()
    {
        var ctl = new FakeControl();
        var game = Game(500, 1000);
        ctl.Live.Add(game);
        var outcome = await Quit(ctl, Stamps(game));
        Assert.Equal([500], ctl.Closed);
        Assert.Equal([500], ctl.Killed);
        Assert.Equal(1, outcome.Found);
    }

    [Fact]
    public async Task Leaves_a_closed_game_alone_when_it_ends_by_itself()
    {
        var ctl = new FakeControl();
        var game = Game(500, 1000);
        ctl.Live.Add(game);
        ctl.OnClose = pid => ctl.Live.RemoveAll(p => p.Pid == pid);
        await Quit(ctl, Stamps(game));
        Assert.Empty(ctl.Killed);
    }

    [Fact]
    public async Task A_reused_pid_is_never_touched()
    {
        // The game ended long ago, its pid now belongs to the user session's sihost (and, in another test, to an ordinary program).
        var ctl = new FakeControl();
        var old = Game(500, 1000);
        ctl.Live.Add(new LiveProcess(500, 4, 9000, Path.Combine(Win, "System32", "sihost.exe"), 1, "sihost"));
        var outcome = await Quit(ctl, Stamps(old));
        Assert.Equal(0, outcome.Found);
        Assert.Empty(ctl.Closed);
        Assert.Empty(ctl.Killed);

        ctl.Live.Clear();
        ctl.Live.Add(new LiveProcess(500, 4, 9000, Path.Combine(Games, "other.exe"), 1, "other"));
        Assert.Equal(0, (await Quit(ctl, Stamps(old))).Found);
        Assert.Empty(ctl.Killed);
    }

    [Fact]
    public async Task Same_start_time_but_other_image_is_not_the_process()
    {
        var ctl = new FakeControl();
        var old = Game(500, 1000);
        ctl.Live.Add(new LiveProcess(500, 4, 1000, Path.Combine(Games, "other.exe"), 1, "other"));
        Assert.Equal(0, (await Quit(ctl, Stamps(old))).Found);
        Assert.Empty(ctl.Killed);
    }

    [Fact]
    public async Task Nothing_is_ended_without_a_known_start_time()
    {
        var ctl = new FakeControl();
        var game = Game(500, 0);
        ctl.Live.Add(game);
        Assert.Equal(0, (await Quit(ctl, Stamps(game))).Found);
        Assert.Empty(ctl.Killed);
    }

    [Fact]
    public async Task A_dead_parent_whose_pid_was_reused_does_not_pull_in_strangers()
    {
        // The game's launcher (pid 300) ended, pid 300 is now explorer. Explorer's children are not the game's.
        var ctl = new FakeControl();
        var game = Game(500, 5000, parent: 300);
        var explorer = new LiveProcess(300, 1, 9000, Path.Combine(Win, "explorer.exe"), 1, "explorer");
        var stranger = new LiveProcess(600, 300, 9500, Path.Combine(Games, "notes.exe"), 1, "notes");
        ctl.Live.AddRange([game, explorer, stranger]);
        // The game itself is remembered, the tree walks down from it only.
        await Quit(ctl, Stamps(game));
        Assert.Equal([500], ctl.Killed);
        Assert.Contains(explorer, ctl.Live);
        Assert.Contains(stranger, ctl.Live);
    }

    [Fact]
    public async Task A_child_older_than_its_parent_is_not_a_child()
    {
        var ctl = new FakeControl();
        var game = Game(500, 5000);
        var fake = new LiveProcess(700, 500, 4000, Path.Combine(Games, "older.exe"), 1, "older"); // started before "its parent": the number was reused
        var real = new LiveProcess(701, 500, 6000, Path.Combine(Games, "helper.exe"), 1, "helper");
        ctl.Live.AddRange([game, fake, real]);
        await Quit(ctl, Stamps(game), force: true);
        Assert.Contains(500, ctl.Killed);
        Assert.Contains(701, ctl.Killed);
        Assert.DoesNotContain(700, ctl.Killed);
    }

    [Fact]
    public async Task System_processes_in_the_tree_are_spared()
    {
        var ctl = new FakeControl();
        var game = Game(500, 5000);
        var cmd = new LiveProcess(510, 500, 5100, Path.Combine(Win, "System32", "cmd.exe"), 1, "cmd");
        var wow = new LiveProcess(511, 500, 5100, Path.Combine(Win, "SysWOW64", "x.exe"), 1, "x");
        var shell = new LiveProcess(512, 500, 5100, Path.Combine(Games, "sihost.exe"), 1, "sihost");
        var other = new LiveProcess(513, 500, 5100, Path.Combine(Games, "foreign.exe"), 2, "foreign"); // other session
        var self = new LiveProcess(100, 500, 5100, Path.Combine(Games, "CouchHud.exe"), 1, "CouchHud");
        var helper = new LiveProcess(514, 500, 5100, Path.Combine(Games, "helper.exe"), 1, "helper");
        ctl.Live.AddRange([game, cmd, wow, shell, other, self, helper]);
        var stamps = Stamps(game, cmd, wow, shell, other, self);
        await Quit(ctl, stamps, force: true);
        Assert.Equal([500, 514], ctl.Killed.Order());
    }

    [Fact]
    public void By_name_never_picks_windows_programs_or_other_sessions()
    {
        var ctl = new FakeControl();
        ctl.Live.Add(new LiveProcess(11, 0, 10, Path.Combine(Win, "System32", "svchost.exe"), 1, "svchost"));
        ctl.Live.Add(new LiveProcess(12, 0, 10, Path.Combine(Win, "System32", "foo.exe"), 1, "foo"));
        ctl.Live.Add(new LiveProcess(13, 0, 10, Path.Combine(Games, "foo.exe"), 2, "foo"));
        ctl.Live.Add(new LiveProcess(14, 0, 10, Path.Combine(Games, "foo.exe"), 1, "foo"));
        Assert.Empty(SafeTermination.StampsByName("svchost", ctl.List(), ctl));
        Assert.Equal([14], SafeTermination.StampsByName("foo", ctl.List(), ctl).Keys);
    }

    [Fact]
    public async Task A_process_that_changed_while_waiting_is_checked_again_before_the_kill()
    {
        // The game ends while we wait; its number is taken by a stranger before the kill step.
        var ctl = new FakeControl();
        var game = Game(500, 1000);
        ctl.Live.Add(game);
        var stranger = new LiveProcess(500, 4, 9000, Path.Combine(Games, "other.exe"), 1, "other");
        ctl.OnClose = pid =>
        {
            ctl.Live.Clear();
            ctl.Live.Add(stranger);
        };
        await Quit(ctl, Stamps(game));
        Assert.Empty(ctl.Killed);
        Assert.Contains(stranger, ctl.Live);
    }

    [Fact]
    public async Task Called_off_ends_nothing_by_force()
    {
        var ctl = new FakeControl();
        var game = Game(500, 1000);
        ctl.Live.Add(game);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await SafeTermination.QuitAsync(ctl, Stamps(game), false, TimeSpan.FromSeconds(5), cts.Token, TimeSpan.FromMilliseconds(5));
        Assert.Empty(ctl.Killed);
    }

    // ---- the tracker: stale entries

    private sealed class FakeProbe : IProcessProbe
    {
        public List<(int, string?)> Processes { get; } = [];
        public Dictionary<int, long> Starts { get; } = [];
        public Dictionary<int, int> Parents { get; } = [];
        public int SteamApp { get; set; }

        public IReadOnlyList<(int Pid, string? Path)> Snapshot() => Processes;

        public int SteamRunningAppId() => SteamApp;

        public IReadOnlyDictionary<int, int> ParentMap() => Parents;

        public IReadOnlyDictionary<int, long> StartTimes() => Starts;
    }

    private static LibraryItem Item(string id, string? dir) => new()
    {
        Id = id,
        Source = id.Split(':')[0],
        Name = id,
        InstallDir = dir,
        Launch = new LaunchTarget(LaunchKind.Url, "x://")
    };

    [Fact]
    public void The_session_carries_image_and_start_of_its_processes()
    {
        var probe = new FakeProbe();
        var tracker = new GameSessionTracker(probe);
        var t0 = DateTimeOffset.Parse("2026-10-17T20:00:00Z");
        tracker.Launched(Item("custom:a", Path.Combine(Games, "A")), t0);
        var exe = Path.Combine(Games, "A", "a.exe");
        probe.Processes.Add((42, exe));
        probe.Starts[42] = 777;
        tracker.Tick(t0.AddSeconds(5));
        var stamp = Assert.Single(tracker.Current!.StampsOrEmpty);
        Assert.Equal(42, stamp.Key);
        Assert.Equal(new ProcessStamp(exe, 777), stamp.Value);
    }

    [Fact]
    public void A_reused_pid_does_not_keep_the_session_alive()
    {
        var probe = new FakeProbe();
        var tracker = new GameSessionTracker(probe);
        var t0 = DateTimeOffset.Parse("2026-10-17T20:00:00Z");
        tracker.Launched(Item("custom:a", Path.Combine(Games, "A")), t0);
        var launcher = Path.Combine(Games, "A", "launcher.exe");
        var real = Path.Combine(Path.GetTempPath(), "couchhud-safe", "jre", "java.exe");
        probe.Processes.Add((41, launcher));
        probe.Processes.Add((42, real));
        probe.Parents[42] = 41;
        probe.Starts[41] = 100;
        probe.Starts[42] = 200;
        tracker.Tick(t0.AddSeconds(5));
        Assert.NotNull(tracker.Current);
        // The launcher ends, the "game" (42) ends too and the number 42 is given to another program with the same image path.
        probe.Processes.Clear();
        probe.Processes.Add((42, real));
        probe.Starts.Clear();
        probe.Starts[42] = 999;
        tracker.Tick(t0.AddSeconds(10));
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void A_child_that_is_older_than_the_supposed_parent_is_not_adopted()
    {
        var probe = new FakeProbe();
        var tracker = new GameSessionTracker(probe);
        var t0 = DateTimeOffset.Parse("2026-10-17T20:00:00Z");
        tracker.Launched(Item("custom:a", Path.Combine(Games, "A")), t0);
        probe.Processes.Add((41, Path.Combine(Games, "A", "a.exe")));
        probe.Processes.Add((50, Path.Combine(Games, "Z", "z.exe")));
        probe.Parents[50] = 41; // parent number reused: 50 is older than "its parent"
        probe.Starts[41] = 500;
        probe.Starts[50] = 100;
        tracker.Tick(t0.AddSeconds(5));
        Assert.Equal([41], tracker.Current!.Pids);
    }

    [Fact]
    public void System_programs_are_never_part_of_a_game()
    {
        var probe = new FakeProbe();
        var tracker = new GameSessionTracker(probe);
        var t0 = DateTimeOffset.Parse("2026-10-17T20:00:00Z");
        // A game folder that points into Windows' own folder: every system process would count as the game.
        var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        tracker.Launched(Item("custom:sys", sys32), t0);
        probe.Processes.Add((44, Path.Combine(sys32, "sihost.exe")));
        tracker.Tick(t0.AddSeconds(5));
        Assert.Null(tracker.Current);

        // And the child of a game that is a Windows program is not followed.
        tracker.Launched(Item("custom:a", Path.Combine(Games, "A")), t0);
        probe.Processes.Add((41, Path.Combine(Games, "A", "a.exe")));
        probe.Processes.Add((45, Path.Combine(sys32, "cmd.exe")));
        probe.Parents[45] = 41;
        tracker.Tick(t0.AddSeconds(6));
        Assert.Equal([41], tracker.Current!.Pids);
    }

    [Fact]
    public void A_stale_steam_value_does_not_keep_a_game_running_for_ever()
    {
        var probe = new FakeProbe { SteamApp = 1245620 };
        var tracker = new GameSessionTracker(probe) { SteamOnlyGrace = TimeSpan.FromSeconds(30) };
        var item = Item("steam:1245620", Path.Combine(Games, "Elden"));
        var t0 = DateTimeOffset.Parse("2026-10-17T20:00:00Z");
        tracker.Launched(item, t0);
        probe.Processes.Add((10, @"C:\Steam\steam.exe"));
        probe.Processes.Add((42, Path.Combine(Games, "Elden", "e.exe")));
        tracker.Tick(t0.AddSeconds(5));
        Assert.NotNull(tracker.Current);
        // The game is gone, steam.exe still carries the old app id.
        probe.Processes.RemoveAll(p => p.Item1 == 42);
        tracker.Tick(t0.AddSeconds(10));
        Assert.NotNull(tracker.Current); // within the grace
        tracker.Tick(t0.AddSeconds(50));
        Assert.Null(tracker.Current);
    }
}
