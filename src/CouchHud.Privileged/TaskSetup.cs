using System.ServiceProcess;
using CouchHud.Core.Guard;
using CouchHud.Core.Startup;

namespace CouchHud.Privileged;

/// <summary>
/// The administrator side of the helper tasks: replaces the tasks of one user (<c>CouchHud\&lt;SID&gt;\…</c>) with one stop task per
/// service and a start task for services that normally run, releases the lock's policies in a user's hive before a task goes, and
/// removes every task on uninstall. Each task runs nothing but <c>System32\sc.exe stop|start &lt;service&gt;</c> (or a fixed reg.exe import).
/// </summary>
public sealed class TaskSetup(ITaskScheduler tasks, IScm scm, PolicyInstaller installer, IEventSink events, Func<bool>? installIsProtected = null)
{
    public const string Folder = "CouchHud";

    private static bool ValidServiceName(string name) =>
        name.Length is > 0 and <= 80 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') && GamingServices.IsAllowed(name);

    /// <summary>
    /// Runs elevated: replaces the service tasks of this user (and the machine-wide ones of older versions) with one stop task per
    /// service and a start task for services that normally run. Tasks of other users stay.
    /// </summary>
    public int RunSetup(string sid, string serviceList, bool policies, string missingKeys = "-")
    {
        if (!PolicyGroups.IsUserSid(sid) || !ProfileExists(sid)) return SetupExit.BadInput;
        if (!(installIsProtected ?? InstallProtection.IsProtected)()) return SetupExit.UnsafeFolder;
        var services = serviceList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (services.Any(s => !ValidServiceName(s))) return SetupExit.BadInput;

        // Start over: this user's tasks and the tasks of services that were deselected go away. The machine-wide tasks of older
        // versions stay until this setup has worked: other users may still depend on them, and a failed setup must not leave
        // the PC without any helper.
        var existing = tasks.List().ToList();
        var mine = existing.Where(t => PolicyGroups.IsTaskOf(t, sid)).ToList();
        // The rule: a helper task goes only when everything it can release is verifiably released. The way back therefore releases
        // first, directly in the user's hive (this process is elevated anyway), and checks the registry; if anything of ours stays,
        // no task is deleted and the step reports the failure. A setup (policies = true) deletes no policy task at all: /Create /F
        // replaces them in place, so there is no moment without a task that can release.
        var released = policies || installer.ReleaseDirect(sid);
        if (!released) return SetupExit.PoliciesStay;
        foreach (var task in PolicyRelease.TasksToDelete(mine, replacing: policies, released)) tasks.Delete(task);

        var result = CreateTasks(sid, services, policies, missingKeys);
        if (result == SetupExit.Ok) RemoveLegacyTasks(existing, sid);
        return result;
    }

    /// <summary>
    /// The machine-wide tasks of older versions go after a setup that worked, with their files, unless an account that is signed in
    /// still has our values (the legacy tasks are what could release them, and they are shared by all accounts).
    /// </summary>
    private void RemoveLegacyTasks(IEnumerable<string> existing, string sid)
    {
        var legacy = existing.Where(PolicyGroups.IsLegacyTask).ToList();
        if (legacy.Count == 0)
        {
            installer.RemoveLegacyFiles(installer.PolicyDirectory());
            return;
        }
        var others = LoadedHivesWithOurs().Where(h => !h.Equals(sid, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count > 0)
        {
            events.Error($"[setup] legacy tasks stay: the policies of {string.Join(", ", others)} could still need them");
            return;
        }
        foreach (var task in legacy) tasks.Delete(task);
        installer.RemoveLegacyFiles(installer.PolicyDirectory());
    }

    /// <summary>The signed-in accounts (loaded hives) that have some of the lock's policy values.</summary>
    internal static IEnumerable<string> LoadedHivesWithOurs()
    {
        foreach (var name in Microsoft.Win32.Registry.Users.GetSubKeyNames())
        {
            if (name.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase) || !PolicyGroups.IsUserSid(name)) continue;
            bool ours;
            try
            {
                ours = PolicyRelease.AnyOurs(new WindowsRegistry(name));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                ours = true;
            }
            if (ours) yield return name;
        }
    }

    /// <summary>
    /// Elevated: only the release of the lock's policies in this user's account, directly and checked (see
    /// <see cref="PolicyInstaller.ReleaseDirect"/>). No task is touched.
    /// </summary>
    public int RunCleanup(string sid)
    {
        if (!PolicyGroups.IsUserSid(sid) || !ProfileExists(sid)) return SetupExit.BadInput;
        return installer.ReleaseDirect(sid) ? SetupExit.Ok : SetupExit.PoliciesStay;
    }

    private int CreateTasks(string sid, List<string> services, bool policies, string missingKeys)
    {

        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var sc = Path.Combine(system32, "sc.exe");
        var workDir = installer.UserPolicyDirectory(sid);
        foreach (var name in services)
        {
            var code = installer.CreateSystemTask(sid, workDir, PolicyGroups.ServiceTaskName(sid, couch: true, name), sc, $"stop {name}");
            if (code != 0) return SetupExit.TaskFailed;
            // Back in PC mode only what normally runs anyway is started again.
            if (scm.GetStartType(name) == ServiceStartMode.Automatic)
            {
                code = installer.CreateSystemTask(sid, workDir, PolicyGroups.ServiceTaskName(sid, couch: false, name), sc, $"start {name}");
                if (code != 0) return SetupExit.TaskFailed;
            }
        }
        if (!policies)
        {
            installer.RemoveFiles(sid);
            return SetupExit.Ok;
        }
        return installer.Install(sid, missingKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>The SID belongs to a profile of this PC: nobody can have the tasks set up for a SID that has no account here.</summary>
    private static bool ProfileExists(string sid)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
        return key is not null;
    }

    /// <summary>
    /// Uninstall: every CouchHud task of every user goes, the legacy ones included.
    /// <paramref name="keepPolicyTasksOf"/>: accounts whose lock policies could not be released (checked in the registry): their policy
    /// tasks and the files they run stay (the rule: a task only goes when everything it can release is released).
    /// </summary>
    public void RemoveAllTasks(IReadOnlySet<string>? keepPolicyTasksOf = null)
    {
        var kept = false;
        foreach (var task in tasks.List())
        {
            if (keepPolicyTasksOf is { Count: > 0 } && PolicyRelease.IsPolicyTask(task) && TaskOwner(task) is { } owner && keepPolicyTasksOf.Contains(owner))
            {
                events.Error($"[uninstall] task {task} stays: the policies of {owner} are not released");
                kept = true;
                continue;
            }
            tasks.Delete(task);
        }
        if (!kept) installer.RemoveAllFiles();
    }

    /// <summary>The SID in the folder of a user's task (<c>CouchHud\&lt;SID&gt;\Name</c>); null for the legacy tasks.</summary>
    private static string? TaskOwner(string task)
    {
        var parts = task.Trim('\\').Split('\\');
        return parts.Length == 3 && PolicyGroups.IsUserSid(parts[1]) ? parts[1] : null;
    }
}
