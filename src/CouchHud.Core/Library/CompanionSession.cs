using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Library;

/// <summary>The window in front: its handle, its process and when that started (FILETIME ticks, 0 when Windows would not tell).</summary>
public sealed record ForegroundInfo(long Window, int Pid, long ProcessStart);

/// <summary>
/// A companion CouchHUD started: exactly this process (id and start time, image when known). Ending it later checks all of it again
/// (<see cref="SafeTermination"/>), so a process id that Windows handed out again is never hit.
/// </summary>
public sealed record CompanionRun(string AppId, string GameId, int Pid, long Start, string? Image, bool CloseWithGame);

/// <summary>A started companion: its process id, and the image and start time read from the still-open process handle.</summary>
/// <param name="Canonical">The real path that was checked and started.</param>
public sealed record CompanionStarted(int Pid, ProcessStamp Stamp, string Canonical);

/// <summary>What the companion start needs from the machine (Win32 in the shell, a fake in the tests).</summary>
public interface ICompanionHost
{
    /// <summary>The real path of the program (links and short names resolved from an open handle), null when it cannot be checked or is no companion.</summary>
    string? Canonical(CompanionStart start);

    /// <summary>A program with this real path runs already: it is not started a second time (and so not ended by us later).</summary>
    bool IsRunning(string canonicalPath);

    /// <summary>
    /// Starts exactly <paramref name="canonical"/> without activating its window (minimized or not); checks the path again on the file it
    /// holds open. Null when it did not start (refused, changed, needs the administrator …).
    /// </summary>
    CompanionStarted? Start(CompanionStart start, string canonical);

    ForegroundInfo? Foreground();

    /// <summary>The process belongs to the running game or descends from it.</summary>
    bool InGameTree(int pid);

    /// <summary>The process is one of the started companions or descends from one (parent id and start times checked); <paramref name="roots"/> maps pid to start time.</summary>
    bool InCompanionTree(int pid, IReadOnlyDictionary<int, long> roots);

    /// <summary>False for a window that is never touched (<see cref="CompanionFocus.Exempt"/>).</summary>
    bool MayMinimize(ForegroundInfo front);

    /// <summary>Minimizes a window without activating it. True when asked.</summary>
    bool MinimizeWindow(long window);

    Task Delay(TimeSpan time, CancellationToken cancel);

    void Log(string line);
}

/// <summary>The one rule behind "a companion must not take the focus from the game".</summary>
public static class CompanionFocus
{
    /// <summary>How long after a start the front window is watched.</summary>
    public static readonly TimeSpan Watch = TimeSpan.FromSeconds(6);

    public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The window in front took the focus from the game: it belongs to the companion or to a process it started (checked by parent id and
    /// start time). The game's own windows and CouchHUD never count, and nothing else does: a stranger that happens to be new is not touched.
    /// Only a minimize follows, never an end.
    /// </summary>
    public static bool Stole(ForegroundInfo front, int ownPid, bool inGameTree, bool inCompanionTree) =>
        front.Pid != ownPid && !inGameTree && inCompanionTree;

    /// <summary>
    /// A window that is never minimized, whatever started it: Windows' own programs (the sign-in, UAC and the like), anything in the Windows
    /// folder, the standard dialogs (class #32770), another Windows session, another integrity level, and a program Windows will not tell about.
    /// </summary>
    public static bool Exempt(string? imagePath, string? windowClass, bool sameSession, bool sameIntegrity, string windowsDir)
    {
        if (string.IsNullOrEmpty(imagePath) || !sameSession || !sameIntegrity) return true;
        if (windowClass == "#32770") return true;
        if (SafeTermination.IsSystemPath(imagePath, windowsDir)) return true;
        return LockedForeground.IsSystem(System.IO.Path.GetFileNameWithoutExtension(imagePath));
    }
}

/// <summary>
/// Starts the companions of a game one after the other, after the game is in front: the game got the foreground from CouchHUD's own
/// permission, a program started afterwards has none (Windows lets a program take the focus only when it was started by the front window),
/// and whatever of the companion shows up in front anyway is minimized again. Nothing is ended here.
/// </summary>
public static class CompanionLauncher
{
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(600);

