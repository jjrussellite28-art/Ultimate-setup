using System.Text;
using CouchHud.Core.Guard;
using CouchHud.Core.Startup;

namespace CouchHud.Privileged;

/// <summary>The Run entry that releases the lock's policies after the next Windows sign-in (written by the lock, removed with it).</summary>
public static class ReleaseEntry
{
    public const string KeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "CouchHudReleaseLockPolicies";
}

/// <summary>
/// The administrator side of the policy helper (see the shell's PolicyHelper for the user side): writes the fixed .reg files of
/// one user into the program folder and creates the tasks that import them (SYSTEM, startable by that user), and takes the lock's
/// values out of a user's hive directly (HKEY_USERS\&lt;SID&gt;).
/// </summary>
public sealed class PolicyInstaller(ITaskScheduler tasks, IEventSink events, string programDirectory)
{
    /// <summary>
    /// Elevated: takes the lock's policy values out of the user's hive directly (HKEY_USERS\&lt;SID&gt;), as an additional safety next to
    /// the release tasks. Only the fixed values of <see cref="PolicyGroups.All"/> with exactly the value the lock gave them and only
    /// our programs in the DisallowRun list (see <see cref="PolicyRelease.RemoveOurs"/>); then the empty keys the lock created and the
    /// Run entry of the release, but only when nothing of ours is left. True when the registry was checked afterwards and is clear.
    /// </summary>
    public bool ReleaseDirect(string sid)
    {
        try
        {
            if (!PolicyGroups.IsUserSid(sid)) return false;
            using (var hive = Microsoft.Win32.Registry.Users.OpenSubKey(sid))
            {
                // Not loaded: the user is not signed in, nothing could be checked (the caller does not delete the tasks then).
                if (hive is null)
                {
                    events.Error($"[setup] hive of {sid} not loaded, policies cannot be checked");
                    return false;
                }
            }
            var registry = new WindowsRegistry(sid);
            var left = PolicyRelease.RemoveOurs(registry);
            if (left.Count > 0)
            {
                events.Error($"[setup] policies stay in {sid}: {string.Join(", ", left)}");
                return false;
            }
            LockHardening.RemoveEmpty(registry, PolicyGroups.KeysToClean(ReadNotedKeys(UserPolicyDirectory(sid))));
            registry.DeleteValue(ReleaseEntry.KeyPath, ReleaseEntry.ValueName);
            events.Info($"[setup] policies of {sid} released directly");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            events.Error($"[setup] policies of {sid}: {ex.Message}");
            return false;
        }
    }

    /// <summary>policy\ next to the program: the fixed .reg files the tasks import.</summary>
    public string PolicyDirectory() => Path.Combine(programDirectory, "policy");

    /// <summary>The files of one user's tasks: policy\&lt;SID&gt;.</summary>
    public string UserPolicyDirectory(string sid) => Path.Combine(PolicyDirectory(), PolicyGroups.IsUserSid(sid) ? sid : throw new ArgumentException("not a user SID", nameof(sid)));

