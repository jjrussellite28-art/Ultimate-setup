using CouchHud.Core.Security;
using System.Xml.Linq;
using CouchHud.Core.Guard;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Tests;

public class PolicyGroupsTests
{
    [Fact]
    public void Only_the_protected_keys_are_in_the_helper_groups()
    {
        Assert.True(LockHardening.IsProtectedKey(LockHardening.ExplorerKey));
        Assert.True(LockHardening.IsProtectedKey(LockHardening.DisallowRunKey));
        Assert.True(LockHardening.IsProtectedKey(LockHardening.SystemKey));
        Assert.True(LockHardening.IsProtectedKey(LockHardening.WindowsSystemKey));
        // The user's own keys of the lock are written directly.
        Assert.False(LockHardening.IsProtectedKey(LockHardening.NarratorKey));
        Assert.False(LockHardening.IsProtectedKey(GameBarGuard.CaptureKey));
        Assert.False(LockHardening.IsProtectedKey(LockHardening.ExplorerKey + "X"));

        foreach (var group in PolicyGroups.All)
        {
            Assert.All(group.Changes, c => Assert.True(LockHardening.IsProtectedKey(c.Location), c.Location));
        }
    }

    [Fact]
    public void The_lock_group_is_what_the_lock_would_write_to_protected_keys_on_a_clean_account()
    {
        var all = LockHardening.Changes(new FakeRegistry());
        var protectedOnes = all.Where(c => LockHardening.IsProtectedKey(c.Location)).ToList();
        Assert.Equal(protectedOnes, PolicyGroups.Lock.Changes);
        Assert.Contains(PolicyGroups.Lock.Changes, c => c.Name == "NoRun" && c.Value == "1");
        Assert.Contains(PolicyGroups.Lock.Changes, c => c.Name == "DisableCMD" && c.Value == "2");
        // DisallowRun: our programs under 1..n.
        var list = PolicyGroups.Lock.Changes.Where(c => c.Location == LockHardening.DisallowRunKey).ToList();
        Assert.Equal(LockHardening.BlockedPrograms.Count, list.Count);
        Assert.Equal(Enumerable.Range(1, list.Count).Select(i => i.ToString()), list.Select(c => c.Name));
        // Task Manager is its own group, not part of the lock's.
        Assert.DoesNotContain(PolicyGroups.Lock.Changes, c => c.Name == "DisableTaskMgr");
        Assert.Single(PolicyGroups.TaskManager.Changes);
    }

    [Fact]
    public void The_apply_file_sets_exactly_the_group_and_only_in_the_current_user_hive()
    {
        var text = PolicyGroups.ApplyFile(PolicyGroups.Lock);
        Assert.StartsWith("Windows Registry Editor Version 5.00\r\n", text);
        Assert.Contains(@"[HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer]", text);
        Assert.Contains("\"NoRun\"=dword:00000001", text);
        Assert.Contains("\"DisableCMD\"=dword:00000002", text);
        Assert.Contains(@"[HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\System]", text);
        Assert.Contains("\"1\"=\"cmd.exe\"", text);
        Assert.DoesNotContain("HKEY_LOCAL_MACHINE", text);
        Assert.DoesNotContain("HKEY_CLASSES_ROOT", text);
        Assert.DoesNotContain("DisableTaskMgr", text);
        Assert.Contains("\"DisableTaskMgr\"=dword:00000001", PolicyGroups.ApplyFile(PolicyGroups.TaskManager));
    }

    [Fact]
    public void The_release_file_removes_the_values_and_the_list_and_sets_nothing()
    {
        var text = PolicyGroups.ReleaseFile(PolicyGroups.Lock);
        Assert.Contains(@"[-HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowRun]", text);
        Assert.Contains("\"NoRun\"=-", text);
        Assert.Contains("\"DisableCMD\"=-", text);
        Assert.DoesNotContain("dword:", text);
        // The user's own parent keys stay: only a minus before a key name deletes, and only for the list.
        Assert.Single(text.Split("\r\n"), l => l.StartsWith("[-"));
    }

