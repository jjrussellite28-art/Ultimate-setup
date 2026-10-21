using CouchHud.Core.Net;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Tests;

/// <summary>SetAutoLogon / AutoLogonOff and the remote's firewall rule.</summary>
public class AutoLogonFirewallTests
{
    private static readonly string Grant = Convert.ToBase64String(new byte[32]);

    [Fact]
    public async Task SetAutoLogon_without_a_grant_is_refused_before_the_password_is_checked()
    {
        using var ops = new OpsHarness();
        foreach (var allow in new[] { false, true })
        {
            var response = await ops.Call(Callers.User(), Operation.SetAutoLogon, new SetAutoLogonArgs("right-password", allow));
            Assert.Equal(ErrorCodes.NeedGrant, response.Code);
        }
        Assert.Empty(ops.Logon.Calls);
        Assert.Empty(ops.AutoLogon.Calls);
    }

    [Theory]
    [InlineData(GrantKinds.Couch)]
    [InlineData(GrantKinds.WindowsUnlock)]
    public async Task A_grant_the_caller_earned_alone_is_not_enough_for_SetAutoLogon(GrantKinds kind)
    {
        using var ops = new OpsHarness(grants: new ValidGrant(Grant, kind));
        foreach (var allow in new[] { false, true })
        {
            Assert.Equal(ErrorCodes.NeedGrant, (await ops.Call(Callers.User(), Operation.SetAutoLogon, new SetAutoLogonArgs("right-password", allow), Grant)).Code);
        }
        Assert.Empty(ops.Logon.Calls);
        Assert.Empty(ops.AutoLogon.Calls);
    }

    [Theory]
    [InlineData(GrantKinds.Parent)]
    [InlineData(GrantKinds.WindowsAdmin)]
    public async Task With_a_parents_or_administrators_grant_a_standard_account_sets_up_its_own_autologon(GrantKinds kind)
    {
        // A parent approved it (parent code or an administrator's Windows credential); the token stays the child's, medium IL.
        using var ops = new OpsHarness(grants: new ValidGrant(Grant, kind));
        var response = await ops.Call(Callers.User(), Operation.SetAutoLogon, new SetAutoLogonArgs("right-password", AllowPasswordSignIn: true), Grant);
        Assert.True(response.Ok, response.Code);
        Assert.Equal([@"on COUCHPC\kid pw=right-password", "allow"], ops.AutoLogon.Calls);
        Assert.Equal(Callers.UserSid, ops.State.AutoLogonOwner);
    }

    [Fact]
    public async Task A_grant_does_not_make_the_autologon_another_accounts()
    {
        using var ops = new OpsHarness(grants: new ValidGrant(Grant, GrantKinds.Parent));
        ops.Logon.TokenSid = Callers.OtherSid;
        Assert.Equal(ErrorCodes.Denied, (await ops.Call(Callers.User(), Operation.SetAutoLogon, new SetAutoLogonArgs("right-password"), Grant)).Code);
        Assert.Empty(ops.AutoLogon.Calls);
    }

    [Fact]
    public async Task SetAutoLogon_uses_the_account_of_the_token_and_notes_the_owner_without_logging_the_password()
    {
        using var ops = new OpsHarness();
        var response = await ops.Call(Callers.ElevatedAdmin(), Operation.SetAutoLogon, new SetAutoLogonArgs("right-password", AllowPasswordSignIn: true));
        Assert.True(response.Ok, response.Code);
        Assert.Equal([@"COUCHPC\kid"], ops.Logon.Calls);
        Assert.Equal([@"on COUCHPC\kid pw=right-password", "allow"], ops.AutoLogon.Calls);
        Assert.Equal(Callers.UserSid, ops.State.AutoLogonOwner);
        Assert.All(ops.AutoLogon.LastPassword!, c => Assert.Equal('\0', c));
        Assert.DoesNotContain("right-password", ops.OpsLog.All);
        Assert.DoesNotContain("right-password", ops.Harness.Log.All);
        Assert.DoesNotContain("right-password", File.ReadAllText(Path.Combine(ops.StateFolder, "state.json")));
    }

    [Fact]
    public async Task A_password_that_signs_in_another_account_is_refused()
    {
        using var ops = new OpsHarness();
        ops.Logon.TokenSid = Callers.OtherSid;
        Assert.Equal(ErrorCodes.Denied, (await ops.Call(Callers.ElevatedAdmin(), Operation.SetAutoLogon, new SetAutoLogonArgs("right-password"))).Code);
        Assert.Empty(ops.AutoLogon.Calls);
        Assert.Null(ops.State.AutoLogonOwner);
    }

