using MegaHub.Core.Remote;
using Xunit;

namespace MegaHub.Core.Tests;

public class SecondFactorChallengeTests
{
    private DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private int _spin;

    private SecondFactorChallenges Make() => new(() => _now, n => _spin++ % n);

    private static readonly Func<string, bool> Fine = _ => true;
    private static readonly Func<string, bool> Gone = _ => false;

    /// <summary>Asks and steps over the gap that follows a new question, so the next one is not held back.</summary>
    private SecondFactorQuestion Ask(SecondFactorChallenges c, string action = "lockMode", string hash = "h1", string detail = "")
    {
        var ask = c.Ask(action, hash, "Label", detail);
        Assert.NotNull(ask.Question);
        return ask.Question!;
    }

    private void StepOverGap() => _now += SecondFactorChallenges.AskGap;

    private static int Wrong(SecondFactorQuestion q) => q.Number == 10 ? 11 : 10;

    [Fact]
    public void The_question_has_a_two_digit_number_and_no_options_for_the_phone()
    {
        var q = Ask(Make());
        Assert.InRange(q.Number, 10, 99);
        Assert.Equal(32, q.RequestId.Length);
        // The phone gets action, detail and time: the record has no list of numbers to tap (a keypad, the number is read on the TV).
        Assert.DoesNotContain(typeof(SecondFactorQuestion).GetProperties(), p => p.Name is "Options" or "Choices");
    }

    [Fact]
    public void Real_numbers_stay_in_range_and_ids_never_repeat()
    {
        var c = new SecondFactorChallenges(() => _now);
        var ids = new HashSet<string>();
        for (var i = 0; i < 150; i++)
        {
            _now += SecondFactorChallenges.AskGap;
            var q = Ask(c, "a", "h" + i);
            Assert.True(ids.Add(q.RequestId));
            Assert.InRange(q.Number, 10, 99);
        }
    }

    [Fact]
    public void The_typed_number_gives_one_ticket_for_exactly_this_action_and_these_parameters()
    {
        var c = Make();
        var q = Ask(c);
        Assert.Equal(SecondFactorAnswer.Confirmed, c.Answer(q.RequestId, "phone1", q.Number, eligible: true));
        Assert.False(c.TryUse("lockMode", "other", Fine), "other parameters");
        Assert.False(c.TryUse("pcMode", "h1", Fine), "other action");
        Assert.True(c.TryUse("lockMode", "h1", Fine));
        Assert.False(c.TryUse("lockMode", "h1", Fine), "once");
    }

    [Fact]
    public void A_wrong_number_ends_the_question_at_once_and_gives_no_ticket()
    {
        var c = Make();
        var q = Ask(c);
        Assert.Equal(SecondFactorAnswer.Wrong, c.Answer(q.RequestId, "p", Wrong(q), true));
        Assert.Null(c.Current);
        Assert.False(c.TryUse("lockMode", "h1", Fine));
        _now += TimeSpan.FromHours(2);
        Assert.Equal(SecondFactorAnswer.Unknown, c.Answer(q.RequestId, "p", q.Number, true));
    }

    [Fact]
    public void The_backoff_is_one_minute_then_ten_then_an_hour_and_a_right_answer_starts_over()
    {
        Assert.Equal(TimeSpan.Zero, SecondFactorChallenges.Backoff(0));
        Assert.Equal(TimeSpan.FromMinutes(1), SecondFactorChallenges.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(10), SecondFactorChallenges.Backoff(2));
        Assert.Equal(TimeSpan.FromHours(1), SecondFactorChallenges.Backoff(3));
        Assert.Equal(TimeSpan.FromHours(1), SecondFactorChallenges.Backoff(9));

        var c = Make();
        var q = Ask(c, hash: "a");
        c.Answer(q.RequestId, "p", Wrong(q), true);
        Assert.Equal(TimeSpan.FromMinutes(1), c.LockedForDevice("p"));
        // Locked: even the right number counts for nothing, the question is still there for the administrator.
        StepOverGap();
        q = Ask(c, hash: "b");
        Assert.Equal(SecondFactorAnswer.LockedOut, c.Answer(q.RequestId, "p", q.Number, true));
        _now += TimeSpan.FromMinutes(1);
        Assert.Equal(TimeSpan.Zero, c.LockedForDevice("p"));

        q = Ask(c, hash: "c");
        c.Answer(q.RequestId, "p", Wrong(q), true);
        Assert.Equal(TimeSpan.FromMinutes(10), c.LockedForDevice("p"));
        _now += TimeSpan.FromMinutes(10);
        q = Ask(c, hash: "d");
        c.Answer(q.RequestId, "p", Wrong(q), true);
        Assert.Equal(TimeSpan.FromHours(1), c.LockedForDevice("p"));
        _now += TimeSpan.FromHours(1);
        q = Ask(c, hash: "e");
        c.Answer(q.RequestId, "p", Wrong(q), true);
        Assert.Equal(TimeSpan.FromHours(1), c.LockedForDevice("p"));

        // A right answer resets the count: the next wrong one is the first again.
        _now += TimeSpan.FromHours(1);
        q = Ask(c, hash: "f");
        Assert.Equal(SecondFactorAnswer.Confirmed, c.Answer(q.RequestId, "p", q.Number, true));
        StepOverGap();
        q = Ask(c, hash: "g");
        c.Answer(q.RequestId, "p", Wrong(q), true);
        Assert.Equal(TimeSpan.FromMinutes(1), c.LockedForDevice("p"));
    }

