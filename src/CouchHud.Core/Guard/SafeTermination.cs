namespace CouchHud.Core.Guard;

/// <summary>
/// What CouchHud noted about a process when it first saw it: the image and the start time. A process id alone is
/// not an identity (Windows hands the number out again), these two together are.
/// </summary>
public sealed record ProcessStamp(string? Path, long Start);

/// <param name="Start">Creation time (FILETIME ticks); 0 when Windows would not tell.</param>
public sealed record LiveProcess(int Pid, int ParentPid, long Start, string? Path, int Session, string Name);

/// <summary>The machine side of ending programs (Win32 in the shell, a fake in the tests).</summary>
public interface IProcessControl
{
    int OwnPid { get; }

    int OwnSession { get; }

    string WindowsDir { get; }

    /// <summary>Every running process of the machine.</summary>
    IReadOnlyList<LiveProcess> List();

    /// <summary>Asks the main window(s) to close; false when there is nothing to ask.</summary>
    bool RequestClose(int pid, long start);

    /// <summary>Ends the process, but only while it still has this start time (checked on the handle that ends it).</summary>
    bool Kill(int pid, long start);
}

/// <summary>
/// Ends programs without ever touching a stranger. A process id that was remembered earlier may belong to something else by now
/// (the game ended, Windows reused the number for a shell component): before anything is closed or killed the process has
/// to show the same start time and image as when it was noted, must live in our own session and must not be part of
/// Windows or CouchHud. The process tree is walked here, by pid and start time of the processes we know, never by the
/// parent id of a dead parent (which may point to an unrelated process).
/// </summary>
public static class SafeTermination
{
    // Ending these logs the user out, breaks the session or kills CouchHud's own UI.
    private static readonly HashSet<string> NeverNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Secure System", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm",
        "fontdrvhost", "sihost", "userinit", "ctfmon", "conhost", "RuntimeBroker", "taskhostw", "dllhost", "audiodg", "WUDFHost",
        "Memory Compression", "SearchHost", "SearchIndexer", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost",
        "SecurityHealthSystray", "SecurityHealthService", "CouchHud", "msedgewebview2", "schtasks", "explorer", "WidgetBoard", "Widgets",
        "ShellHost", "ApplicationFrameHost", "LockApp", "SystemSettings", "backgroundTaskHost", "PickerHost", "WidgetService",
        "CrossDeviceResume", "DefenderSessionHelper", "spoolsv", "lsm", "LogonUI", "consent"
    };

    /// <summary>Part of Windows (System32, SysWOW64, SystemApps, the Windows folder at all).</summary>
    public static bool IsSystemPath(string? path, string windowsDir)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var root = windowsDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Never to be ended: system, shell, other sessions, CouchHud itself.</summary>
    public static bool IsProtected(LiveProcess p, IProcessControl ctl) =>
        p.Pid <= 4 || p.Pid == ctl.OwnPid || p.Session != ctl.OwnSession || p.Session == 0
        || NeverNames.Contains(p.Name) || NeverNames.Contains(Path.GetFileNameWithoutExtension(p.Path ?? ""))
        || IsSystemPath(p.Path, ctl.WindowsDir);

    /// <summary>
    /// The live processes that are still the ones remembered in <paramref name="expected"/> (same pid, same start time, same image),
    /// plus whatever they started since (found by parent pid, but only with a child that is not older than its parent).
    /// Nothing protected is ever in the result, and nothing is trusted whose start time is unknown.
    /// </summary>
    public static IReadOnlyList<LiveProcess> Resolve(IReadOnlyDictionary<int, ProcessStamp> expected, IReadOnlyList<LiveProcess> live, IProcessControl ctl)
    {
        var byPid = live.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        var result = new Dictionary<int, LiveProcess>();
        foreach (var (pid, stamp) in expected)
        {
            if (pid <= 0 || stamp.Start <= 0 || !byPid.TryGetValue(pid, out var p)) continue;
            if (p.Start != stamp.Start) continue;
            if (stamp.Path is not null && !string.Equals(stamp.Path, p.Path, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsProtected(p, ctl)) continue;
            result[pid] = p;
        }
        for (var added = true; added;)
        {
            added = false;
            foreach (var p in live)
            {
                if (result.ContainsKey(p.Pid) || p.Start <= 0 || p.ParentPid == p.Pid) continue;
                // A child never starts before its parent: otherwise the parent id belongs to a reused pid.
                if (!result.TryGetValue(p.ParentPid, out var parent) || p.Start < parent.Start) continue;
                if (IsProtected(p, ctl)) continue;
                result[p.Pid] = p;
                added = true;
            }
        }
        return result.Values.ToList();
    }

    /// <summary>Stamps of everything running right now with that image name in our session (by-name ending from the task list).</summary>
    public static Dictionary<int, ProcessStamp> StampsByName(string name, IReadOnlyList<LiveProcess> live, IProcessControl ctl) =>
        live.Where(p => p.Start > 0 && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && !IsProtected(p, ctl))
            .ToDictionary(p => p.Pid, p => new ProcessStamp(p.Path, p.Start));

    /// <param name="Found">How many processes were still the remembered ones (0: nothing left to end).</param>
    /// <param name="Killed">How many had to be ended by force.</param>
    public sealed record Outcome(int Found, int Killed);

    /// <summary>
    /// Asks politely (unless <paramref name="force"/>), waits up to <paramref name="grace"/>, then ends what is still the same process.
    /// The identity is checked again right before each step, with a fresh process list.
    /// </summary>
    public static async Task<Outcome> QuitAsync(IProcessControl ctl, IReadOnlyDictionary<int, ProcessStamp> expected, bool force, TimeSpan grace,
        CancellationToken cancel = default, TimeSpan? poll = null)
    {
        var targets = Resolve(expected, ctl.List(), ctl);
        if (targets.Count == 0) return new Outcome(0, 0);
        if (!force)
        {
            foreach (var p in targets) ctl.RequestClose(p.Pid, p.Start);
            var deadline = DateTime.UtcNow + grace;
            var step = poll ?? TimeSpan.FromMilliseconds(250);
            while (DateTime.UtcNow < deadline && Resolve(expected, ctl.List(), ctl).Count > 0)
            {
                // Called off (the parents added time while the game was closing): nothing is ended by force.
                if (cancel.IsCancellationRequested) return new Outcome(targets.Count, 0);
                await Task.Delay(step, CancellationToken.None).ConfigureAwait(false);
            }
        }
        if (cancel.IsCancellationRequested) return new Outcome(targets.Count, 0);
        // Fresh look: the list from the start may be old, a process that ended meanwhile must not be hit through its reused number.
        var killed = 0;
        foreach (var p in Resolve(expected, ctl.List(), ctl))
        {
            if (ctl.Kill(p.Pid, p.Start)) killed++;
        }
        return new Outcome(targets.Count, killed);
    }
}