    /// <param name="stillAllowed">Asked before every single start (the lock, the profile, the rules and the program may have changed since the plan): false ends the whole round.</param>
    public static async Task<IReadOnlyList<CompanionRun>> StartAsync(CompanionPlan plan, string gameId, ICompanionHost host, int ownPid,
        Func<CompanionStart, bool>? stillAllowed = null, CancellationToken cancel = default)
    {
        var runs = new List<CompanionRun>();
        foreach (var start in plan.Start)
        {
            if (cancel.IsCancellationRequested) break;
            var name = PluginSettingRules.LogText(start.App.Name);
            if (stillAllowed is not null && !stillAllowed(start))
            {
                host.Log($"companion {name}: no longer allowed, the rest is not started either");
                break;
            }
            // One real path for the check, the running test and the start.
            var canonical = host.Canonical(start);
            if (canonical is null)
            {
                host.Log($"companion {name}: program not verified, not started");
                continue;
            }
            if (host.IsRunning(canonical))
            {
                host.Log($"companion {name}: already running, left alone");
                continue;
            }
            // Only when the game is in front is there a focus to protect (back in the HUD, a companion may show).
            var before = host.Foreground();
            var protect = before is not null && before.Pid != ownPid && host.InGameTree(before.Pid);
            CompanionStarted? started;
            try
            {
                started = host.Start(start, canonical);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                host.Log($"companion {name}: not started ({ex.GetType().Name})");
                continue;
            }
            if (started is null || started.Pid <= 0)
            {
                host.Log($"companion {name}: not started");
                continue;
            }
            // Ending it later needs a start time, and a process whose image is not the file that was checked (a stub that replaced itself)
            // is not ours to end: it is never ended.
            var sameImage = started.Stamp.Path is not null && string.Equals(started.Stamp.Path, started.Canonical, StringComparison.OrdinalIgnoreCase);
            if (!sameImage) host.Log($"companion {name}: its image is not the checked file, it will not be ended");
            runs.Add(new CompanionRun(start.App.Id, gameId, started.Pid, started.Stamp.Start, started.Stamp.Path, start.Rule.CloseWithGame && started.Stamp.Start > 0 && sameImage));
            if (protect) await KeepGameInFrontAsync(host, ownPid, new Dictionary<int, long> { [started.Pid] = started.Stamp.Start }, name, cancel);
            try
            {
                await host.Delay(Gap, cancel);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return runs;
    }

    private static async Task KeepGameInFrontAsync(ICompanionHost host, int ownPid, IReadOnlyDictionary<int, long> roots, string name, CancellationToken cancel)
    {
        var minimized = new HashSet<long>();
        for (var waited = TimeSpan.Zero; waited < CompanionFocus.Watch; waited += CompanionFocus.Step)
        {
            try
            {
                await host.Delay(CompanionFocus.Step, cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (host.Foreground() is not { } front) continue;
            if (!CompanionFocus.Stole(front, ownPid, host.InGameTree(front.Pid), host.InCompanionTree(front.Pid, roots))) continue;
            if (!host.MayMinimize(front)) continue;
            // The same window again is not asked twice in a row.
            if (!minimized.Add(front.Window)) continue;
            host.MinimizeWindow(front.Window);
            host.Log($"companion {name}: took the focus, minimized again");
        }
    }
}

/// <summary>The companions CouchHUD started and has not ended or let go yet.</summary>
public sealed class CompanionRegistry
{
    private const int MaxRuns = 64;
    private readonly object _gate = new();
    private readonly List<CompanionRun> _runs = [];

    public void Add(IEnumerable<CompanionRun> runs)
    {
        lock (_gate)
        {
            foreach (var run in runs)
            {
                _runs.RemoveAll(r => r.Pid == run.Pid);
                _runs.Add(run);
            }
            while (_runs.Count > MaxRuns) _runs.RemoveAt(0);
        }
    }

    public IReadOnlyList<CompanionRun> All()
    {
        lock (_gate) return _runs.ToList();
    }

    /// <summary>
    /// A game ended: its companions are done. Those with "end with the game" are returned to be ended, unless another game that still runs
    /// wants the same app (then the app stays and now belongs to that game). Runs without the option are only forgotten. The rules are
    /// cleaned here (a hand-edited file may hold a null list or a null entry).
    /// </summary>
    public IReadOnlyList<CompanionRun> Ended(string gameId, IReadOnlyCollection<string> stillRunning, IReadOnlyList<CompanionRule?>? rules)
    {
        var clean = CompanionRules.Sanitize(rules);
        lock (_gate)
        {
            var close = new List<CompanionRun>();
            foreach (var run in _runs.Where(r => r.GameId == gameId).ToList())
            {
                _runs.Remove(run);
                var heir = stillRunning.FirstOrDefault(g => g != gameId && clean.Any(r => r.AppId == run.AppId && CompanionRules.Applies(r, g)));
                if (heir is not null)
                {
                    _runs.Add(run with { GameId = heir });
                    continue;
                }
                if (run.CloseWithGame) close.Add(run);
            }
            return close;
        }
    }

    public void Forget(IEnumerable<int> pids)
    {
        lock (_gate) _runs.RemoveAll(r => pids.Contains(r.Pid));
    }
}

/// <summary>Ends companions at the end of a game: only what CouchHUD started, and only while it is still that process.</summary>
public static class CompanionStop
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Asks the main windows to close, waits, then ends what is still exactly the process we started (pid, start time and image, checked
    /// again on the handle that ends it) and what it started since (parent id and start time). A process of the game itself is left alone
    /// even if a rule named the game's own program.
    /// </summary>
    public static async Task<SafeTermination.Outcome> StopAsync(IReadOnlyList<CompanionRun> runs, IProcessControl ctl, IReadOnlySet<int> gamePids, TimeSpan? grace = null, TimeSpan? poll = null)
    {
        var expected = new Dictionary<int, ProcessStamp>();
        foreach (var run in runs)
        {
            if (!run.CloseWithGame || run.Pid <= 0 || run.Start <= 0 || gamePids.Contains(run.Pid)) continue;
            expected[run.Pid] = new ProcessStamp(run.Image, run.Start);
        }
        if (expected.Count == 0) return new SafeTermination.Outcome(0, 0);
        return await SafeTermination.QuitAsync(new HidingGame(ctl, gamePids), expected, force: false, grace ?? Grace, default, poll);
    }

    /// <summary>The game's processes are not in the list the ending works on: whatever a companion started, the game is never part of it.</summary>
    private sealed class HidingGame(IProcessControl inner, IReadOnlySet<int> gamePids) : IProcessControl
    {
        public int OwnPid => inner.OwnPid;
        public int OwnSession => inner.OwnSession;
        public string WindowsDir => inner.WindowsDir;
        public IReadOnlyList<LiveProcess> List() => inner.List().Where(p => !gamePids.Contains(p.Pid)).ToList();
        public bool RequestClose(int pid, long start) => !gamePids.Contains(pid) && inner.RequestClose(pid, start);
        public bool Kill(int pid, long start) => !gamePids.Contains(pid) && inner.Kill(pid, start);
        public bool Kill(int pid, long start, string? image) => !gamePids.Contains(pid) && inner.Kill(pid, start, image);
    }
}
