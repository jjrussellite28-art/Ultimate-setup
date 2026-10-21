using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Tests;

public class AuthorizerTests
{
    private static ValidatedRequest Req(Operation op, object? args = null) => new(op, null, args);

    [Fact]
    public void Every_operation_has_a_rule()
    {
        foreach (var op in Enum.GetValues<Operation>())
        {
            Assert.True(OperationPolicy.Table.ContainsKey(op), $"{op} has no entry in OperationPolicy.Table");
            Assert.True(Enum.IsDefined(OperationPolicy.Table[op].Requirement));
        }
        Assert.Equal(Enum.GetValues<Operation>().Length, OperationPolicy.Table.Count);
    }

    [Fact]
    public void Every_operation_has_a_rate_bucket_decision()
    {
        // Codes and passwords are always limited; health is not.
        Assert.Equal(RateBucket.Codes, RateLimiter.BucketFor(Operation.VerifyCode));
        Assert.Equal(RateBucket.Housekeeping, RateLimiter.BucketFor(Operation.EnrollPasscode));
        Assert.Equal(RateBucket.Housekeeping, RateLimiter.BucketFor(Operation.ChangePasscode));
        Assert.Equal(RateBucket.Password, RateLimiter.BucketFor(Operation.SetAutoLogon));
        var unlimited = Enum.GetValues<Operation>().Where(o => RateLimiter.BucketFor(o) == RateBucket.None).ToHashSet();
        Assert.Equal(new HashSet<Operation> { Operation.Hello, Operation.Status, Operation.Ping, Operation.GetJob, Operation.GetRecentEvents }, unlimited);
    }

    [Fact]
    public void A_standard_interactive_user_passes_the_caller_check() => Assert.Null(Authorizer.CheckCaller(Callers.User()));

    public static TheoryData<string, CallerInfo?> RefusedCallers() => new()
    {
        { "no identity", null },
        { "low IL", Callers.User() with { IntegrityLevel = IntegrityLevels.Low } },
        { "untrusted IL", Callers.User() with { IntegrityLevel = IntegrityLevels.Untrusted } },
        { "network", Callers.User() with { IsNetwork = true } },
        { "not interactive", Callers.User() with { IsInteractive = false } },
        { "service logon", Callers.User() with { IsService = true } },
        { "anonymous flag", Callers.User() with { IsAnonymous = true } },
        { "anonymous SID", Callers.User() with { Sid = "S-1-5-7" } },
        { "SYSTEM", Callers.User() with { Sid = "S-1-5-18", IntegrityLevel = IntegrityLevels.System } },
        { "LOCAL SERVICE", Callers.User() with { Sid = "S-1-5-19" } },
        { "NETWORK SERVICE", Callers.User() with { Sid = "S-1-5-20" } },
        { "service SID", Callers.User() with { Sid = "S-1-5-80-1-2-3-4-5" } },
        { "IIS app pool", Callers.User() with { Sid = "S-1-5-82-1-2-3-4-5" } },
        { "Everyone", Callers.User() with { Sid = "S-1-1-0" } },
        { "guest", Callers.User() with { Sid = "S-1-5-21-1-2-3-501" } },
        { "DefaultAccount", Callers.User() with { Sid = "S-1-5-21-1-2-3-503" } },
        { "WDAGUtilityAccount", Callers.User() with { Sid = "S-1-5-21-1-2-3-504" } },
        { "garbage SID", Callers.User() with { Sid = "S-1-5-21-1-2-3-1001\n" } },
        { "empty SID", Callers.User() with { Sid = "" } },
        { "AppContainer", Callers.User() with { IsAppContainer = true } },
        { "restricted", Callers.User() with { IsRestricted = true } },
        { "session 0", Callers.User() with { PipeSessionId = 0, TokenSessionId = 0 } },
        { "session mismatch", Callers.User() with { TokenSessionId = 2 } },
        { "no process", Callers.User() with { ProcessId = 0 } },
        { "no logon session", Callers.User() with { LogonId = 0 } },
    };