    [Fact]
    public void Pristine_applied_and_present_follow_the_registry()
    {
        var reg = new FakeRegistry();
        var group = PolicyGroups.Lock;
        Assert.True(PolicyGroups.Pristine(reg, group));
        Assert.False(PolicyGroups.Applied(reg, group));
        Assert.False(PolicyGroups.AnyPresent(reg, group));

        // A value of the user's: the helper does not take the group over.
        reg.WriteDword(LockHardening.ExplorerKey, "NoRun", 1);
        Assert.False(PolicyGroups.Pristine(reg, group));
        Assert.True(PolicyGroups.AnyPresent(reg, group));

        reg = new FakeRegistry();
        reg.WriteString(LockHardening.DisallowRunKey, "1", "notepad.exe");
        Assert.False(PolicyGroups.Pristine(reg, group));

        // Everything set as the group wants it (a task ran): applied, no longer pristine.
        reg = new FakeRegistry();
        foreach (var c in group.Changes)
        {
            if (c.Kind == GuardKind.RegistryDword) reg.WriteDword(c.Location, c.Name, int.Parse(c.Value));
            else reg.WriteString(c.Location, c.Name, c.Value);
        }
        Assert.True(PolicyGroups.Applied(reg, group));
        Assert.False(PolicyGroups.Pristine(reg, group));
        reg.WriteDword(LockHardening.ExplorerKey, "NoRun", 0);
        Assert.False(PolicyGroups.Applied(reg, group));
    }

    [Fact]
    public void Task_and_file_names_are_fixed_per_group_and_direction()
    {
        Assert.Equal(@"CouchHud\Policy-lock-on", PolicyGroups.LegacyTaskName(PolicyGroups.Lock, true));
        Assert.Equal(@"CouchHud\Policy-taskmgr-off", PolicyGroups.LegacyTaskName(PolicyGroups.TaskManager, false));
        Assert.Equal("lock-on.reg", PolicyGroups.FileName(PolicyGroups.Lock, true));
        Assert.Same(PolicyGroups.TaskManager, PolicyGroups.ById("TASKMGR"));
        Assert.Null(PolicyGroups.ById("../evil"));
    }
}

public class PolicyGroupsOwnershipTests
{
    [Fact]
    public void An_unlock_gives_back_the_lock_group_but_leaves_task_manager_to_the_couch_security()
    {
        var released = PolicyGroups.ReleasedWithUnlock(["lock", "taskmgr"]).ToList();
        Assert.Equal([PolicyGroups.Lock], released);
        Assert.Empty(PolicyGroups.ReleasedWithUnlock(["taskmgr"]));
        Assert.Empty(PolicyGroups.ReleasedWithUnlock(["../evil"]));
        Assert.False(PolicyGroups.TaskManager.ReleasedWithUnlock);
        Assert.True(PolicyGroups.Lock.ReleasedWithUnlock);
    }

    [Fact]
    public void The_autologon_task_only_switches_the_winlogon_flag_off()
    {
        var text = PolicyGroups.AutoLogonOffFile();
        Assert.Equal("Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon]\r\n\"AutoAdminLogon\"=\"0\"\r\n", text);
        Assert.DoesNotContain("DefaultPassword", text);
    }

    [Fact]
    public void Only_a_couch_hud_sign_in_is_switched_off_by_the_recovery()
    {
        Assert.True(AutoLogonRecovery.ShouldSwitchOff(AutoLogonState.CouchHud));
        Assert.False(AutoLogonRecovery.ShouldSwitchOff(AutoLogonState.External));
        Assert.False(AutoLogonRecovery.ShouldSwitchOff(AutoLogonState.Off));
        Assert.False(AutoLogonRecovery.ShouldSwitchOff(AutoLogonState.Lost));
    }

    [Fact]
    public void Cleanup_candidates_are_the_protected_policy_keys_and_the_parent_the_import_creates()
    {
        Assert.Contains(LockHardening.ExplorerKey, PolicyGroups.CleanupCandidates);
        Assert.Contains(LockHardening.SystemKey, PolicyGroups.CleanupCandidates);
        Assert.Contains(LockHardening.WindowsSystemKey, PolicyGroups.CleanupCandidates);
        Assert.Contains(@"HKCU\Software\Policies\Microsoft\Windows", PolicyGroups.CleanupCandidates);
        Assert.DoesNotContain(LockHardening.NarratorKey, PolicyGroups.CleanupCandidates);
        // The setup gets the list as an argument: foreign keys are dropped.
        Assert.Equal([LockHardening.ExplorerKey], PolicyGroups.ValidCleanupKeys([LockHardening.ExplorerKey, @"HKCU\Software\Evil", @"HKLM\SOFTWARE\X"]));
        var text = PolicyGroups.CleanupFile([LockHardening.ExplorerKey]);
        Assert.Contains(@"[-HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer]", text);
        Assert.Single(text.Split("\r\n"), l => l.StartsWith("["));
    }

