using CouchHud.Core.Guard;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Tests;

/// <summary>ApplyLockPolicies, ReleaseLockPolicies and CleanupPolicyKeys through the dispatcher, on in-memory hives.</summary>
public class PolicyOperationsTests
{
    private static readonly string Grant = Convert.ToBase64String(new byte[32]);

    [Fact]
    public async Task Apply_sets_the_fixed_values_in_the_callers_hive_only_and_notes_them()
    {
        using var ops = new OpsHarness();
        var response = await ops.Call<LockPoliciesData>(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock", "taskmgr"]));
        Assert.True(response.Ok, response.Code);
        Assert.Equal(["lock", "taskmgr"], response.Data!.Groups);

        var mine = ops.Hives.For(Callers.UserSid);
        Assert.True(PolicyGroups.Applied(mine, PolicyGroups.Lock));
        Assert.True(PolicyGroups.Applied(mine, PolicyGroups.TaskManager));
        Assert.Equal(6, mine.ReadValues(LockHardening.DisallowRunKey).Count);
        // The other account's hive was never opened, let alone written.
        Assert.Empty(ops.Hives.For(Callers.OtherSid).Data);
        Assert.DoesNotContain(Callers.OtherSid, ops.Hives.Opened);

        var account = ops.State.Accounts[Callers.UserSid];
        Assert.Equal(["lock", "taskmgr"], account.Groups);
        // Every candidate key was missing before: all are noted as created by the service.
        Assert.Equal(PolicyGroups.CleanupCandidates.Order(), account.CreatedKeys.Order());
        Assert.False(ops.State.Accounts.ContainsKey(Callers.OtherSid));
    }

    [Fact]
    public async Task Apply_twice_writes_nothing_more()
    {
        using var ops = new OpsHarness();
        await ops.Call(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock"]));
        var writes = ops.Hives.For(Callers.UserSid).Writes;
        var again = await ops.Call<LockPoliciesData>(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock"]));
        Assert.True(again.Ok);
        Assert.Equal(["lock"], again.Data!.Groups);
        Assert.Equal(writes, ops.Hives.For(Callers.UserSid).Writes);
    }

    [Fact]
    public async Task A_value_that_was_there_before_is_never_taken_over_nor_released()
    {
        using var ops = new OpsHarness();
        var mine = ops.Hives.For(Callers.UserSid);
        // A Group Policy set NoRun already.
        mine.WriteDword(LockHardening.ExplorerKey, "NoRun", 1);
        var response = await ops.Call<LockPoliciesData>(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock"]));
        Assert.True(response.Ok);
        Assert.Empty(response.Data!.Groups);
        Assert.Null(mine.ReadDword(LockHardening.ExplorerKey, "NoControlPanel"));
        Assert.False(ops.State.Accounts.ContainsKey(Callers.UserSid));

        // And so the service may not release it either.
        var release = await ops.Call(Callers.ElevatedAdmin(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]));
        Assert.Equal(ErrorCodes.Denied, release.Code);
        Assert.Equal(1, mine.ReadDword(LockHardening.ExplorerKey, "NoRun"));
    }

    [Fact]
    public async Task Release_removes_exactly_what_the_service_set_and_forgets_the_group()
    {
        using var ops = new OpsHarness();
        await ops.Call(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock", "taskmgr"]));
        var mine = ops.Hives.For(Callers.UserSid);
        // Someone else's entry in the DisallowRun list and another value in the same key stay.
        mine.WriteString(LockHardening.DisallowRunKey, "50", "notepad.exe");
        mine.WriteDword(LockHardening.ExplorerKey, "NoDrives", 4);

        var release = await ops.Call<LockPoliciesData>(Callers.User(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]));
        Assert.True(release.Ok, release.Code);
        Assert.Equal(["lock"], release.Data!.Groups);
        Assert.Empty(PolicyRelease.OursPresent(mine, PolicyGroups.Lock));
        Assert.Equal("notepad.exe", mine.ReadString(LockHardening.DisallowRunKey, "50"));
        Assert.Equal(4, mine.ReadDword(LockHardening.ExplorerKey, "NoDrives"));
        // taskmgr stays set and held.
        Assert.True(PolicyGroups.Applied(mine, PolicyGroups.TaskManager));
        Assert.Equal(["taskmgr"], ops.State.Accounts[Callers.UserSid].Groups);

        // Released already: a second release is not the service's any more.
        var again = await ops.Call(Callers.User(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]));
        Assert.Equal(ErrorCodes.Denied, again.Code);
    }

    [Fact]
    public async Task Another_account_cannot_release_and_its_request_never_touches_the_first_hive()
    {
        using var ops = new OpsHarness();
        await ops.Call(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock"]));
        var before = ops.Hives.For(Callers.UserSid).Dump();
        var other = await ops.Call(Callers.User(Callers.OtherSid), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]));
        Assert.Equal(ErrorCodes.Denied, other.Code);
        var cleanup = await ops.Call(Callers.User(Callers.OtherSid), Operation.CleanupPolicyKeys);
        Assert.Equal(ErrorCodes.Denied, cleanup.Code);
        Assert.Equal(before, ops.Hives.For(Callers.UserSid).Dump());
        Assert.Equal(["lock"], ops.State.Accounts[Callers.UserSid].Groups);
    }

    [Fact]
    public async Task With_a_code_enrolled_release_needs_a_grant_or_an_administrator()
    {
        using var ops = new OpsHarness(codeEnrolled: true, grants: new ValidGrant(Grant));
        await ops.Call(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock", "taskmgr"]));
        Assert.Equal(ErrorCodes.NeedGrant, (await ops.Call(Callers.User(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]))).Code);
        Assert.True((await ops.Call(Callers.User(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]), Grant)).Ok);
        Assert.True((await ops.Call(Callers.ElevatedAdmin(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["taskmgr"]))).Ok);
    }