    [Fact]
    public void New_questions_come_at_most_every_ten_seconds_and_the_same_binding_is_not_new()
    {
        var c = Make();
        var first = c.Ask("lockMode", "a", "L").Question!;
        // Another binding at once: held back, the first stays.
        var second = c.Ask("pcMode", "b", "L");
        Assert.Null(second.Question);
        Assert.InRange(second.Throttled.TotalSeconds, 9, 10);
        Assert.Same(first, c.Current);
        // The same binding (the page repeating the call) gets its question, no new one.
        Assert.Same(first, c.Ask("lockMode", "a", "L").Question);
        _now += TimeSpan.FromSeconds(9);
        Assert.Null(c.Ask("pcMode", "b", "L").Question);
        _now += TimeSpan.FromSeconds(1);
        var replaced = c.Ask("pcMode", "b", "L").Question;
        Assert.NotNull(replaced);
        Assert.NotEqual(first.RequestId, replaced!.RequestId);
        // The old request id is dead.
        Assert.Equal(SecondFactorAnswer.Unknown, c.Answer(first.RequestId, "p", first.Number, true));
        Assert.Equal(SecondFactorAnswer.Confirmed, c.Answer(replaced.RequestId, "p", replaced.Number, true));
        Assert.False(c.TryUse("lockMode", "b", Fine));
        Assert.True(c.TryUse("pcMode", "b", Fine));
    }

    [Fact]
    public void The_throttle_also_holds_after_an_answer_and_after_cancel()
    {
        var c = Make();
        var q = c.Ask("a", "h", "L").Question!;
        c.Cancel(q.RequestId);
        Assert.Null(c.Ask("a", "h2", "L").Question);
        _now += SecondFactorChallenges.AskGap;
        Assert.NotNull(c.Ask("a", "h2", "L").Question);
    }

    [Fact]
    public void The_ticket_remembers_the_phone_and_is_refused_once_that_phone_may_not_confirm_any_more()
    {
        var c = Make();
        var q = Ask(c);
        c.Answer(q.RequestId, "phoneA", q.Number, true);
        Assert.False(c.TryUse("lockMode", "h1", Gone), "the phone was unpaired or lowered since");
        Assert.False(c.TryUse("lockMode", "h1", Fine), "and the ticket is gone either way");

        StepOverGap();
        q = Ask(c, hash: "h2");
        c.Answer(q.RequestId, "phoneA", q.Number, true);
        string? asked = null;
        Assert.True(c.HasTicket("lockMode", "h2", id => (asked = id) == "phoneA"));
        Assert.Equal("phoneA", asked);
        Assert.True(c.TryUse("lockMode", "h2", id => id == "phoneA"));
    }

    [Fact]
    public void The_administrators_ticket_needs_no_phone()
    {
        var c = Make();
        var q = Ask(c);
        Assert.False(c.ApproveByAdmin("wrong id"));
        Assert.True(c.ApproveByAdmin(q.RequestId));
        Assert.True(c.TryUse("lockMode", "h1", Gone));
        Assert.False(c.ApproveByAdmin(q.RequestId));
    }

