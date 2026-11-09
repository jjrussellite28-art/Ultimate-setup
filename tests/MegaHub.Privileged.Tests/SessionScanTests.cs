using MegaHub.Privileged;

namespace MegaHub.Privileged.Tests;

public class SessionScanTests
{
    private const int User = 5, Domain = 7;

    private static string? Sid(string account) => "SID:" + account;

    [Fact]
    public void Signed_in_users_are_listed_and_empty_sessions_skipped()
    {
        var names = new Dictionary<int, (string?, string?)> { [1] = ("kid", "PC"), [2] = ("", ""), [3] = ("mum", "") };
        var sids = WindowsSessions.Collect([0, 1, 2, 3], null, (s, c) => c == User ? names[s].Item1 : names[s].Item2, Sid);
        Assert.Equal(new[] { @"SID:PC\kid", "SID:mum" }.Order(), sids!.Order());
    }

    [Fact]
    public void The_leaving_session_is_left_out()
    {
        var sids = WindowsSessions.Collect([1, 2], 2, (s, c) => c == User ? "u" + s : "", Sid);
        Assert.Equal(["SID:u1"], sids!);
    }

    [Theory]
    [InlineData(User)]
    [InlineData(Domain)]
    public void A_failed_query_makes_the_answer_unknown(int failing)
    {
        var sids = WindowsSessions.Collect([1, 2], null, (s, c) => s == 2 && c == failing ? null : "u" + s, Sid);
        Assert.Null(sids);
    }

    [Fact]
    public void An_account_that_cannot_be_resolved_makes_the_answer_unknown() =>
        Assert.Null(WindowsSessions.Collect([1], null, (_, _) => "u", _ => null));
}