    [Fact]
    public async Task Cleanup_removes_only_the_empty_keys_the_service_created()
    {
        using var ops = new OpsHarness();
        var mine = ops.Hives.For(Callers.UserSid);
        // The System key existed before the lock (with a value of the user).
        mine.WriteDword(LockHardening.SystemKey, "Other", 1);
        await ops.Call(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock", "taskmgr"]));
        Assert.DoesNotContain(LockHardening.SystemKey, ops.State.Accounts[Callers.UserSid].CreatedKeys);

        // Not while a group is still held.
        Assert.Equal(ErrorCodes.Failed, (await ops.Call(Callers.User(), Operation.CleanupPolicyKeys)).Code);

        await ops.Call(Callers.User(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock", "taskmgr"]));
        var cleanup = await ops.Call(Callers.User(), Operation.CleanupPolicyKeys);
        Assert.True(cleanup.Ok, cleanup.Code);
        Assert.False(mine.KeyExists(LockHardening.ExplorerKey));
        Assert.False(mine.KeyExists(LockHardening.WindowsSystemKey));
        Assert.True(mine.KeyExists(LockHardening.SystemKey));
        Assert.Equal(1, mine.ReadDword(LockHardening.SystemKey, "Other"));
        Assert.False(ops.State.Accounts.ContainsKey(Callers.UserSid));
        // Nothing left: the check refuses a further cleanup.
        Assert.Equal(ErrorCodes.Denied, (await ops.Call(Callers.User(), Operation.CleanupPolicyKeys)).Code);
    }

    [Fact]
    public async Task Cleanup_stops_when_something_else_lives_in_a_created_key()
    {
        using var ops = new OpsHarness();
        await ops.Call(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock"]));
        await ops.Call(Callers.User(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]));
        var mine = ops.Hives.For(Callers.UserSid);
        mine.WriteDword(LockHardening.ExplorerKey, "NoDrives", 4);
        Assert.Equal(ErrorCodes.Failed, (await ops.Call(Callers.User(), Operation.CleanupPolicyKeys)).Code);
        Assert.Equal(4, mine.ReadDword(LockHardening.ExplorerKey, "NoDrives"));
        Assert.NotEmpty(ops.State.Accounts[Callers.UserSid].CreatedKeys);
    }

    [Fact]
    public async Task A_hive_that_is_not_loaded_is_unavailable()
    {
        using var ops = new OpsHarness();
        ops.Hives.Loaded.Remove(Callers.UserSid);
        Assert.Equal(ErrorCodes.Unavailable, (await ops.Call(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock"]))).Code);
    }

    [Fact]
    public async Task A_failed_write_takes_back_what_was_written_and_forgets_the_group()
    {
        using var ops = new OpsHarness();
        ops.Hives.For(Callers.UserSid).ThrowOnWrite = true;
        Assert.Equal(ErrorCodes.Failed, (await ops.Call(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock"]))).Code);
        Assert.False(ops.State.Accounts.TryGetValue(Callers.UserSid, out var account) && account.Groups.Count > 0);
    }

    [Fact]
    public async Task What_the_service_set_survives_a_restart_of_the_service()
    {
        using var ops = new OpsHarness();
        await ops.Call(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock"]));
        ops.Restart();
        Assert.Equal(["lock"], ops.State.Accounts[Callers.UserSid].Groups);
        Assert.True((await ops.Call(Callers.User(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]))).Ok);
    }
}