    [Fact]
    public void The_administrator_works_while_the_phone_way_is_locked_and_starts_the_count_over()
    {
        var c = Make();
        var q = Ask(c, hash: "a");
        c.Answer(q.RequestId, "p", Wrong(q), true);
        Assert.True(c.LockedForDevice("p") > TimeSpan.Zero);
        StepOverGap();
        q = Ask(c, hash: "b");
        Assert.True(c.ApproveByAdmin(q.RequestId));
        Assert.Equal(TimeSpan.Zero, c.LockedForDevice("p"));
        Assert.True(c.TryUse("lockMode", "b", Gone));
    }

    [Fact]
    public void The_question_is_gone_after_sixty_seconds_and_so_is_an_unused_ticket()
    {
        var c = Make();
        var q = Ask(c);
        _now += TimeSpan.FromSeconds(59);
        Assert.NotNull(c.Current);
        _now += TimeSpan.FromSeconds(1);
        Assert.Null(c.Current);
        Assert.Equal(SecondFactorAnswer.Unknown, c.Answer(q.RequestId, "p", q.Number, true));

        q = Ask(c, hash: "t");
        Assert.Equal(SecondFactorAnswer.Confirmed, c.Answer(q.RequestId, "p", q.Number, true));
        _now += TimeSpan.FromSeconds(60);
        Assert.False(c.TryUse("lockMode", "t", Fine));
    }

    [Fact]
    public void A_replayed_answer_with_the_same_request_id_finds_nothing()
    {
        var c = Make();
        var q = Ask(c);
        Assert.Equal(SecondFactorAnswer.Confirmed, c.Answer(q.RequestId, "p", q.Number, true));
        Assert.Equal(SecondFactorAnswer.Unknown, c.Answer(q.RequestId, "p", q.Number, true));
        Assert.True(c.TryUse("lockMode", "h1", Fine));
        Assert.Equal(SecondFactorAnswer.Unknown, c.Answer(q.RequestId, "p", q.Number, true));
        Assert.False(c.TryUse("lockMode", "h1", Fine));
    }

    [Fact]
    public void A_phone_that_may_not_confirm_learns_nothing_and_does_not_end_the_question()
    {
        var c = Make();
        var q = Ask(c);
        Assert.Equal(SecondFactorAnswer.NotEligible, c.Answer(q.RequestId, "p", q.Number, eligible: false));
        Assert.Equal(SecondFactorAnswer.NotEligible, c.Answer(q.RequestId, "p", Wrong(q), eligible: false));
        Assert.Equal(TimeSpan.Zero, c.LockedForDevice("p"));
        Assert.NotNull(c.Current);
        Assert.Equal(SecondFactorAnswer.Confirmed, c.Answer(q.RequestId, "p", q.Number, true));
    }

    [Fact]
    public void A_wrong_request_id_is_unknown_and_counts_for_nothing()
    {
        var c = Make();
        var q = Ask(c);
        Assert.Equal(SecondFactorAnswer.Unknown, c.Answer("0000", "p", q.Number, true));
        Assert.Equal(SecondFactorAnswer.Unknown, c.Answer(null, "p", q.Number, true));
        Assert.Equal(TimeSpan.Zero, c.LockedForDevice("p"));
        Assert.NotNull(c.Current);
    }

    [Fact]
    public void Reset_drops_the_question_and_the_tickets_but_not_the_failed_tries()
    {
        var c = Make();
        var q = Ask(c, hash: "a");
        c.Answer(q.RequestId, "p", Wrong(q), true);
        StepOverGap();
        q = Ask(c, hash: "b");
        c.Reset();
        Assert.Null(c.Current);
        Assert.True(c.LockedForDevice("p") > TimeSpan.Zero, "resetting is no way to start the count over");

        c.ResetAll();
        Assert.Equal(TimeSpan.Zero, c.LockedForDevice("p"));
        Assert.Null(Make().Current);
    }

    [Fact]
    public void A_ticket_is_gone_after_Reset()
    {
        var c = Make();
        var q = Ask(c);
        c.Answer(q.RequestId, "p", q.Number, true);
        c.Reset();
        Assert.False(c.TryUse("lockMode", "h1", Fine));
    }