    [Theory]
    [MemberData(nameof(RefusedCallers))]
    public void Callers_that_are_no_interactive_user_are_refused_for_every_operation(string why, CallerInfo? caller)
    {
        Assert.NotNull(Authorizer.CheckCaller(caller));
        foreach (var op in Enum.GetValues<Operation>())
        {
            Assert.True(Authorizer.AuthorizeTable(caller, Req(op), codeEnrolled: false).Verdict == Verdict.Deny, $"{why}: {op}");
        }
    }

    [Fact]
    public void Entra_id_and_medium_plus_and_rdp_sessions_are_fine()
    {
        Assert.Null(Authorizer.CheckCaller(Callers.User("S-1-12-1-111-222-333-444")));
        // The non-person RIDs are a thing of S-1-5-21 accounts only; an Entra ID SID ending in 501 is a person.
        Assert.Null(Authorizer.CheckCaller(Callers.User("S-1-12-1-111-222-333-501")));
        Assert.Null(Authorizer.CheckCaller(Callers.User("S-1-12-1-111-222-333-504")));
        Assert.Null(Authorizer.CheckCaller(Callers.User() with { IntegrityLevel = IntegrityLevels.MediumPlus }));
        Assert.Null(Authorizer.CheckCaller(Callers.User() with { PipeSessionId = 3, TokenSessionId = 3 }));
    }

    [Theory]
    [InlineData(Operation.Hello)]
    [InlineData(Operation.Status)]
    [InlineData(Operation.Ping)]
    [InlineData(Operation.ApplyLockPolicies)]
    [InlineData(Operation.PauseServices)]
    [InlineData(Operation.ResumeServices)]
    [InlineData(Operation.AutoLogonOff)]
    [InlineData(Operation.InstallWindowsUpdates)]
    [InlineData(Operation.RemoveRemoteFirewall)]
    [InlineData(Operation.InstallCouchHudUpdate)]
    [InlineData(Operation.EnrollPasscode)]
    [InlineData(Operation.VerifyCode)]
    [InlineData(Operation.RequestWindowsGrant)]
    [InlineData(Operation.GetJob)]
    [InlineData(Operation.GetRecentEvents)]
    [InlineData(Operation.MigrateTasks)]
    public void A_standard_user_may_do_these_for_its_own_account(Operation op) =>
        Assert.Equal(Verdict.Allow, Authorizer.AuthorizeTable(Callers.User(), Req(op), codeEnrolled: true).Verdict);

    [Theory]
    [InlineData(Operation.ReleaseLockPolicies)]
    [InlineData(Operation.CleanupPolicyKeys)]
    [InlineData(Operation.SetRemoteFirewall)]
    [InlineData(Operation.ChangePasscode)]
    public void These_need_a_grant_from_a_standard_user(Operation op)
    {
        Assert.Equal(Verdict.NeedGrant, Authorizer.AuthorizeTable(Callers.User(), Req(op), codeEnrolled: true).Verdict);
        Assert.Equal(Verdict.Allow, Authorizer.AuthorizeTable(Callers.ElevatedAdmin(), Req(op), codeEnrolled: true).Verdict);
    }

    [Fact]
    public void Without_an_enrolled_code_release_is_parity_but_the_firewall_is_not()
    {
        Assert.Equal(Verdict.Allow, Authorizer.AuthorizeTable(Callers.User(), Req(Operation.ReleaseLockPolicies), codeEnrolled: false).Verdict);
        Assert.Equal(Verdict.Allow, Authorizer.AuthorizeTable(Callers.User(), Req(Operation.CleanupPolicyKeys), codeEnrolled: false).Verdict);
        Assert.Equal(Verdict.NeedGrant, Authorizer.AuthorizeTable(Callers.User(), Req(Operation.SetRemoteFirewall), codeEnrolled: false).Verdict);
        Assert.Equal(Verdict.NeedGrant, Authorizer.AuthorizeTable(Callers.User(), Req(Operation.ChangePasscode), codeEnrolled: false).Verdict);
    }

