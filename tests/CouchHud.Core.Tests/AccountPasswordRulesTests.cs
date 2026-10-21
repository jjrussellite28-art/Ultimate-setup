using CouchHud.Core.Security;

namespace CouchHud.Core.Tests;

public sealed class AccountPasswordRulesTests
{
    [Theory]
    [InlineData(0x0200u, true)]    // normal account: Windows insists on a password
    [InlineData(0x0210u, true)]
    [InlineData(0x0220u, null)]    // password not required: may be empty, only a probe tells
    [InlineData(0x0020u, null)]
    public void TheAccountFlagsDecideWithoutASignInTry(uint flags, bool? expected) =>
        Assert.Equal(expected, AccountPasswordRules.FromFlags(flags));

    [Theory]
    [InlineData(true, 0, false)]        // the empty password works: none set
    [InlineData(false, 1327, false)]    // blank passwords refused for this account: none set
    [InlineData(false, 1326, true)]     // wrong password: there is one
    [InlineData(false, 1331, true)]
    public void AnEmptyPasswordTryTellsTheRest(bool succeeded, int error, bool hasPassword) =>
        Assert.Equal(hasPassword, AccountPasswordRules.FromBlankLogon(succeeded, error));

    [Fact]
    public void ATryIsNotRepeatedWithinMinutes() =>
        Assert.True(AccountPasswordRules.ProbeInterval >= TimeSpan.FromMinutes(1));
}