    [Fact]
    public void Missing_candidates_are_the_initial_state()
    {
        var reg = new FakeRegistry();
        reg.WriteDword(LockHardening.SystemKey, "Other", 1);
        var missing = PolicyGroups.MissingCandidates(reg);
        Assert.DoesNotContain(LockHardening.SystemKey, missing);
        Assert.Contains(LockHardening.ExplorerKey, missing);
    }

    [Fact]
    public void Cleanup_runs_only_for_keys_we_created_and_that_are_empty()
    {
        var listed = new[] { LockHardening.ExplorerKey, LockHardening.DisallowRunKey };
        var reg = new FakeRegistry();
        // Gone already: fine.
        Assert.True(PolicyGroups.CleanupAllowed(reg, listed, listed));
        // An empty key (and an empty listed child) left by the import.
        reg.Data[LockHardening.ExplorerKey] = new();
        reg.Data[LockHardening.DisallowRunKey] = new();
        Assert.True(PolicyGroups.CleanupAllowed(reg, listed, listed));
        // The lock did not note the key as created by it (it existed before the first apply): not touched.
        Assert.False(PolicyGroups.CleanupAllowed(reg, listed, [LockHardening.DisallowRunKey]));
        // A value of someone else below: not touched.
        reg.WriteString(LockHardening.ExplorerKey, "NoDrives", "x");
        Assert.False(PolicyGroups.CleanupAllowed(reg, listed, listed));
        reg.DeleteValue(LockHardening.ExplorerKey, "NoDrives");
        // An unlisted subkey: not touched.
        reg.Data[LockHardening.ExplorerKey + @"\Other"] = new();
        Assert.False(PolicyGroups.CleanupAllowed(reg, listed, listed));
        Assert.False(PolicyGroups.CleanupAllowed(reg, [], listed));
    }

    [Fact]
    public void A_new_setup_run_never_forgets_keys_an_earlier_run_noted()
    {
        // The lock created the keys since (they exist now, so they are not "missing"): the earlier note must stay.
        var merged = PolicyGroups.MergeCleanupKeys([LockHardening.DisallowRunKey], [LockHardening.ExplorerKey]);
        Assert.Contains(LockHardening.ExplorerKey, merged);
        Assert.Contains(LockHardening.DisallowRunKey, merged);
        // Only known keys, whatever the argument says.
        Assert.Empty(PolicyGroups.MergeCleanupKeys([@"HKCU\Software\Anything"], [@"HKLM\Software\Else"]));
    }

    [Fact]
    public void The_note_is_out_of_date_when_a_candidate_is_missing_that_it_does_not_list()
    {
        var reg = new FakeRegistry();
        // Everything missing, nothing noted (an older setup saw the keys present): one more setup run is due, though its tasks exist.
        Assert.False(PolicyGroups.CleanupNoteCurrent(reg, []));
        Assert.True(PolicyGroups.CleanupNoteCurrent(reg, PolicyGroups.CleanupCandidates));
        // Keys that exist need no note.
        foreach (var key in PolicyGroups.CleanupCandidates) reg.Data[key] = new();
        Assert.True(PolicyGroups.CleanupNoteCurrent(reg, []));
        reg.Data.Clear();
        var missing = PolicyGroups.MissingCandidates(reg);
        Assert.True(missing.Count > 1);
        Assert.False(PolicyGroups.CleanupNoteCurrent(reg, missing.Skip(1).ToList()));
        Assert.True(PolicyGroups.CleanupNoteCurrent(reg, missing.Select(k => k.ToUpperInvariant()).ToList()));
    }

    [Fact]
    public void Without_a_note_only_empty_keys_of_our_own_paths_go()
    {
        var reg = new FakeRegistry();
        reg.Data[LockHardening.ExplorerKey] = new();
        reg.Data[LockHardening.SystemKey] = new();
        reg.Data[LockHardening.WindowsSystemKey] = new() { ["Other"] = "1" };
        reg.Data[@"HKCU\Software\Policies\Microsoft\Windows"] = new();
        var keys = PolicyGroups.KeysToClean([]);
        LockHardening.RemoveEmpty(reg, keys);
        Assert.False(reg.KeyExists(LockHardening.ExplorerKey));
        Assert.False(reg.KeyExists(LockHardening.SystemKey));
        // Content stays, and the Windows parent key is not ours without a note.
        Assert.True(reg.KeyExists(LockHardening.WindowsSystemKey));
        Assert.True(reg.KeyExists(@"HKCU\Software\Policies\Microsoft\Windows"));
        // A note wins when it names candidates.
        Assert.Equal([LockHardening.ExplorerKey], PolicyGroups.KeysToClean([LockHardening.ExplorerKey]));
    }
}