    /// <summary>The Policies keys the setup noted as missing (the lock may remove them again when empty).</summary>
    public static IReadOnlyList<string> ReadNotedKeys(string dir)
    {
        try
        {
            var file = Path.Combine(dir, PolicyGroups.CleanupKeysFileName);
            return File.Exists(file) ? File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Runs elevated (from the setup helper, after the protected folder was checked): writes the fixed .reg files for
    /// <paramref name="sid"/> into the program folder and creates the tasks (SYSTEM, startable by that user). Returns a
    /// <see cref="SetupExit"/> code.
    /// </summary>
    public int Install(string sid, IReadOnlyCollection<string>? missingKeys = null)
    {
        try
        {
            var root = PolicyDirectory();
            var dir = UserPolicyDirectory(sid);
            Directory.CreateDirectory(dir);
            // The files of the legacy tasks stay: those tasks release with them, and only go (with their files, see
            // RemoveLegacyFiles) once the new setup works and nothing of the lock is left in an account.
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var reg = Path.Combine(system32, "reg.exe");
            var hive = PolicyGroups.UserHive(sid);
            foreach (var group in PolicyGroups.All)
            {
                foreach (var apply in new[] { true, false })
                {
                    var file = Path.Combine(dir, PolicyGroups.FileName(group, apply));
                    // UTF-16 with BOM: what reg.exe reads for sure.
                    File.WriteAllText(file, apply ? PolicyGroups.ApplyFile(group, hive) : PolicyGroups.ReleaseFile(group, hive), new UnicodeEncoding(false, true));
                    var code = CreateSystemTask(sid, dir, PolicyGroups.TaskName(group, apply, sid), reg, PolicyGroups.ImportArguments(file));
                    if (code != 0) return SetupExit.TaskFailed;
                }
            }
            // The recovery's switch for the automatic sign-in: a fixed import of one HKLM value (the same file for every user).
            var autoLogon = Path.Combine(root, PolicyGroups.AutoLogonOffFileName);
            File.WriteAllText(autoLogon, PolicyGroups.AutoLogonOffFile(), new UnicodeEncoding(false, true));
            if (CreateSystemTask(sid, dir, PolicyGroups.AutoLogonOffTaskName(sid), reg, PolicyGroups.ImportArguments(autoLogon)) != 0) return SetupExit.TaskFailed;
            // The initial state: Policies keys that do not exist yet may be removed again when empty, after the last release.
            var keysFile = Path.Combine(dir, PolicyGroups.CleanupKeysFileName);
            // Every run notes the initial state again, but never forgets what an earlier run noted (the lock may have created those keys since).
            var keys = PolicyGroups.MergeCleanupKeys(missingKeys ?? [], ReadNotedKeys(dir));
            if (keys.Count == 0)
            {
                if (File.Exists(keysFile)) File.Delete(keysFile);
                var stale = Path.Combine(dir, PolicyGroups.CleanupFileName);
                if (File.Exists(stale)) File.Delete(stale);
                // No key to clean up any more: a cleanup task from an earlier setup has nothing left to release (it only deletes empty keys).
                tasks.Delete(PolicyGroups.CleanupTaskName(sid));
            }
            else
            {
                var cleanup = Path.Combine(dir, PolicyGroups.CleanupFileName);
                File.WriteAllText(cleanup, PolicyGroups.CleanupFile(keys, hive), new UnicodeEncoding(false, true));
                File.WriteAllLines(keysFile, keys);
                if (CreateSystemTask(sid, dir, PolicyGroups.CleanupTaskName(sid), reg, PolicyGroups.ImportArguments(cleanup)) != 0) return SetupExit.TaskFailed;
            }
            return SetupExit.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            events.Error($"[setup] policy files: {ex.Message}");
            return SetupExit.TaskFailed;
        }
    }

    /// <summary>
    /// Elevated: one task, run as SYSTEM, that starts only <c>program arguments</c>, and that <paramref name="sid"/> may start. The
    /// definition goes through a file in <paramref name="workDir"/> (a folder only administrators write). Returns the schtasks exit code.
    /// </summary>
    public int CreateSystemTask(string sid, string workDir, string taskName, string program, string arguments)
    {
        Directory.CreateDirectory(workDir);
        var xml = Path.Combine(workDir, "task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(xml, PolicyGroups.SystemTaskXml(sid, program, arguments), new UnicodeEncoding(false, true));
            var code = tasks.Create(taskName, xml);
            if (code != 0)
            {
                events.Error($"[setup] task {taskName}: schtasks {code}");
                return code;
            }
            // Whether schtasks takes the <SecurityDescriptor> of the XML as the task's ACL is not something to rely on: ask the
            // Task Scheduler itself and set it when the user's start right is not there.
            return tasks.EnsureStartRight(taskName, sid) ? 0 : -2;
        }
        finally
        {
            try { File.Delete(xml); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Earlier versions kept HKCU-based files directly in policy\ (for tasks of the signed-in user); only the sign-in switch stays.
    /// Call only after the legacy tasks that run these files are deleted (their release needs them).
    /// </summary>
    public void RemoveLegacyFiles(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root))
        {
            if (Path.GetFileName(file).Equals(PolicyGroups.AutoLogonOffFileName, StringComparison.OrdinalIgnoreCase)) continue;
            File.Delete(file);
        }
    }

    /// <summary>
    /// Elevated: the files of this user go; when no other user's files are left, the shared one and the folder go too (the tasks
    /// are deleted with all the others of the user's folder).
    /// </summary>
    public void RemoveFiles(string sid)
    {
        try
        {
            var root = PolicyDirectory();
            if (!Directory.Exists(root)) return;
            var mine = UserPolicyDirectory(sid);
            if (Directory.Exists(mine)) Directory.Delete(mine, recursive: true);
            if (!Directory.EnumerateDirectories(root).Any()) Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            events.Error($"[setup] policy files not removed: {ex.Message}");
        }
    }

    /// <summary>
    /// The service's task migration: only this user's folder (policy\&lt;SID&gt;) goes. The shared files directly in policy\ stay, the
    /// sign-in switch's among them (the Autologon-off tasks stay as a reserve and import it). A folder that is a reparse point is left
    /// alone. True when the folder is gone afterwards.
    /// </summary>
    public bool RemoveUserFiles(string sid)
    {
        try
        {
            var mine = new DirectoryInfo(UserPolicyDirectory(sid));
            if (!mine.Exists) return true;
            if (mine.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                events.Error($"[migrate] policy files of {sid} stay: the folder is a reparse point");
                return false;
            }
            mine.Delete(recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            events.Error($"[migrate] policy files of {sid} not removed: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>Uninstall (elevated): all files of all users.</summary>
    public void RemoveAllFiles()
    {
        try
        {
            var dir = PolicyDirectory();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            events.Error($"[setup] policy files not removed: {ex.Message}");
        }
    }
}
