namespace CouchHud.Core.Security;

/// <summary>
/// Counts consecutive unclean ends of CouchHud as the shell. Every start leaves a "running"
/// marker that a clean end (quit, sign-out, shutdown, restart) removes again. A marker that is
/// still there at the next start means the last run was killed or crashed. Three of those in a
/// row, with no healthy run in between, are a crash loop. A boot cycle takes minutes, so the unclean
/// ends only have to lie within a few hours (<see cref="Window"/>) of each other; older ones are forgotten, and so are
/// entries with a time that cannot be (in the future) or a file that makes no sense. A healthy run (it ran for a
/// while, or ended cleanly) resets the count. The files are plain user files: a crash loop only ever leads to the lock
/// screen first (see Recovery), so planting them gains nothing.
/// </summary>
public sealed class CrashTracker(string dir, int threshold = 3, Func<DateTimeOffset>? now = null, TimeSpan? window = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Unclean ends further apart than this do not add up.</summary>
    public TimeSpan Window { get; } = window ?? TimeSpan.FromHours(6);

    private string MarkerFile => Path.Combine(dir, "running.marker");

    private string CountFile => Path.Combine(dir, "unclean.count");

    /// <summary>The unclean ends so far (unix ms), only the plausible ones: not in the future, not older than the window.</summary>
    private List<long> Entries()
    {
        try
        {
            if (!File.Exists(CountFile)) return [];
            var nowMs = _now().ToUnixTimeMilliseconds();
            var oldest = nowMs - (long)Window.TotalMilliseconds;
            var list = new List<long>();
            foreach (var line in File.ReadAllLines(CountFile).Take(16))
            {
                if (!long.TryParse(line, out var value)) continue;
                // The older format was a bare counter: those count as "just now".
                if (value is >= 0 and < 1_000_000)
                {
                    for (var i = 0; i < Math.Min(value, 16); i++) list.Add(nowMs);
                    continue;
                }
                if (value > nowMs + 60_000 || value < oldest) continue;
                list.Add(value);
            }
            return list;
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>Unclean starts in a row so far.</summary>
    public int Consecutive() => Entries().Count;

    /// <summary>
    /// Call once per start: counts the previous run if it ended unclean and writes the marker for this one.
    /// True when that makes a crash loop (the counter is reset then).
    /// </summary>
    public bool RegisterStart()
    {
        Directory.CreateDirectory(dir);
        var entries = Entries();
        if (File.Exists(MarkerFile)) entries.Add(_now().ToUnixTimeMilliseconds());
        if (entries.Count >= threshold)
        {
            File.Delete(CountFile);
            File.Delete(MarkerFile);
            return true;
        }
        if (entries.Count > 0) File.WriteAllLines(CountFile, entries.Select(e => e.ToString()));
        else File.Delete(CountFile);
        File.WriteAllText(MarkerFile, _now().ToUnixTimeMilliseconds().ToString());
        return false;
    }

    /// <summary>
    /// The crash loop was found but the recovery could not run (Windows would not lock): the counting is given back, so that
    /// the next start, with this run as one more unclean end, is a crash loop again and the recovery tries once more.
    /// </summary>
    public void Rearm()
    {
        Directory.CreateDirectory(dir);
        var nowMs = _now().ToUnixTimeMilliseconds();
        File.WriteAllLines(CountFile, Enumerable.Repeat(nowMs.ToString(), Math.Max(0, threshold - 1)));
        File.WriteAllText(MarkerFile, nowMs.ToString());
    }

    /// <summary>Ended on purpose: this run is no crash, and a run that ends cleanly was healthy.</summary>
    public void MarkClean()
    {
        File.Delete(MarkerFile);
        File.Delete(CountFile);
    }

    /// <summary>Running fine for a while: earlier unclean starts no longer count.</summary>
    public void MarkHealthy() => File.Delete(CountFile);
}

/// <summary>
/// The guard restarts CouchHud when it vanishes. If that keeps happening, restarting is the
/// problem: this counts the restarts, three within ten minutes are too many.
/// </summary>
public sealed class GuardRestarts(string dir, Func<DateTimeOffset> now, int max = 3, TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(10);

    private string RestartsFile => Path.Combine(dir, "guard-restarts.log");

    public GuardRestarts(string dir) : this(dir, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>Records a restart; false when the limit is reached (the restart must not happen).</summary>
    public bool TryRegister()
    {
        Directory.CreateDirectory(dir);
        var limit = now().ToUnixTimeMilliseconds() - (long)_window.TotalMilliseconds;
        var recent = (File.Exists(RestartsFile) ? File.ReadAllLines(RestartsFile) : [])
            .Select(l => long.TryParse(l, out var t) ? t : 0).Where(t => t > limit).ToList();
        if (recent.Count >= max)
        {
            File.Delete(RestartsFile);
            return false;
        }
        recent.Add(now().ToUnixTimeMilliseconds());
        File.WriteAllLines(RestartsFile, recent.Select(t => t.ToString()));
        return true;
    }

    /// <summary>The limit was reached but the recovery could not run: the restarts count again, the next one reaches the limit again.</summary>
    public void Rearm()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllLines(RestartsFile, Enumerable.Repeat(now().ToUnixTimeMilliseconds().ToString(), max));
    }

    /// <summary>A healthy run: the restarts before it no longer count.</summary>
    public void Reset() => File.Delete(RestartsFile);
}

/// <summary>What the shell code does in a few situations, as plain decisions.</summary>
public static class ShellDecisions
{
    /// <summary>Lock and policy only belong to a CouchHud that really is the shell; never touch a light or dev session.</summary>
    public static bool EmergencyMayLock(bool installedAsShell) => installedAsShell;

    /// <summary>Quitting must not leave a screen without any shell.</summary>
    public static bool StartDesktopOnQuit(bool desktopRunning) => !desktopRunning;
}
