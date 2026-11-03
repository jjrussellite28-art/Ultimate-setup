using System.Text;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Tests;

/// <summary>
/// ResetServiceState through the real grant store (owner decision for 0.6.4): an administrator's approval through UAC for exactly this
/// operation opens it once; the couch code, a parents' code, the Windows unlock or an approval for another operation never do.
/// </summary>
public class ServiceStateApprovalTests
{
    private static CallerInfo User => Callers.User();

    private static byte[] Reset(string? grant = null) => ProtocolJson.SerializeRequest(Operation.ResetServiceState, grant: grant);

    [Fact]
    public void It_can_be_asked_for_in_an_administrator_approval() =>
        Assert.True(new RequestValidator(_ => false).Validate(Encoding.UTF8.GetBytes("""{"v":1,"op":"RequestAdminApproval","args":{"ops":["ResetServiceState"]}}""")).Ok);

    [Theory]
    [InlineData("""{"v":1,"op":"VerifyCode","args":{"kind":"parent","sequence":["a","b","x","y","up","down"],"ops":["ResetServiceState"]}}""")]
    [InlineData("""{"v":1,"op":"VerifyCode","args":{"kind":"parent","sequence":["a","b","x","y","up","down"],"ops":["SetAutoLogon","ResetServiceState"]}}""")]
    [InlineData("""{"v":1,"op":"VerifyCode","args":{"kind":"parent","sequence":["a","b","x","y","up","down"],"ops":["ResetCodeStore"]}}""")]
    [InlineData("""{"v":1,"op":"ResetServiceState","args":{"sid":"S-1-5-18"}}""")]
    public void A_parents_code_is_never_checked_for_it_and_it_takes_no_arguments(string json) =>
        Assert.False(new RequestValidator(_ => false).Validate(Encoding.UTF8.GetBytes(json)).Ok);

    [Fact]
    public async Task An_administrators_approval_opens_it_once()
    {
        using var rig = new CodeRig();
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.CallAsync(User, Reset())).Response.Code);
        var grant = await rig.AdminGrantAsync(User, Operation.ResetServiceState);
        Assert.NotNull(grant);
        var response = (await rig.CallAsync(User, Reset(grant))).Response;
        Assert.True(response.Ok, response.Code);
        // Used up: the same grant does not open it a second time.
        Assert.False((await rig.CallAsync(User, Reset(grant))).Response.Ok);
    }

    [Fact]
    public async Task An_approval_for_another_operation_or_a_forged_grant_does_not_open_it()
    {
        using var rig = new CodeRig();
        var other = await rig.AdminGrantAsync(User, Operation.SetAutoLogon);
        Assert.NotNull(other);
        Assert.False((await rig.CallAsync(User, Reset(other))).Response.Ok);
        Assert.False((await rig.CallAsync(User, Reset("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"))).Response.Ok);
        // Another account's approval is not this account's.
        var foreign = await rig.AdminGrantAsync(Callers.User(Callers.OtherSid), Operation.ResetServiceState);
        Assert.NotNull(foreign);
        Assert.False((await rig.CallAsync(User, Reset(foreign))).Response.Ok);
    }

    [Fact]
    public async Task The_couch_code_and_a_parents_code_do_not_open_it()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.LockWithCodeAsync(User, Codes.HashA, CodeKinds.Both)).Ok);
        var couch = await rig.GrantAsync(User, Codes.A);
        Assert.NotNull(couch);
        Assert.False((await rig.CallAsync(User, Reset(couch))).Response.Ok);
        var parent = await rig.GrantAsync(User, Codes.A, CodeKinds.Parent, Operation.SetAutoLogon);
        Assert.NotNull(parent);
        Assert.False((await rig.CallAsync(User, Reset(parent))).Response.Ok);
    }
}
