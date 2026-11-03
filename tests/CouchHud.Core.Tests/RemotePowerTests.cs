using System.Text.Json;
using System.Text.Json.Nodes;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Localization;
using CouchHud.Core.Remote;

namespace CouchHud.Core.Tests;

/// <summary>Power from the paired phone: a fixed action list, the couch's gates, a one-time ticket per confirmation, a rate limit, a clean log line.</summary>
public class RemotePowerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 21, 0, 0, TimeSpan.Zero);
    private static readonly RemotePowerContext Open = new(Locked: false, ProfileHeld: false, AreaAllowsPower: true);
    private static readonly IReadOnlySet<string> None = new HashSet<string>();
    private static readonly IReadOnlySet<string> WithPower = new HashSet<string> { RemotePower.BridgeMethod };

    [Theory]
    [InlineData("shutdown")]
    [InlineData("restart")]
    [InlineData("sleep")]
    public void The_three_actions_are_allowed_on_an_open_couch(string action)
    {
        Assert.True(RemotePower.IsAction(action));
        Assert.Equal(RemotePowerRefusal.None, RemotePower.Decide(action, Open));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Shutdown")]
    [InlineData(" shutdown")]
    [InlineData("shutdown ")]
    [InlineData("restart-pc")]
    [InlineData("standby")]
    [InlineData("logoff")]
    [InlineData("quit")]
    [InlineData("pc-mode")]
    [InlineData("restart-app")]
    [InlineData("shutdown\0")]
    public void Anything_else_is_an_unknown_action(string? action)
    {
        Assert.False(RemotePower.IsAction(action));
        Assert.Equal(RemotePowerRefusal.UnknownAction, RemotePower.Decide(action, Open));
    }

    [Fact]
    public void The_phone_gets_exactly_the_gates_of_the_couchs_power_call()
    {
        // The couch's power call works on a locked couch and while the picker waits; the phone may do what the couch may, no more.
        Assert.Equal(LockPolicy.OpenWhileLocked.Contains("power") ? RemotePowerRefusal.None : RemotePowerRefusal.Locked,
            RemotePower.Decide("shutdown", Open with { Locked = true }));
        Assert.Equal(ProfilePolicy.OpenWhileHeld.Contains("power") ? RemotePowerRefusal.None : RemotePowerRefusal.ProfileHeld,
            RemotePower.Decide("restart", Open with { ProfileHeld = true }));
        Assert.Equal(RemotePowerRefusal.AreaOff, RemotePower.Decide("sleep", Open with { AreaAllowsPower = false }));
    }

    /// <summary>The decision with lists the couch does not have today, so every refusal is reached (locked, held, area, entry unlock), in the bridge's order.</summary>
    public static TheoryData<string, bool, bool, bool, bool, bool, bool, bool, string, RemotePowerRefusal> Cases => new()
    {
        // action, locked, held, area, pcMode, enabled, powerOpenWhileLocked, powerOpenWhileHeld, entry with unlock, expected
        { "shutdown", false, false, true, false, true, false, false, "", RemotePowerRefusal.None },
        { "shutdown", true, false, true, false, true, false, false, "", RemotePowerRefusal.Locked },
        { "shutdown", true, false, true, false, true, true, false, "", RemotePowerRefusal.None },
        { "restart", false, true, true, false, true, true, false, "", RemotePowerRefusal.ProfileHeld },
        { "restart", false, true, true, false, true, true, true, "", RemotePowerRefusal.None },
        // The picker comes first, like in the bridge.
        { "restart", true, true, true, false, true, false, false, "", RemotePowerRefusal.ProfileHeld },
        { "sleep", false, false, false, false, true, true, true, "", RemotePowerRefusal.AreaOff },
        { "sleep", true, false, false, false, true, false, true, "", RemotePowerRefusal.Locked },
        // An entry that asks for the lock code stays out of reach on a locked couch, also where the call itself is open.
        { "sleep", true, false, true, false, true, true, true, "standby", RemotePowerRefusal.Locked },
        { "sleep", false, false, true, false, true, true, true, "standby", RemotePowerRefusal.None },
        { "restart", true, false, true, false, true, true, true, "standby", RemotePowerRefusal.None },
        // PC mode: someone works at the desktop.
        { "shutdown", false, false, true, true, true, true, true, "", RemotePowerRefusal.PcMode },
        { "sleep", true, false, true, true, true, true, true, "", RemotePowerRefusal.PcMode },
        // The owner's setting comes before everything but the action list.
        { "shutdown", true, true, false, true, false, false, false, "", RemotePowerRefusal.Disabled },
        { "restart", false, false, true, false, false, true, true, "", RemotePowerRefusal.Disabled },
        { "reboot", false, false, true, false, false, true, true, "", RemotePowerRefusal.UnknownAction }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_gate_refuses_with_its_own_reason(string action, bool locked, bool held, bool area, bool pcMode, bool enabled,
        bool openWhileLocked, bool openWhileHeld, string entryWithUnlock, RemotePowerRefusal expected)
    {
        var context = new RemotePowerContext(locked, held, area, pcMode, enabled);
        IReadOnlySet<string> entries = entryWithUnlock.Length == 0 ? None : new HashSet<string> { entryWithUnlock };
        Assert.Equal(expected, RemotePower.Decide(action, context, openWhileLocked ? WithPower : None, openWhileHeld ? WithPower : None, entries));
    }

    [Fact]
    public void Pc_mode_and_the_setting_refuse_with_the_real_lists_too()
    {
        foreach (var action in RemotePower.Actions)
        {
            Assert.Equal(RemotePowerRefusal.PcMode, RemotePower.Decide(action, Open with { PcMode = true }));
            Assert.Equal(RemotePowerRefusal.Disabled, RemotePower.Decide(action, Open with { Enabled = false }));
            Assert.Equal(RemotePowerRefusal.Disabled, RemotePower.Decide(action, Open with { Enabled = false, PcMode = true, Locked = true }));
        }
    }

    [Fact]
    public void The_setting_is_on_by_default()
    {
        Assert.True(new AppConfig().RemotePower);
        Assert.True(new RemotePowerContext(false, false, true).Enabled);
        Assert.False(new RemotePowerContext(false, false, true).PcMode);
    }

    [Fact]
    public void A_power_entry_that_asks_for_the_couch_lock_is_refused_on_a_locked_couch()
    {
        // None of the phone's actions is such an entry today (a contract test pins EntriesWithUnlock against power.js); the entries that are
        // stay out of reach, and the mapping goes to the couch's own entry ids.
        Assert.Equal("standby", RemotePower.EntryId("sleep"));
        Assert.Equal("shutdown", RemotePower.EntryId("shutdown"));
        Assert.Equal("restart", RemotePower.EntryId("restart"));
        foreach (var action in RemotePower.Actions) Assert.DoesNotContain(RemotePower.EntryId(action), RemotePower.EntriesWithUnlock);
        Assert.Contains("restart-pc", RemotePower.EntriesWithUnlock);
    }

    [Fact]
    public void Refusals_have_texts_in_both_languages()
    {
        foreach (var refusal in Enum.GetValues<RemotePowerRefusal>().Where(r => r != RemotePowerRefusal.None))
        {
            var key = RemotePower.RefusalKey(refusal);
            Assert.NotEqual(key, Strings.GetIn("de", key));
            Assert.NotEqual(key, Strings.GetIn("en", key));
        }
        Assert.Equal("remote.power.pcMode", RemotePower.RefusalKey(RemotePowerRefusal.PcMode));
        Assert.Equal("remote.power.disabled", RemotePower.RefusalKey(RemotePowerRefusal.Disabled));
        Assert.Equal("Der PC ist im Desktop-Modus – Ausschalten nur am PC.", Strings.GetIn("de", "remote.power.pcMode"));
        foreach (var key in new[] { "remote.power.tooOften", "remote.power.coolingDown", "remote.power.failed", "remote.power.expired",
                     "remote.power.shutdown", "remote.power.restart", "remote.power.sleep" })
        {
            Assert.NotEqual(key, Strings.GetIn("de", key));
            Assert.NotEqual(key, Strings.GetIn("en", key));
        }
    }

    [Fact]
    public void A_ticket_confirms_once_for_the_same_phone_and_action()
    {
        var tickets = new RemotePowerTickets();
        var ticket = tickets.Issue("phone-a", "shutdown", Now);

        Assert.Equal(TicketCheck.Valid, tickets.Redeem("phone-a", "shutdown", ticket, Now.AddSeconds(2)));
        // Replay: the same request again does nothing.
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-a", "shutdown", ticket, Now.AddSeconds(25)));
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-a", "shutdown", ticket, Now.AddMinutes(5)));
    }

    [Fact]
    public void A_ticket_does_not_count_for_another_phone_or_action_and_is_spent_by_the_try()
    {
        var tickets = new RemotePowerTickets();
        var ticket = tickets.Issue("phone-a", "sleep", Now);
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-b", "sleep", ticket, Now.AddSeconds(2)));
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-a", "sleep", ticket, Now.AddSeconds(3)));

        ticket = tickets.Issue("phone-a", "sleep", Now.AddSeconds(4));
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-a", "shutdown", ticket, Now.AddSeconds(6)));
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-a", "sleep", ticket, Now.AddSeconds(7)));
    }

    [Fact]
    public void A_double_tap_never_confirms_and_an_old_question_runs_out()
    {
        var tickets = new RemotePowerTickets();
        var ticket = tickets.Issue("phone-a", "shutdown", Now);
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-a", "shutdown", ticket, Now.AddMilliseconds(200)));
        // Spent: a second, slower tap needs a new question.
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-a", "shutdown", ticket, Now.AddSeconds(2)));

        ticket = tickets.Issue("phone-a", "shutdown", Now.AddSeconds(3));
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-a", "shutdown", ticket, Now.AddSeconds(3) + RemotePowerTickets.Lifetime + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Wrong_missing_or_made_up_tickets_fail()
    {
        var tickets = new RemotePowerTickets();
        tickets.Issue("phone-a", "restart", Now);
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-a", "restart", null, Now.AddSeconds(2)));
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-a", "restart", "", Now.AddSeconds(2)));
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-a", "restart", new string('0', 32), Now.AddSeconds(2)));
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-a", "restart", new string('a', 4000), Now.AddSeconds(2)));
    }

    [Fact]
    public void A_new_question_replaces_the_phones_open_one()
    {
        var tickets = new RemotePowerTickets();
        var first = tickets.Issue("phone-a", "restart", Now);
        var second = tickets.Issue("phone-a", "shutdown", Now.AddSeconds(1));
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-a", "restart", first, Now.AddSeconds(3)));
        Assert.Equal(TicketCheck.Valid, tickets.Redeem("phone-a", "shutdown", second, Now.AddSeconds(3)));
    }

    [Fact]
    public void After_an_action_ran_nothing_more_for_a_while_from_any_phone()
    {
        var tickets = new RemotePowerTickets();
        var a = tickets.Issue("phone-a", "sleep", Now);
        var b = tickets.Issue("phone-b", "shutdown", Now);
        Assert.Equal(TicketCheck.Valid, tickets.Redeem("phone-a", "sleep", a, Now.AddSeconds(2)));
        Assert.Equal(TicketCheck.CoolingDown, tickets.Redeem("phone-b", "shutdown", b, Now.AddSeconds(3)));
        // The ticket went with that try.
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-b", "shutdown", b, Now.AddSeconds(4)));
        Assert.True(tickets.CoolingDown(Now.AddSeconds(4)));
        Assert.False(tickets.CoolingDown(Now.AddSeconds(2) + RemotePowerTickets.Cooldown));
    }

    [Fact]
    public void An_action_that_did_not_run_lifts_the_pause()
    {
        var tickets = new RemotePowerTickets();
        var a = tickets.Issue("phone-a", "sleep", Now);
        Assert.Equal(TicketCheck.Valid, tickets.Redeem("phone-a", "sleep", a, Now.AddSeconds(2), out var pause));
        Assert.NotEqual(0, pause);
        Assert.True(tickets.CoolingDown(Now.AddSeconds(3)));
        tickets.Failed(0);
        Assert.True(tickets.CoolingDown(Now.AddSeconds(3)));
        tickets.Failed(pause);
        Assert.False(tickets.CoolingDown(Now.AddSeconds(3)));
        var b = tickets.Issue("phone-a", "sleep", Now.AddSeconds(3));
        Assert.Equal(TicketCheck.Valid, tickets.Redeem("phone-a", "sleep", b, Now.AddSeconds(5)));
    }

    [Fact]
    public void A_slow_failure_does_not_lift_the_pause_of_a_later_action()
    {
        var tickets = new RemotePowerTickets();
        var a = tickets.Issue("phone-a", "restart", Now);
        Assert.Equal(TicketCheck.Valid, tickets.Redeem("phone-a", "restart", a, Now.AddSeconds(2), out var slow));
        // Phone A's restart hangs; its pause runs out and phone B's shutdown runs.
        var b = tickets.Issue("phone-b", "shutdown", Now.AddSeconds(22));
        Assert.Equal(TicketCheck.Valid, tickets.Redeem("phone-b", "shutdown", b, Now.AddSeconds(24), out var later));
        Assert.NotEqual(slow, later);
        // Then A's restart fails: B's pause stays.
        tickets.Failed(slow);
        Assert.True(tickets.CoolingDown(Now.AddSeconds(30)));
        tickets.Failed(later);
        Assert.False(tickets.CoolingDown(Now.AddSeconds(30)));
    }

    [Fact]
    public void The_setting_is_neither_harmless_nor_for_the_assistant()
    {
        // Behind the couch lock like every other setting, and no assistant tool may switch it.
        Assert.DoesNotContain("remotePower", LockPolicy.HarmlessSettings);
        Assert.DoesNotContain("remotePower", CouchHud.Core.Assistant.ToolCatalog.SettingKeys);
    }

    [Fact]
    public void Too_many_requests_from_one_phone_are_paused_without_blocking_the_others()
    {
        var tickets = new RemotePowerTickets();
        for (var i = 0; i < RemotePowerTickets.MaxAttempts; i++) Assert.True(tickets.Count("phone-a", Now.AddSeconds(i)));
        Assert.False(tickets.Count("phone-a", Now.AddSeconds(11)));
        Assert.True(tickets.Count("phone-b", Now.AddSeconds(11)));
        var ticket = tickets.Issue("phone-b", "restart", Now.AddSeconds(11));
        Assert.Equal(TicketCheck.Valid, tickets.Redeem("phone-b", "restart", ticket, Now.AddSeconds(13)));
        Assert.True(tickets.Count("phone-a", Now.AddMinutes(2)));
    }

    [Fact]
    public void A_rate_limited_confirmation_still_spends_its_ticket()
    {
        var tickets = new RemotePowerTickets();
        var ticket = tickets.Issue("phone-a", "restart", Now);
        for (var i = 0; i < RemotePowerTickets.MaxAttempts; i++) tickets.Count("phone-a", Now.AddSeconds(-50));
        Assert.Equal(TicketCheck.TooOften, tickets.Redeem("phone-a", "restart", ticket, Now.AddSeconds(2)));
        // Once those requests left the minute, the same request (its ticket still young enough) is no good any more.
        Assert.Equal(TicketCheck.Invalid, tickets.Redeem("phone-a", "restart", ticket, Now.AddSeconds(12)));
    }

    [Fact]
    public void Unpaired_or_wrong_tokens_find_no_phone()
    {
        var (pairing, token) = Paired("Wohnzimmer");

        Assert.Equal("Wohnzimmer", pairing.Find(token, Now.AddMinutes(1))?.Name);
        Assert.Null(pairing.Find(null, Now));
        Assert.Null(pairing.Find("", Now));
        Assert.Null(pairing.Find(token + "0", Now));
        Assert.Null(pairing.Find(new string('a', 64), Now));
        Assert.Null(pairing.Find(new string('a', 4000), Now));
        Assert.Null(pairing.Find(token, Now + Pairing.TokenLifetime + TimeSpan.FromDays(1)));
    }

    [Fact]
    public void The_log_line_names_the_phone_but_no_secret()
    {
        var (pairing, token) = Paired("Handy\r\n[remote] fake");
        var device = pairing.Find(token, Now)!;

        var line = RemotePower.LogLine("shutdown", device, "runs");
        Assert.StartsWith("[remote] power shutdown from phone ", line);
        Assert.DoesNotContain(token, line);
        Assert.DoesNotContain(device.TokenHash, line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("?", RemotePower.LogLine("format c:", device, "refused"));
    }

    [Theory]
    [InlineData("Pixel 8", "Pixel 8")]
    [InlineData("  Pixel\t8  ", "Pixel8")]
    [InlineData("Evil\u202Egnp.exe", "Evilgnp.exe")]
    [InlineData("a\u200Bb\u200Dc\u2066d\u2069e\uFEFF", "abcde")]
    [InlineData("line\u2028break\u2029", "linebreak")]
    [InlineData("\u0007\u001B[31mred", "[31mred")]
    public void Phone_names_lose_control_and_format_characters(string name, string clean)
    {
        Assert.Equal(clean, RemotePower.CleanName(name));
    }

    [Fact]
    public void Phone_names_are_capped_and_never_empty()
    {
        Assert.Equal(RemotePower.MaxNameLength, RemotePower.CleanName(new string('x', 500)).Length);
        // A surrogate pair is not cut in half at the limit.
        var emoji = RemotePower.CleanName(new string('x', RemotePower.MaxNameLength - 1) + "\U0001F600");
        Assert.Equal(RemotePower.MaxNameLength - 1, emoji.Length);
        Assert.Equal(Strings.Get("remote.defaultDevice"), RemotePower.CleanName("\u202E\u200B\r\n "));
        Assert.Equal(Strings.Get("remote.defaultDevice"), RemotePower.CleanName(null));
    }

    // The endpoints (RemotePowerApi, what RemoteHost maps to /api/power/prepare and /api/power), with a fake HUD: nothing is ever switched off.

    [Fact]
    public async Task Unpaired_wrong_token_or_foreign_origin_get_401_before_the_body_is_read()
    {
        var (api, target, token, _) = Api();
        var read = 0;
        Func<Task<JsonNode?>> body = () => { read++; return Task.FromResult<JsonNode?>(new JsonObject { ["action"] = "shutdown" }); };

        foreach (var (trusted, given) in new[] { (true, (string?)null), (true, ""), (true, token + "0"), (true, new string('a', 64)), (false, token) })
        {
            Assert.Equal(401, (await api.PrepareAsync(trusted, given, body)).Status);
            Assert.Equal(401, (await api.RunAsync(trusted, given, body)).Status);
        }
        Assert.Equal(0, read);
        Assert.Equal(0, target.Checks);
        Assert.Empty(target.Ran);
    }

    [Theory]
    [InlineData("""{"action":"logoff"}""")]
    [InlineData("""{"action":"Shutdown"}""")]
    [InlineData("""{"action":["shutdown"]}""")]
    [InlineData("""{"action":1}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    public async Task Unknown_actions_get_400_and_never_reach_the_hud(string json)
    {
        var (api, target, token, _) = Api();
        var reply = await api.PrepareAsync(true, token, Body(json));
        Assert.Equal(400, reply.Status);
        Assert.Equal("unknown", Field(reply, "code"));
        Assert.Equal(0, target.Checks);
        Assert.Equal(400, (await api.PrepareAsync(true, token, () => Task.FromResult<JsonNode?>(null))).Status);
    }

    [Fact]
    public async Task Prepare_then_confirm_runs_once_and_a_replay_gets_409()
    {
        var (api, target, token, clock) = Api();
        target.Game = "Hollow Knight";
        var prepared = await api.PrepareAsync(true, token, Body("""{"action":"restart"}"""));
        Assert.Equal(200, prepared.Status);
        Assert.Equal("Hollow Knight", Field(prepared, "game"));
        var ticket = Field(prepared, "ticket")!;

        clock.Advance(TimeSpan.FromSeconds(2));
        var confirm = Body($$"""{"action":"restart","ticket":"{{ticket}}"}""");
        Assert.Equal(200, (await api.RunAsync(true, token, confirm)).Status);
        Assert.Equal(["restart"], target.Ran);

        clock.Advance(TimeSpan.FromSeconds(1));
        var replay = await api.RunAsync(true, token, confirm);
        Assert.Equal(409, replay.Status);
        Assert.Equal("ticket", Field(replay, "code"));
        Assert.Equal(["restart"], target.Ran);
    }

    [Fact]
    public async Task A_confirmation_needs_the_ticket_of_its_own_question()
    {
        var (api, target, token, clock) = Api();
        var ticket = Field(await api.PrepareAsync(true, token, Body("""{"action":"sleep"}""")), "ticket")!;
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(409, (await api.RunAsync(true, token, Body($$"""{"action":"shutdown","ticket":"{{ticket}}"}"""))).Status);
        Assert.Equal(409, (await api.RunAsync(true, token, Body("""{"action":"sleep"}"""))).Status);
        Assert.Equal(409, (await api.RunAsync(true, token, Body($$"""{"action":"sleep","ticket":"{{new string('0', 32)}}"}"""))).Status);
        Assert.Empty(target.Ran);
    }

    [Theory]
    [InlineData(RemotePowerRefusal.Locked)]
    [InlineData(RemotePowerRefusal.ProfileHeld)]
    [InlineData(RemotePowerRefusal.AreaOff)]
    [InlineData(RemotePowerRefusal.PcMode)]
    [InlineData(RemotePowerRefusal.Disabled)]
    public async Task A_refusal_of_the_hud_gets_403_with_its_text_and_no_ticket(RemotePowerRefusal refusal)
    {
        var (api, target, token, _) = Api();
        target.Refusal = refusal;
        var reply = await api.PrepareAsync(true, token, Body("""{"action":"shutdown"}"""));
        Assert.Equal(403, reply.Status);
        Assert.Equal(refusal.ToString(), Field(reply, "code"));
        Assert.Equal(Strings.Get(RemotePower.RefusalKey(refusal)), Field(reply, "error"));
        Assert.Null(Field(reply, "ticket"));
        Assert.Empty(target.Ran);
    }

    [Fact]
    public async Task The_couch_locked_between_question_and_confirmation_refuses_and_lifts_the_pause()
    {
        var (api, target, token, clock) = Api();
        var ticket = Field(await api.PrepareAsync(true, token, Body("""{"action":"shutdown"}""")), "ticket")!;
        clock.Advance(TimeSpan.FromSeconds(2));
        target.RunRefusal = RemotePowerRefusal.PcMode;
        var reply = await api.RunAsync(true, token, Body($$"""{"action":"shutdown","ticket":"{{ticket}}"}"""));
        Assert.Equal(403, reply.Status);
        Assert.Equal("PcMode", Field(reply, "code"));
        Assert.False(api.Tickets.CoolingDown(clock.Now));
    }

    [Fact]
    public async Task A_failed_call_gets_a_fixed_text_and_lifts_the_pause()
    {
        var (api, target, token, clock) = Api();
        var ticket = Field(await api.PrepareAsync(true, token, Body("""{"action":"sleep"}""")), "ticket")!;
        clock.Advance(TimeSpan.FromSeconds(2));
        target.Fail = true;
        var reply = await api.RunAsync(true, token, Body($$"""{"action":"sleep","ticket":"{{ticket}}"}"""));
        Assert.Equal(500, reply.Status);
        Assert.Equal("failed", Field(reply, "code"));
        Assert.Equal(Strings.Get("remote.power.failed"), Field(reply, "error"));
        Assert.False(api.Tickets.CoolingDown(clock.Now));

        // Right away a new question works.
        Assert.Equal(200, (await api.PrepareAsync(true, token, Body("""{"action":"sleep"}"""))).Status);
    }

    [Fact]
    public async Task After_an_action_the_next_question_gets_its_own_pause_text()
    {
        var (api, target, token, clock) = Api();
        var ticket = Field(await api.PrepareAsync(true, token, Body("""{"action":"restart"}""")), "ticket")!;
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(200, (await api.RunAsync(true, token, Body($$"""{"action":"restart","ticket":"{{ticket}}"}"""))).Status);

        var reply = await api.PrepareAsync(true, token, Body("""{"action":"shutdown"}"""));
        Assert.Equal(429, reply.Status);
        Assert.Equal("cooldown", Field(reply, "code"));
        Assert.Equal(Strings.Get("remote.power.coolingDown"), Field(reply, "error"));

        clock.Advance(RemotePowerTickets.Cooldown);
        Assert.Equal(200, (await api.PrepareAsync(true, token, Body("""{"action":"shutdown"}"""))).Status);
    }

    [Fact]
    public async Task Refused_and_unknown_requests_count_towards_the_rate_limit()
    {
        var (api, target, token, _) = Api();
        target.Refusal = RemotePowerRefusal.Locked;
        for (var i = 0; i < RemotePowerTickets.MaxAttempts / 2; i++) Assert.Equal(403, (await api.PrepareAsync(true, token, Body("""{"action":"shutdown"}"""))).Status);
        for (var i = 0; i < RemotePowerTickets.MaxAttempts / 2; i++) Assert.Equal(400, (await api.PrepareAsync(true, token, Body("""{"action":"nope"}"""))).Status);
        var checks = target.Checks;

        target.Refusal = RemotePowerRefusal.None;
        var reply = await api.PrepareAsync(true, token, Body("""{"action":"shutdown"}"""));
        Assert.Equal(429, reply.Status);
        Assert.Equal("tooOften", Field(reply, "code"));
        // Counted before the HUD is asked.
        Assert.Equal(checks, target.Checks);
        Assert.Equal(429, (await api.RunAsync(true, token, Body("""{"action":"shutdown","ticket":"x"}"""))).Status);
        Assert.Empty(target.Ran);
    }

    [Fact]
    public async Task Another_phones_ticket_does_not_work()
    {
        var (api, target, token, clock) = Api();
        var other = api.Pairing_TryPair("Tablet", clock.Now);
        var ticket = Field(await api.PrepareAsync(true, token, Body("""{"action":"shutdown"}""")), "ticket")!;
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(409, (await api.RunAsync(true, other, Body($$"""{"action":"shutdown","ticket":"{{ticket}}"}"""))).Status);
        Assert.Equal(409, (await api.RunAsync(true, token, Body($$"""{"action":"shutdown","ticket":"{{ticket}}"}"""))).Status);
        Assert.Empty(target.Ran);
    }

    [Fact]
    public async Task Log_lines_carry_neither_token_nor_ticket()
    {
        var log = new List<string>();
        var (api, target, token, clock) = Api(log);
        var ticket = Field(await api.PrepareAsync(true, token, Body("""{"action":"shutdown"}""")), "ticket")!;
        clock.Advance(TimeSpan.FromSeconds(2));
        await api.RunAsync(true, token, Body($$"""{"action":"shutdown","ticket":"{{ticket}}"}"""));
        await api.RunAsync(true, token, Body($$"""{"action":"shutdown","ticket":"{{ticket}}"}"""));
        target.Refusal = RemotePowerRefusal.Locked;
        await api.PrepareAsync(true, token, Body("""{"action":"restart"}"""));
        Assert.NotEmpty(log);
        Assert.All(log, line =>
        {
            Assert.DoesNotContain(token, line);
            Assert.DoesNotContain(ticket, line);
        });
    }

    private static (Pairing Pairing, string Token) Paired(string name)
    {
        var devices = new List<RemoteDevice>();
        var pairing = new Pairing(() => devices, change => change(devices));
        return (pairing, pairing.TryPair(pairing.CurrentCode(Now), name, Now)!);
    }

    private static (TestApi Api, FakeTarget Target, string Token, FakeClock Clock) Api(List<string>? log = null)
    {
        var devices = new List<RemoteDevice>();
        var pairing = new Pairing(() => devices, change => change(devices));
        var clock = new FakeClock(Now);
        var token = pairing.TryPair(pairing.CurrentCode(Now), "Pixel", Now)!;
        var target = new FakeTarget();
        return (new TestApi(pairing, target, log ?? [], clock), target, token, clock);
    }

    private static Func<Task<JsonNode?>> Body(string json) => () => Task.FromResult(JsonNode.Parse(json));

    private static string? Field(RemotePowerReply reply, string name) =>
        reply.Body is null ? null : JsonSerializer.SerializeToNode(reply.Body)?[name]?.ToString();

    private sealed class FakeClock(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; private set; } = now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class TestApi(Pairing pairing, IRemotePowerTarget target, List<string> log, FakeClock clock)
    {
        private readonly RemotePowerApi _api = new(pairing, target, log.Add, () => clock.Now);
        public RemotePowerTickets Tickets => _api.Tickets;
        public Task<RemotePowerReply> PrepareAsync(bool trusted, string? token, Func<Task<JsonNode?>> body) => _api.PrepareAsync(trusted, token, body);
        public Task<RemotePowerReply> RunAsync(bool trusted, string? token, Func<Task<JsonNode?>> body) => _api.RunAsync(trusted, token, body);
        public string Pairing_TryPair(string name, DateTimeOffset now) => pairing.TryPair(pairing.CurrentCode(now), name, now)!;
    }

    /// <summary>The HUD as the endpoints see it; it only records what it was asked to do.</summary>
    private sealed class FakeTarget : IRemotePowerTarget
    {
        public RemotePowerRefusal Refusal { get; set; }
        public RemotePowerRefusal RunRefusal { get; set; }
        public bool Fail { get; set; }
        public string? Game { get; set; }
        public int Checks { get; private set; }
        public List<string> Ran { get; } = [];
        public bool PowerEnabled => true;

        public bool GameRunning { get; set; }

        public Task<(RemotePowerRefusal Refusal, string? Game, bool GameRunning)> PowerCheckAsync(string action, RemoteDevice device)
        {
            Checks++;
            return Task.FromResult((Refusal, Game, GameRunning || Game is not null));
        }

        public Task<RemotePowerResult> PowerAsync(string action, RemoteDevice device)
        {
            if (RunRefusal != RemotePowerRefusal.None) return Task.FromResult(new RemotePowerResult(RunRefusal, false));
            if (Fail) return Task.FromResult(new RemotePowerResult(RemotePowerRefusal.None, true));
            Ran.Add(action);
            return Task.FromResult(RemotePowerResult.Ran);
        }
    }
}