    [Fact]
    public void The_binding_hash_differs_for_action_and_every_parameter_and_cannot_be_confused_by_joining()
    {
        var a = SecondFactorChallenges.Bind("lockMode", "off");
        Assert.NotEqual(a, SecondFactorChallenges.Bind("lockMode", "code"));
        Assert.NotEqual(a, SecondFactorChallenges.Bind("pcMode", "off"));
        Assert.NotEqual(SecondFactorChallenges.Bind("x", "ab", "c"), SecondFactorChallenges.Bind("x", "a", "bc"));
        Assert.NotEqual(SecondFactorChallenges.Bind("x", "a"), SecondFactorChallenges.Bind("x", "a", ""));
        Assert.Equal(a, SecondFactorChallenges.Bind("lockMode", "off"));
        Assert.Equal(64, a.Length);
        // A set of steps is one binding that differs from each step and from another set.
        var shell = SecondFactorChallenges.Bind("shell", "uninstall");
        var lockOff = SecondFactorChallenges.Bind("lockMode", "off");
        Assert.NotEqual(SecondFactorChallenges.BindAll(shell, lockOff), SecondFactorChallenges.BindAll(shell));
        Assert.NotEqual(SecondFactorChallenges.BindAll(shell, lockOff), SecondFactorChallenges.BindAll(lockOff, shell));
        Assert.NotEqual(SecondFactorChallenges.BindAll(shell), shell);
    }

    [Fact]
    public void A_null_parameter_and_an_empty_one_bind_differently()
    {
        Assert.NotEqual(SecondFactorChallenges.Bind("x", (string?)null), SecondFactorChallenges.Bind("x", ""));
        Assert.NotEqual(SecondFactorChallenges.Bind("x", null, ""), SecondFactorChallenges.Bind("x", "", null));
        Assert.Equal(SecondFactorChallenges.Bind("x", (string?)null), SecondFactorChallenges.Bind("x", (string?)null));
        var c = Make();
        var q = Ask(c, hash: SecondFactorChallenges.Bind("x", (string?)null));
        c.Answer(q.RequestId, "p", q.Number, true);
        Assert.False(c.TryUse("lockMode", SecondFactorChallenges.Bind("x", ""), Fine));
    }

    [Fact]
    public void The_lock_out_is_per_phone_a_foreign_phone_cannot_shut_the_parents_phone_out()
    {
        var c = Make();
        var q = Ask(c, hash: "a");
        c.Answer(q.RequestId, "foreign", Wrong(q), true);
        Assert.True(c.LockedForDevice("foreign") > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, c.LockedForDevice("parents"));
        StepOverGap();
        q = Ask(c, hash: "b");
        Assert.Equal(SecondFactorAnswer.Confirmed, c.Answer(q.RequestId, "parents", q.Number, true));
        // The TV sees a free phone among the candidates, and the foreign one alone is locked.
        Assert.Equal(TimeSpan.Zero, c.LockedFor(["foreign", "parents"]));
        Assert.True(c.LockedFor(["foreign"]) > TimeSpan.Zero);
        // A right answer of one phone does not clear the other's count.
        Assert.True(c.LockedForDevice("foreign") > TimeSpan.Zero);
    }

    [Fact]
    public void Ten_wrong_answers_across_all_phones_within_an_hour_shut_everybody_for_an_hour()
    {
        var c = Make();
        for (var i = 0; i < SecondFactorChallenges.HardLimit; i++)
        {
            StepOverGap();
            var q = Ask(c, hash: "h" + i);
            Assert.Equal(SecondFactorAnswer.Wrong, c.Answer(q.RequestId, "phone" + i, Wrong(q), true));
        }
        Assert.True(c.IsLockedOut);
        Assert.True(c.LockedForDevice("a fresh phone") > TimeSpan.Zero);
        Assert.True(c.LockedFor([]) > TimeSpan.Zero);
        _now += SecondFactorChallenges.HardLock;
        Assert.False(c.IsLockedOut);
    }

    [Fact]
    public void Wrong_answers_older_than_an_hour_do_not_count_towards_the_hard_limit()
    {
        var c = Make();
        for (var i = 0; i < SecondFactorChallenges.HardLimit - 1; i++)
        {
            StepOverGap();
            var q = Ask(c, hash: "h" + i);
            c.Answer(q.RequestId, "phone" + i, Wrong(q), true);
        }
        _now += SecondFactorChallenges.HardWindow;
        var last = Ask(c, hash: "last");
        c.Answer(last.RequestId, "phoneX", Wrong(last), true);
        Assert.False(c.IsLockedOut);
    }

