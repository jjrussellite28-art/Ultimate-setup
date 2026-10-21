using CouchHud.Core.Platform;

namespace CouchHud.Core.Guard;

/// <summary>
/// What the lock's Windows policies look like from the outside, and the one rule around them: <b>a helper task is only deleted when
/// everything it can release is verifiably released</b> (the values gone from the registry, the mark file empty). The tasks are the
/// only way for a normal program to remove the protected Policies values again (see <see cref="PolicyGroups"/>); deleted too early,
/// NoRun, NoControlPanel, DisableCMD and the DisallowRun list stay for good. Everything here works on the fixed values of
/// <see cref="PolicyGroups.All"/> only, and on our programs in the DisallowRun list: a value that is not exactly ours is not ours.
/// </summary>
public static class PolicyRelease
{
    /// <summary>The group's values that still have exactly the value the lock gave them, plus our programs in its lists.</summary>
    public static IReadOnlyList<string> OursPresent(IRegistry registry, PolicyGroup group)
    {
        var found = new List<string>();
        foreach (var c in group.Dwords)
        {
            if (registry.ReadDword(c.Location, c.Name)?.ToString(System.Globalization.CultureInfo.InvariantCulture) == c.Value) found.Add($"{c.Location}\\{c.Name}");
        }
        foreach (var key in group.ListKeys)
        {
            foreach (var (name, value) in registry.ReadValues(key))
            {
                if (IsOurProgram(value)) found.Add($"{key}\\{name}");
            }
        }
        return found;
    }

    private static bool IsOurProgram(string value) => LockHardening.BlockedPrograms.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The groups that still have some of our values in the registry.</summary>
    public static IReadOnlyList<PolicyGroup> GroupsWithOurs(IRegistry registry) =>
        PolicyGroups.All.Where(g => OursPresent(registry, g).Count > 0).ToList();

    public static bool AnyOurs(IRegistry registry) => GroupsWithOurs(registry).Count > 0;

    /// <summary>
    /// For an administrator process (it may write the Policies keys itself): removes exactly the values of <see cref="OursPresent"/>,
    /// nothing else. The DisallowRun list loses only our programs and goes when it is empty then. Returns what is still there
    /// afterwards (empty: released).
    /// </summary>
    public static IReadOnlyList<string> RemoveOurs(IRegistry registry, IEnumerable<PolicyGroup>? groups = null)
    {
        var still = new List<string>();
        foreach (var group in groups ?? PolicyGroups.All)
        {
            foreach (var c in group.Dwords)
            {
                if (registry.ReadDword(c.Location, c.Name)?.ToString(System.Globalization.CultureInfo.InvariantCulture) != c.Value) continue;
                Try(() => registry.DeleteValue(c.Location, c.Name));
            }
            foreach (var key in group.ListKeys)
            {
                foreach (var (name, value) in registry.ReadValues(key))
                {
                    if (IsOurProgram(value)) Try(() => registry.DeleteValue(key, name));
                }
                if (registry.ReadValues(key).Count == 0) Try(() => registry.DeleteKeyIfEmpty(key));
            }
            still.AddRange(OursPresent(registry, group));
        }
        return still;
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            // Stays in the registry; the check afterwards says so.
        }
    }

    /// <summary>The outcome of a release that was checked in the registry afterwards.</summary>
    public sealed record Verdict(bool Released, IReadOnlyList<string> Remaining, IReadOnlyList<string> StillMarked)
    {
        public static Verdict Check(IRegistry registry, IEnumerable<string> marked)
        {
            var remaining = PolicyGroups.All.SelectMany(g => OursPresent(registry, g)).ToList();
            var stillMarked = marked.ToList();
            return new Verdict(remaining.Count == 0 && stillMarked.Count == 0, remaining, stillMarked);
        }
    }

    /// <summary>
    /// The rule: may the helper tasks be deleted now? Only when nothing of ours is in the registry and no group is marked as set
    /// by the helper. Unreadable counts as "no" (the caller passes null for a check that failed).
    /// </summary>
    public static bool TasksMayGo(Verdict? verdict) => verdict is { Released: true };

    /// <summary>
    /// Names of the tasks (below the user's folder) that release or clean up policies: the ones the rule protects. The service tasks
    /// (Couch-/PC-…) do not release anything and may go at any time.
    /// </summary>
    public static bool IsPolicyTask(string taskName)
    {
        var leaf = taskName.Trim('\\').Split('\\')[^1];
        return leaf.StartsWith("Policy-", StringComparison.OrdinalIgnoreCase) || leaf.Equals("Autologon-off", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Which of a user's tasks may be deleted now. A <paramref name="replacing"/> setup (it creates the policy tasks again in place)
    /// deletes service tasks only; the way back deletes everything, but only when <paramref name="released"/> says that the registry
    /// was checked and nothing of the lock is left. Otherwise nothing at all goes.
    /// </summary>
    public static IReadOnlyList<string> TasksToDelete(IEnumerable<string> tasks, bool replacing, bool released) =>
        replacing ? tasks.Where(t => !IsPolicyTask(t)).ToList() : released ? tasks.ToList() : [];

    /// <summary>
    /// Policies with our values that nothing explains: no group marked as set by the helper, no lock that is meant to be on now
    /// (<paramref name="expectedOn"/>: ids of the groups that are supposed to be set right now). These are the leftovers of a way back
    /// that deleted the tasks too early, or of a crash with the tasks gone.
    /// </summary>
    public static IReadOnlyList<PolicyGroup> Stuck(IRegistry registry, IReadOnlyCollection<string> marked, IReadOnlyCollection<string> expectedOn) =>
        PolicyGroups.All
            .Where(g => !marked.Contains(g.Id, StringComparer.OrdinalIgnoreCase) && !expectedOn.Contains(g.Id, StringComparer.OrdinalIgnoreCase))
            .Where(g => OursPresent(registry, g).Count > 0)
            .ToList();
}
