using CouchHud.Core.Guard;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.State;

namespace CouchHud.Service.Tests;

/// <summary>ResetServiceState (the way out of a damaged state) and the caller's own migration state in Hello and Status.</summary>
public class ResetStateTests
{
    private static readonly string Grant = Convert.ToBase64String(new byte[32]);

    private static void SetLock(MemoryRegistry registry)
    {
        foreach (var c in PolicyGroups.Lock.Changes.Concat(PolicyGroups.TaskManager.Changes))
        {
            if (c.Kind == GuardKind.RegistryDword) registry.WriteDword(c.Location, c.Name, int.Parse(c.Value));
            else registry.WriteString(c.Location, c.Name, c.Value);
        }
    }

    private static string Damaged(string sid) =>
        $$$"""{"version":1,"bootTimeMs":0,"accounts":{"{{{sid}}}":{"groups":["lock"],"createdKeys":[]},"S-1-5-18":{}},"paused":{"WSearch":{"holders":["{{{sid}}}"]},"WinDefend":{}},"extra":true}""";

    [Fact]
    public async Task A_damaged_state_is_set_aside_after_releasing_what_it_still_names()
    {
        using var ops = new OpsHarness(prepareState: folder => File.WriteAllText(Path.Combine(folder, ServiceStateStore.FileName), Damaged(Callers.UserSid)));
        SetLock(ops.Hives.For(Callers.UserSid));
        SetLock(ops.Hives.For(Callers.OtherSid));
        Assert.False(ops.Store.IsAvailable);

        var response = await ops.Call<ResetServiceStateData>(Callers.ElevatedAdmin(), Operation.ResetServiceState);
        Assert.True(response.Ok, response.Code);
        Assert.True(response.Data!.CorruptFileKept);
        Assert.Equal([Callers.UserSid], response.Data.Released);
        Assert.Empty(response.Data.Left);

        // The account the file named is free; one it did not name is not touched (its values may be someone else's).
        Assert.False(PolicyRelease.AnyOurs(ops.Hives.For(Callers.UserSid)));
        Assert.True(PolicyRelease.AnyOurs(ops.Hives.For(Callers.OtherSid)));
        // Allowlisted services named in it are started again; a denylisted one is never touched.
        Assert.Equal(["start WSearch"], ops.Scm.Calls);

        var kept = Assert.Single(Directory.GetFiles(ops.StateFolder, "state.corrupt-*.json"));
        Assert.Equal(Damaged(Callers.UserSid), File.ReadAllText(kept));
        Assert.True(ops.Store.IsAvailable);
        Assert.Empty(ops.State.Accounts);
        // And the service works again.
        Assert.True((await ops.Call(Callers.User(Callers.OtherSid), Operation.PauseServices, new ServicesArgs(["SysMain"]))).Ok);
    }

    [Theory]
    [InlineData(GrantKinds.Couch)]
    [InlineData(GrantKinds.Parent)]
    [InlineData(GrantKinds.WindowsAdmin)]
    public async Task A_reset_is_for_an_elevated_admin_and_no_grant_opens_it(GrantKinds kind)
    {
        using var ops = new OpsHarness(grants: new ValidGrant(Grant, kind), prepareState: folder => File.WriteAllText(Path.Combine(folder, ServiceStateStore.FileName), "{ broken"));
        Assert.Equal(ErrorCodes.Denied, (await ops.Call(Callers.User(), Operation.ResetServiceState)).Code);
        Assert.Equal(ErrorCodes.Denied, (await ops.Call(Callers.User(), Operation.ResetServiceState, grant: Grant)).Code);
        Assert.False(ops.Store.IsAvailable);
        Assert.Empty(Directory.GetFiles(ops.StateFolder, "state.corrupt-*"));
        var admin = await ops.Call<ResetServiceStateData>(Callers.ElevatedAdmin(), Operation.ResetServiceState);
        Assert.True(admin.Ok, admin.Code);
        Assert.True(admin.Data!.CorruptFileKept);
        Assert.Empty(admin.Data.Released);
    }

    [Fact]
    public void The_reset_checks_and_swaps_under_one_lock()
    {
        using var ops = new OpsHarness();
        ops.Store.Update(s =>
        {
            s.Account(Callers.UserSid).Groups.Add("lock");
            return true;
        });
        // Something is still held at the moment of the swap: nothing changes.
        Assert.Equal(ResetOutcome.Refused, ops.Store.Reset(ops.Now, s => s.Accounts.Values.All(a => a.Groups.Count == 0)));
        Assert.Equal(["lock"], ops.State.Accounts[Callers.UserSid].Groups);
        Assert.Equal(ResetOutcome.Fresh, ops.Store.Reset(ops.Now));
        Assert.Empty(ops.State.Accounts);
    }