    [Fact]
    public void The_administrator_clears_the_failed_tries_of_every_phone()
    {
        var c = Make();
        var q = Ask(c, hash: "a");
        c.Answer(q.RequestId, "p1", Wrong(q), true);
        StepOverGap();
        q = Ask(c, hash: "b");
        Assert.True(c.ApproveByAdmin(q.RequestId));
        Assert.Equal(TimeSpan.Zero, c.LockedForDevice("p1"));
    }

    private string TempState() => Path.Combine(Path.GetTempPath(), "sf-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void The_failed_tries_and_lock_outs_survive_a_restart()
    {
        var file = TempState();
        try
        {
            var first = new SecondFactorChallenges(() => _now, n => _spin++ % n, file);
            var q = first.Ask("lockMode", "a", "L").Question!;
            first.Answer(q.RequestId, "p", q.Number == 10 ? 11 : 10, true);
            var wait = first.LockedForDevice("p");
            Assert.True(wait > TimeSpan.Zero);

            _now += TimeSpan.FromSeconds(20);
            var second = new SecondFactorChallenges(() => _now, n => _spin++ % n, file);
            Assert.InRange(second.LockedForDevice("p").TotalSeconds, 30, 41);
            // The count went on too: the next wrong answer is the second one (10 minutes), not a fresh first.
            _now += TimeSpan.FromMinutes(1);
            var q2 = second.Ask("lockMode", "b", "L").Question!;
            second.Answer(q2.RequestId, "p", q2.Number == 10 ? 11 : 10, true);
            Assert.Equal(TimeSpan.FromMinutes(10), second.LockedForDevice("p"));
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".bak");
        }
    }

    [Fact]
    public void A_state_file_that_cannot_be_read_shuts_the_phone_way_for_an_hour_and_a_missing_one_does_not()
    {
        var file = TempState();
        try
        {
            Assert.False(new SecondFactorChallenges(() => _now, null, file).IsLockedOut, "no file: a fresh start");
            File.WriteAllText(file, "{ this is not json");
            var broken = new SecondFactorChallenges(() => _now, null, file);
            Assert.True(broken.IsLockedOut);
            Assert.True(broken.LockedFor([]) > TimeSpan.FromMinutes(59));
            _now += TimeSpan.FromHours(1);
            Assert.False(broken.IsLockedOut);
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".bak");
        }
    }

    [Fact]
    public void A_stored_lock_out_far_in_the_future_is_cut_to_an_hour()
    {
        var file = TempState();
        try
        {
            var far = _now.AddYears(5).ToString("O");
            File.WriteAllText(file, "{\"Devices\":{\"p\":{\"Strikes\":9,\"Until\":\"" + far + "\"}},\"Failures\":[],\"GlobalUntil\":\"" + far + "\"}");
            var c = new SecondFactorChallenges(() => _now, null, file);
            Assert.True(c.LockedForDevice("p") <= SecondFactorChallenges.HardLock);
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".bak");
        }
    }

    [Fact]
    public void The_detail_travels_with_the_question()
    {
        var q = Ask(Make(), detail: "Kind: weekdayMinutes");
        Assert.Equal("Kind: weekdayMinutes", q.Detail);
    }

    [Fact]
    public void Failed_tries_stamped_in_the_future_are_capped_to_now_not_dropped()
    {
        var file = TempState();
        try
        {
            var future = _now.AddDays(3).ToString("O");
            var stamps = string.Join(",", Enumerable.Repeat("\"" + future + "\"", SecondFactorChallenges.HardLimit - 1));
            File.WriteAllText(file, "{\"Devices\":{},\"Failures\":[" + stamps + "],\"GlobalUntil\":\"0001-01-01T00:00:00+00:00\"}");
            var c = new SecondFactorChallenges(() => _now, n => _spin++ % n, file);
            Assert.False(c.IsLockedOut);
            // One more wrong answer makes the tenth within the hour: dropped stamps would leave it at one.
            var q = c.Ask("lockMode", "a", "L").Question!;
            c.Answer(q.RequestId, "p", q.Number == 10 ? 11 : 10, true);
            Assert.True(c.IsLockedOut);
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".bak");
        }
    }
}