    [Fact]
    public async Task Wrong_passwords_are_refused_and_then_paused()
    {
        using var ops = new OpsHarness();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(ErrorCodes.VerificationFailed, (await ops.Call(Callers.ElevatedAdmin(), Operation.SetAutoLogon, new SetAutoLogonArgs("wrong"))).Code);
        }
        Assert.Equal(ErrorCodes.RateLimited, (await ops.Call(Callers.ElevatedAdmin(), Operation.SetAutoLogon, new SetAutoLogonArgs("right-password"))).Code);
        ops.Now += TimeSpan.FromMinutes(1);
        Assert.True((await ops.Call(Callers.ElevatedAdmin(), Operation.SetAutoLogon, new SetAutoLogonArgs("right-password"))).Ok);
    }

    [Fact]
    public async Task A_failed_turn_on_forgets_the_owner_again()
    {
        using var ops = new OpsHarness();
        ops.AutoLogon.TurnOnResult = 3;
        Assert.Equal(ErrorCodes.Failed, (await ops.Call(Callers.ElevatedAdmin(), Operation.SetAutoLogon, new SetAutoLogonArgs("right-password"))).Code);
        Assert.Null(ops.State.AutoLogonOwner);
    }

    [Fact]
    public async Task AutoLogonOff_is_for_the_owner_or_an_administrator_only()
    {
        using var ops = new OpsHarness();
        Assert.True((await ops.Call(Callers.ElevatedAdmin(), Operation.SetAutoLogon, new SetAutoLogonArgs("right-password"))).Ok);

        Assert.Equal(ErrorCodes.Denied, (await ops.Call(Callers.User(Callers.OtherSid), Operation.AutoLogonOff)).Code);
        Assert.True(ops.AutoLogon.Enabled);

        Assert.True((await ops.Call(Callers.User(), Operation.AutoLogonOff)).Ok);
        Assert.False(ops.AutoLogon.Enabled);
        Assert.Null(ops.State.AutoLogonOwner);

        // Without an owner: an administrator of any account may, a user may not.
        Assert.Equal(ErrorCodes.Denied, (await ops.Call(Callers.User(), Operation.AutoLogonOff)).Code);
        Assert.True((await ops.Call(Callers.ElevatedAdmin() with { Sid = Callers.OtherSid }, Operation.AutoLogonOff)).Ok);
    }

    [Fact]
    public async Task The_marker_of_an_earlier_version_makes_its_account_the_owner()
    {
        using var ops = new OpsHarness();
        ops.AutoLogon.Enabled = true;
        ops.AutoLogon.Marker = @"COUCHPC\kid";
        // Taken over once at start, so the state check only reads the state.
        ops.Restart();
        Assert.Equal(Callers.UserSid, ops.State.AutoLogonOwner);
        Assert.Equal(ErrorCodes.Denied, (await ops.Call(Callers.User(Callers.OtherSid), Operation.AutoLogonOff)).Code);
        Assert.True((await ops.Call(Callers.User(), Operation.AutoLogonOff)).Ok);
    }

    [Fact]
    public async Task AutoLogonOff_checks_the_owner_again_inside_its_gate()
    {
        using var ops = new OpsHarness();
        Assert.True((await ops.Call(Callers.ElevatedAdmin(), Operation.SetAutoLogon, new SetAutoLogonArgs("right-password"))).Ok);
        // The handler on its own (as if the owner changed after the state check ran): another account is refused.
        var handlers = new Dictionary<Operation, Dispatch.Handler>();
        ops.Operations.AddHandlers(handlers);
        var answer = await handlers[Operation.AutoLogonOff](new Dispatch.RequestContext(Callers.User(Callers.OtherSid), new ValidatedRequest(Operation.AutoLogonOff, null, null)), CancellationToken.None);
        Assert.Equal(ErrorCodes.Denied, ProtocolJson.DeserializeResponse(answer)!.Code);
        Assert.True(ops.AutoLogon.Enabled);
    }

    [Fact]
    public async Task Any_failure_of_SetAutoLogon_gives_the_earlier_owner_back()
    {
        using var ops = new OpsHarness();
        ops.Store.Update(s =>
        {
            s.AutoLogonOwner = Callers.OtherSid;
            return true;
        });
        ops.AutoLogon.ThrowOnTurnOn = new InvalidOperationException("unexpected");
        var response = await ops.Call(Callers.ElevatedAdmin(), Operation.SetAutoLogon, new SetAutoLogonArgs("right-password"));
        Assert.False(response.Ok);
        Assert.Equal(Callers.OtherSid, ops.State.AutoLogonOwner);
    }

    // ---- firewall ----

    [Fact]
    public async Task Opening_the_firewall_needs_a_grant_or_an_administrator()
    {
        using var ops = new OpsHarness(grants: new ValidGrant(Grant));
        Assert.Equal(ErrorCodes.NeedGrant, (await ops.Call(Callers.User(), Operation.SetRemoteFirewall, new SetRemoteFirewallArgs([8766]))).Code);
        Assert.Empty(ops.Firewall.Calls);
        Assert.True((await ops.Call(Callers.User(), Operation.SetRemoteFirewall, new SetRemoteFirewallArgs([8766]), Grant)).Ok);
    }

    [Fact]
    public async Task The_rule_has_the_fixed_name_program_and_scope_and_is_noted()
    {
        using var ops = new OpsHarness();
        Assert.True((await ops.Call(Callers.ElevatedAdmin(), Operation.SetRemoteFirewall, new SetRemoteFirewallArgs([8766, 8765]))).Ok);
        var add = Assert.Single(ops.Firewall.Calls);
        Assert.Equal(FirewallRule.AddArgs(Path.Combine(ops.AppDir, "CouchHud.exe"), [8766, 8765]), add);
        Assert.Equal([8766, 8765], ops.State.FirewallPorts);

        // Again: the rule is changed in place, not added twice.
        Assert.True((await ops.Call(Callers.ElevatedAdmin(), Operation.SetRemoteFirewall, new SetRemoteFirewallArgs([8766]))).Ok);
        Assert.Equal("set", ops.Firewall.Calls[^1][2]);
        Assert.Single(ops.Firewall.RuleList!, r => r.Name == FirewallRule.Name);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(70000)]
    public async Task Ports_out_of_range_never_reach_netsh(int port)
    {
        using var ops = new OpsHarness();
        Assert.Equal(ErrorCodes.BadArgument, (await ops.Call(Callers.ElevatedAdmin(), Operation.SetRemoteFirewall, new SetRemoteFirewallArgs([port]))).Code);
        Assert.Empty(ops.Firewall.Calls);
    }

    [Fact]
    public async Task A_rule_of_our_name_that_blocks_is_replaced()
    {
        using var ops = new OpsHarness();
        ops.Firewall.RuleList!.Add(new FirewallRuleInfo(FirewallRule.Name, false, true, true, null, "8766", 6, "*"));
        Assert.True((await ops.Call(Callers.ElevatedAdmin(), Operation.SetRemoteFirewall, new SetRemoteFirewallArgs([8766]))).Ok);
        Assert.Equal(FirewallRule.DeleteArgs(), ops.Firewall.Calls[0]);
        Assert.Equal("add", ops.Firewall.Calls[1][2]);
    }

    [Fact]
    public async Task Removing_takes_our_rule_and_our_legacy_rules_but_never_the_ones_of_Windows()
    {
        using var ops = new OpsHarness();
        var exe = Path.Combine(ops.AppDir, "CouchHud.exe");
        await ops.Call(Callers.ElevatedAdmin(), Operation.SetRemoteFirewall, new SetRemoteFirewallArgs([8766]));
        var windowsBlock = new FirewallRuleInfo(FirewallRule.LegacyName, false, true, true, exe, "*", 6, "*");
        var oldOurs = new FirewallRuleInfo(FirewallRule.LegacyName, true, true, true, exe, "8765", 6, "LocalSubnet");
        ops.Firewall.RuleList!.Add(windowsBlock);
        ops.Firewall.RuleList.Add(oldOurs);

        Assert.True((await ops.Call(Callers.User(), Operation.RemoveRemoteFirewall)).Ok);
        Assert.DoesNotContain(ops.Firewall.RuleList, r => r.Name == FirewallRule.Name);
        Assert.DoesNotContain(oldOurs, ops.Firewall.RuleList);
        Assert.Contains(windowsBlock, ops.Firewall.RuleList);
        Assert.Null(ops.State.FirewallPorts);
        // No netsh call ever names the legacy rule without its program, ports and scope.
        Assert.All(ops.Firewall.Calls.Where(c => c.Contains($"name={FirewallRule.LegacyName}")), c => Assert.Contains("remoteip=localsubnet", c));
    }

    [Fact]
    public async Task Legacy_rules_are_not_touched_when_the_firewall_cannot_be_read()
    {
        using var ops = new OpsHarness();
        ops.Firewall.RuleList = null;
        Assert.Equal(ErrorCodes.Unavailable, (await ops.Call(Callers.User(), Operation.RemoveLegacyFirewallRules)).Code);
        Assert.Empty(ops.Firewall.Calls);
    }
}