    [Fact]
    public void Release_all_accounts_is_for_an_elevated_admin_only()
    {
        Assert.Equal(Verdict.Deny, Authorizer.AuthorizeTable(Callers.User(), Req(Operation.ReleaseAllAccounts), false).Verdict);
        Assert.Equal(Verdict.Allow, Authorizer.AuthorizeTable(Callers.ElevatedAdmin(), Req(Operation.ReleaseAllAccounts), false).Verdict);
    }

    [Fact]
    public void A_filtered_admin_token_is_no_admin()
    {
        // UAC: an administrator's normal token has the group deny-only and medium IL.
        var filtered = Callers.User() with { IsAdminGroupEnabled = false, IntegrityLevel = IntegrityLevels.Medium };
        Assert.False(filtered.IsElevatedAdmin);
        Assert.Equal(Verdict.Deny, Authorizer.AuthorizeTable(filtered, Req(Operation.ReleaseAllAccounts), false).Verdict);

        // A live group at medium IL (should not exist) is not trusted either.
        var odd = Callers.User() with { IsAdminGroupEnabled = true, IntegrityLevel = IntegrityLevels.Medium };
        Assert.False(odd.IsElevatedAdmin);
        Assert.Equal(Verdict.NeedGrant, Authorizer.AuthorizeTable(odd, Req(Operation.SetRemoteFirewall), false).Verdict);
    }

    [Fact]
    public void Set_auto_logon_needs_an_administrators_grant_or_an_elevated_admin()
    {
        // Machine-wide (owner decision): the caller's own account with password proof (the handler), plus an administrator's approval
        // as a grant (P9) or an elevated token, with or without allowPasswordSignIn, whether a code is enrolled or not.
        Assert.Equal(Requirement.ParentGrantOrAdmin, OperationPolicy.Table[Operation.SetAutoLogon].Requirement);
        Assert.Equal(GrantKinds.Parent | GrantKinds.WindowsAdmin, OperationPolicy.AcceptedGrants(Operation.SetAutoLogon));
        Assert.Equal(GrantKinds.Couch | GrantKinds.ParentOrAdmin, OperationPolicy.AcceptedGrants(Operation.ReleaseLockPolicies));
        Assert.Equal(GrantKinds.None, OperationPolicy.AcceptedGrants(Operation.ResetServiceState));
        foreach (var allow in new[] { false, true })
        {
            var request = Req(Operation.SetAutoLogon, new SetAutoLogonArgs("pw", allow));
            Assert.Equal(Verdict.NeedGrant, Authorizer.AuthorizeTable(Callers.User(), request, codeEnrolled: false).Verdict);
            Assert.Equal(Verdict.Allow, Authorizer.AuthorizeTable(Callers.ElevatedAdmin(), request, codeEnrolled: true).Verdict);
        }
    }

    [Fact]
    public void Reset_service_state_is_for_an_elevated_admin_only()
    {
        // Machine-wide release: no grant of any kind opens it.
        Assert.Equal(Requirement.Admin, OperationPolicy.Table[Operation.ResetServiceState].Requirement);
        Assert.Equal(Verdict.Deny, Authorizer.AuthorizeTable(Callers.User(), Req(Operation.ResetServiceState), codeEnrolled: false).Verdict);
        Assert.Equal(Verdict.Deny, Authorizer.AuthorizeTable(Callers.User() with { IsAdminGroupEnabled = true }, Req(Operation.ResetServiceState), codeEnrolled: false).Verdict);
        Assert.Equal(Verdict.Allow, Authorizer.AuthorizeTable(Callers.ElevatedAdmin(), Req(Operation.ResetServiceState), codeEnrolled: false).Verdict);
        Assert.Equal(RateBucket.Housekeeping, RateLimiter.BucketFor(Operation.ResetServiceState));
    }
    [Fact]
    public void No_argument_type_carries_an_account()
    {
        // The account is the token's SID: no argument record may have a field that names a user or SID.
        var argTypes = typeof(Operation).Assembly.GetTypes().Where(t => t.Name.EndsWith("Args", StringComparison.Ordinal));
        foreach (var type in argTypes)
        {
            foreach (var property in type.GetProperties())
            {
                Assert.DoesNotContain("sid", property.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("user", property.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("account", property.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
