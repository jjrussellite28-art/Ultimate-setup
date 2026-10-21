namespace CouchHud.Core.Guard;

/// <summary>Walks up the parent chain of a process; the process list and start times come from the caller (testable).</summary>
public static class ProcessTree
{
    /// <summary>
    /// True when the process itself or one of its ancestors passes <paramref name="isAncestor"/>. The process is tested
    /// first, before any start time is read (a protected or elevated process has none to read, and the game's own pid must
    /// still count). A start time is only needed to climb: a parent that started after its child is a reused pid, and a
    /// start time that cannot be read ends the climb.
    /// </summary>
    public static bool DescendsFrom(int pid, Func<int, bool> isAncestor, Func<int, int?> parentOf, Func<int, DateTime?> startOf, int maxDepth = 12)
    {
        DateTime? childStart = null;
        var started = false;
        for (var depth = 0; depth < maxDepth && pid > 0; depth++)
        {
            if (isAncestor(pid)) return true;
            if (parentOf(pid) is not { } parent || parent <= 0) return false;
            childStart = started ? childStart : startOf(pid);
            started = true;
            if (childStart is not { } cs || startOf(parent) is not { } ps || ps > cs) return false;
            pid = parent;
            childStart = ps;
        }
        return false;
    }
}