    [Fact]
    public async Task A_release_of_every_account_goes_on_after_the_request_timed_out_and_Status_tells_when_it_is_done()
    {
        using var ops = new OpsHarness();
        await ops.Call(Callers.User(Callers.OtherSid), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["taskmgr"]));
        ops.Hives.OnDisk[Callers.OtherSid] = ops.Hives.Loaded[Callers.OtherSid];
        ops.Hives.Loaded.Remove(Callers.OtherSid);
        using var slow = new ManualResetEventSlim(false);
        ops.Hives.LoadGate = slow;
        Assert.Equal("none", (await ops.Call<StatusData>(Callers.User(), Operation.Status)).Data!.Release);

        // The harness gives a handler 2 s.
        Assert.Equal(ErrorCodes.Timeout, (await ops.Call(Callers.ElevatedAdmin(), Operation.ReleaseAllAccounts)).Code);
        Assert.Equal("running", (await ops.Call<StatusData>(Callers.User(), Operation.Status)).Data!.Release);
        slow.Set();
        for (var i = 0; i < 100 && ops.Operations.ReleaseStatus == "running"; i++) await Task.Delay(50);
        Assert.Equal("done", (await ops.Call<StatusData>(Callers.User(), Operation.Status)).Data!.Release);
        Assert.Empty(ops.State.Accounts.GetValueOrDefault(Callers.OtherSid)?.Groups ?? []);
    }

    [Fact]
    public async Task A_valid_state_is_reset_only_when_every_account_was_released()
    {
        using var ops = new OpsHarness();
        await ops.Call(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock"]));
        await ops.Call(Callers.User(Callers.OtherSid), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["taskmgr"]));
        ops.Hives.Loaded.Remove(Callers.OtherSid);   // signed out and not loadable

        Assert.Equal(ErrorCodes.Failed, (await ops.Call(Callers.ElevatedAdmin(), Operation.ResetServiceState)).Code);
        Assert.Equal(["taskmgr"], ops.State.Accounts[Callers.OtherSid].Groups);

        ops.Hives.For(Callers.OtherSid);
        var response = await ops.Call<ResetServiceStateData>(Callers.ElevatedAdmin(), Operation.ResetServiceState);
        Assert.True(response.Ok, response.Code);
        Assert.False(response.Data!.CorruptFileKept);
        Assert.Empty(ops.State.Accounts);
        Assert.Empty(Directory.GetFiles(ops.StateFolder, "state.corrupt-*"));
    }

    [Fact]
    public async Task Without_a_trusted_folder_there_is_nothing_to_reset()
    {
        using var ops = new OpsHarness(stateFolder: false);
        Assert.Equal(ErrorCodes.Unavailable, (await ops.Call(Callers.ElevatedAdmin(), Operation.ResetServiceState)).Code);
    }

    [Fact]
    public async Task Hello_and_Status_report_the_callers_own_migration_only()
    {
        using var ops = new OpsHarness();
        Assert.True((await ops.Call(Callers.User(), Operation.MigrateTasks)).Ok);

        var mine = await ops.Call<HelloData>(Callers.User(), Operation.Hello);
        var other = await ops.Call<HelloData>(Callers.User(Callers.OtherSid), Operation.Hello);
        Assert.Equal("migrated", mine.Data!.Migration);
        Assert.Equal("notMigrated", other.Data!.Migration);
        Assert.Equal("migrated", (await ops.Call<StatusData>(Callers.User(), Operation.Status)).Data!.Migration);
        Assert.Equal("notMigrated", (await ops.Call<StatusData>(Callers.User(Callers.OtherSid), Operation.Status)).Data!.Migration);
        Assert.Contains("ResetServiceState", mine.Data.Capabilities);
    }

    [Fact]
    public async Task Hello_says_unavailable_while_the_state_is_damaged()
    {
        using var ops = new OpsHarness(prepareState: folder => File.WriteAllText(Path.Combine(folder, ServiceStateStore.FileName), "[]"));
        Assert.Equal("unavailable", (await ops.Call<HelloData>(Callers.User(), Operation.Hello)).Data!.Migration);
    }
}
