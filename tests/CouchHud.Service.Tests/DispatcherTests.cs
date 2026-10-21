using System.Buffers.Binary;
using System.Text;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Dispatch;
using CouchHud.Service.Pipe;

namespace CouchHud.Service.Tests;

/// <summary>End to end over an in-memory stream with a fake caller: frames, validation, authorization, handlers.</summary>
public class DispatcherTests
{
    private static readonly string Grant = Convert.ToBase64String(new byte[32]);

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Frame(string json) => FrameCodec.Encode(Json(json));

    private static byte[] Ping() => ProtocolJson.SerializeRequest(Operation.Ping);

    [Fact]
    public async Task Ping_answers_ok_with_the_time()
    {
        var harness = new Harness();
        var answer = Assert.Single(await harness.ExchangeAsync(Callers.User(), FrameCodec.Encode(Ping())));
        var response = ProtocolJson.DeserializeResponse<PingData>(answer)!;
        Assert.True(response.Ok);
        Assert.Equal(123, response.Data!.ServerTimeMs);
    }

    [Fact]
    public async Task Hello_names_the_implemented_operations()
    {
        var harness = new Harness();
        var answer = Assert.Single(await harness.ExchangeAsync(Callers.User(), FrameCodec.Encode(ProtocolJson.SerializeRequest(Operation.Hello))));
        var hello = ProtocolJson.DeserializeResponse<HelloData>(answer)!.Data!;
        Assert.Equal(Protocol.Version, hello.Protocol);
        Assert.Equal("0.4.1", hello.ServiceVersion);
        Assert.Equal(["GetRecentEvents", "Hello", "Ping", "Status"], hello.Capabilities);
        Assert.False(hello.UnsafeInstall);
    }

    [Fact]
    public async Task Status_reports_uptime()
    {
        var harness = new Harness();
        var answer = Assert.Single(await harness.ExchangeAsync(Callers.User(), FrameCodec.Encode(ProtocolJson.SerializeRequest(Operation.Status))));
        Assert.Equal(7, ProtocolJson.DeserializeResponse<StatusData>(answer)!.Data!.UptimeSeconds);
    }

    [Fact]
    public async Task Several_requests_on_one_connection_are_answered_in_order_up_to_the_limit()
    {
        var harness = new Harness(limits: new ConnectionLimits(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), 3));
        var frames = Enumerable.Range(0, 5).Select(_ => FrameCodec.Encode(Ping())).ToArray();
        var answers = await harness.ExchangeAsync(Callers.User(), frames);
        Assert.Equal(3, answers.Count);
    }

    [Theory]
    [InlineData(Operation.ApplyLockPolicies, """{"groups":["lock"]}""")]
    [InlineData(Operation.PauseServices, """{"names":["GamingServices"]}""")]
    [InlineData(Operation.InstallWindowsUpdates, null)]
    [InlineData(Operation.VerifyCode, """{"kind":"couch","sequence":["a","b","x","y","a","b"]}""")]
    [InlineData(Operation.MigrateTasks, null)]
    public async Task Allowed_operations_without_a_handler_are_not_implemented(Operation op, string? args)
    {
        var harness = new Harness();
        var json = args is null ? $$"""{"v":1,"op":"{{op}}"}""" : $$"""{"v":1,"op":"{{op}}","args":{{args}}}""";
        var response = await harness.OneAsync(Callers.User(), Json(json));
        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.NotImplemented, response.Code);
    }

    [Fact]
    public async Task An_unknown_operation_is_refused()
    {
        var response = await new Harness().OneAsync(Callers.User(), Json("""{"v":1,"op":"RunCommand","args":{"cmd":"calc"}}"""));
        Assert.Equal(ErrorCodes.UnknownOperation, response.Code);
    }

    [Theory]
    [InlineData("""{"v":1,"op":"Ping","sid":"S-1-5-18"}""", ErrorCodes.UnknownField)]
    [InlineData("""{"v":2,"op":"Ping"}""", ErrorCodes.BadVersion)]
    [InlineData("""not json""", ErrorCodes.BadJson)]
    [InlineData("""{"v":1,"op":"ApplyLockPolicies","args":{"groups":["everything"]}}""", ErrorCodes.BadArgument)]
    [InlineData("""{"v":1,"op":"PauseServices","args":{"names":["WinDefend"]}}""", ErrorCodes.BadArgument)]
    [InlineData("""{"v":1,"op":"ApplyLockPolicies","args":{"groups":["lock"],"sid":"S-1-5-21-1-2-3-1002"}}""", ErrorCodes.UnknownField)]
    public async Task Validator_failures_come_back_as_their_code(string json, string code)
    {
        var harness = new Harness();
        var response = await harness.OneAsync(Callers.User(), Json(json));
        Assert.False(response.Ok);
        Assert.Equal(code, response.Code);
        Assert.Contains("[reject]", harness.Log.All);
    }

    [Fact]
    public async Task A_length_above_the_limit_is_too_large_and_ends_the_connection()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
        var answers = await new Harness().ExchangeAsync(Callers.User(), header, FrameCodec.Encode(Ping()));
        var response = ProtocolJson.DeserializeResponse(Harness.Strip(Assert.Single(answers)))!;
        Assert.Equal(ErrorCodes.TooLarge, response.Code);
    }

    [Fact]
    public async Task A_zero_length_is_a_bad_frame()
    {
        var answers = await new Harness().ExchangeAsync(Callers.User(), new byte[4]);
        Assert.Equal(ErrorCodes.BadFrame, ProtocolJson.DeserializeResponse(Harness.Strip(Assert.Single(answers)))!.Code);
    }

    [Fact]
    public async Task A_frame_that_ends_early_is_truncated()
    {
        var frame = FrameCodec.Encode(Ping());
        var answers = await new Harness().ExchangeAsync(Callers.User(), frame[..^3]);
        Assert.Equal(ErrorCodes.Truncated, ProtocolJson.DeserializeResponse(Harness.Strip(Assert.Single(answers)))!.Code);
    }

    [Fact]
    public async Task A_client_that_sends_nothing_gets_a_timeout()
    {
        var harness = new Harness(limits: new ConnectionLimits(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), 8));
        var stream = new StallingStream([5, 0]);   // half a header, then nothing
        var run = harness.Connections.HandleAsync(stream, Callers.User(), CancellationToken.None);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))));
        var answer = Assert.Single(Harness.ReadFrames(stream.Written.ToArray()));
        Assert.Equal(ErrorCodes.Timeout, ProtocolJson.DeserializeResponse(Harness.Strip(answer))!.Code);
    }

    [Fact]
    public async Task An_unauthorized_caller_is_refused_before_anything_is_read()
    {
        var harness = new Harness();
        var stream = new StallingStream([]);
        await harness.Connections.HandleAsync(stream, Callers.User() with { IntegrityLevel = IntegrityLevels.Low }, CancellationToken.None);
        var answer = Assert.Single(Harness.ReadFrames(stream.Written.ToArray()));
        Assert.Equal(ErrorCodes.Denied, ProtocolJson.DeserializeResponse(Harness.Strip(answer))!.Code);
        Assert.Contains("integrity below medium", harness.Log.All);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_or_network_callers_get_denied_even_for_ping(bool network)
    {
        var caller = network ? Callers.User() with { IsNetwork = true } : null;
        var response = await new Harness().OneAsync(caller, Ping());
        Assert.Equal(ErrorCodes.Denied, response.Code);
    }

    [Fact]
    public async Task The_dispatcher_itself_refuses_an_unauthorized_caller_too()
    {
        var harness = new Harness();
        var answer = await harness.Dispatcher.DispatchAsync(Callers.User() with { IsAppContainer = true }, Ping(), CancellationToken.None);
        Assert.Equal(ErrorCodes.Denied, ProtocolJson.DeserializeResponse(answer)!.Code);
    }

    [Fact]
    public async Task Release_needs_a_grant_from_a_standard_user()
    {
        var grants = new FakeGrants(Grant);
        var harness = new Harness(grants: grants, extraChecks: c => c.Register(StateCheck.GroupsSetByService, Harness.AllowAll), extraHandlers: h => h[Operation.ReleaseLockPolicies] =
            (_, _) => ValueTask.FromResult(ProtocolJson.Serialize(Response<LockPoliciesData>.Success(new LockPoliciesData(["lock"])))));
        var without = await harness.OneAsync(Callers.User(), Json("""{"v":1,"op":"ReleaseLockPolicies","args":{"groups":["lock"]}}"""));
        Assert.Equal(ErrorCodes.NeedGrant, without.Code);

        var wrong = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
        var bad = await harness.OneAsync(Callers.User(), Json($$$"""{"v":1,"op":"ReleaseLockPolicies","grant":"{{{wrong}}}","args":{"groups":["lock"]}}"""));
        Assert.Equal(ErrorCodes.NeedGrant, bad.Code);

        var good = await harness.OneAsync(Callers.User(), Json($$$"""{"v":1,"op":"ReleaseLockPolicies","grant":"{{{Grant}}}","args":{"groups":["lock"]}}"""));
        Assert.True(good.Ok);
        Assert.Equal(1, grants.Consumed);

        var admin = await harness.OneAsync(Callers.ElevatedAdmin(), Json("""{"v":1,"op":"ReleaseLockPolicies","args":{"groups":["lock"]}}"""));
        Assert.True(admin.Ok);
        Assert.Equal(1, grants.Consumed);   // an admin does not use up a grant
    }

    [Fact]
    public async Task Without_grant_support_no_grant_is_ever_valid()
    {
        var harness = new Harness(extraHandlers: h => h[Operation.SetRemoteFirewall] = Harness.Ok);
        var response = await harness.OneAsync(Callers.User(), Json($$$"""{"v":1,"op":"SetRemoteFirewall","grant":"{{{Grant}}}","args":{"ports":[8080]}}"""));
        Assert.Equal(ErrorCodes.NeedGrant, response.Code);
    }

    [Fact]
    public async Task Release_all_accounts_is_denied_to_a_user_and_reaches_the_handler_lookup_for_an_admin()
    {
        var harness = new Harness();
        Assert.Equal(ErrorCodes.Denied, (await harness.OneAsync(Callers.User(), ProtocolJson.SerializeRequest(Operation.ReleaseAllAccounts))).Code);
        Assert.Equal(ErrorCodes.NotImplemented, (await harness.OneAsync(Callers.ElevatedAdmin(), ProtocolJson.SerializeRequest(Operation.ReleaseAllAccounts))).Code);
    }

    [Fact]
    public async Task Code_attempts_are_rate_limited_per_account()
    {
        var harness = new Harness(extraHandlers: h => h[Operation.VerifyCode] = Harness.Ok);
        var verify = Json("""{"v":1,"op":"VerifyCode","args":{"kind":"couch","sequence":["a","b","x","y","a","b"]}}""");
        for (var i = 0; i < 5; i++) Assert.True((await harness.OneAsync(Callers.User(), verify)).Ok);
        Assert.Equal(ErrorCodes.RateLimited, (await harness.OneAsync(Callers.User(), verify)).Code);
        Assert.True((await harness.OneAsync(Callers.User(Callers.OtherSid), verify)).Ok);

        for (var i = 0; i < 3; i++) harness.Limiter.RecordFailure(Callers.OtherSid, Operation.VerifyCode);
        Assert.Equal(ErrorCodes.LockedOut, (await harness.OneAsync(Callers.User(Callers.OtherSid), verify)).Code);
    }

    [Fact]
    public async Task An_unsafe_install_answers_only_health()
    {
        var harness = new Harness { UnsafeInstall = true };
        Assert.True((await harness.OneAsync(Callers.User(), Ping())).Ok);
        Assert.True((await harness.OneAsync(Callers.User(), ProtocolJson.SerializeRequest(Operation.Hello))).Ok);
        Assert.Equal(ErrorCodes.UnsafeInstall, (await harness.OneAsync(Callers.User(), Json("""{"v":1,"op":"ApplyLockPolicies","args":{"groups":["lock"]}}"""))).Code);
        Assert.Equal(ErrorCodes.UnsafeInstall, (await harness.OneAsync(Callers.User(), ProtocolJson.SerializeRequest(Operation.GetRecentEvents))).Code);
    }

    [Fact]
    public async Task Too_many_connections_of_one_account_are_busy()
    {
        var harness = new Harness();
        var leases = Enumerable.Range(0, 4).Select(_ => harness.Quota.TryEnter(Callers.UserSid)).ToList();
        Assert.All(leases, Assert.NotNull);
        Assert.Equal(ErrorCodes.Busy, (await harness.OneAsync(Callers.User(), Ping())).Code);
        Assert.True((await harness.OneAsync(Callers.User(Callers.OtherSid), Ping())).Ok);
        leases[0]!.Dispose();
        leases[0]!.Dispose();   // twice is once
        Assert.True((await harness.OneAsync(Callers.User(), Ping())).Ok);
    }

    [Fact]
    public async Task Recent_events_are_only_the_callers_own()
    {
        var harness = new Harness();
        await harness.OneAsync(Callers.User(Callers.OtherSid), ProtocolJson.SerializeRequest(Operation.ReleaseAllAccounts));
        await harness.OneAsync(Callers.User(), Json("""{"v":1,"op":"Bogus"}"""));

        var answer = Assert.Single(await harness.ExchangeAsync(Callers.User(), FrameCodec.Encode(Json("""{"v":1,"op":"GetRecentEvents","args":{"count":10}}"""))));
        var events = ProtocolJson.DeserializeResponse<EventsData>(answer)!.Data!.Events;
        var only = Assert.Single(events);
        Assert.Contains("unknownOperation", only.Message);
        Assert.DoesNotContain(events, e => e.Message.Contains("ReleaseAllAccounts"));
    }

    [Fact]
    public async Task A_handler_exception_is_internal_and_logs_only_the_type()
    {
        var harness = new Harness(extraHandlers: h => h[Operation.MigrateTasks] = (_, _) => throw new InvalidOperationException("secret detail hunter2"));
        var response = await harness.OneAsync(Callers.User(), ProtocolJson.SerializeRequest(Operation.MigrateTasks));
        Assert.Equal(ErrorCodes.Internal, response.Code);
        Assert.Contains("InvalidOperationException", harness.Log.All);
        Assert.DoesNotContain("hunter2", harness.Log.All);
    }

    [Fact]
    public async Task A_handler_that_hangs_times_out()
    {
        var harness = new Harness(extraHandlers: h => h[Operation.MigrateTasks] = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        });
        var response = await harness.OneAsync(Callers.User(), ProtocolJson.SerializeRequest(Operation.MigrateTasks));
        Assert.Equal(ErrorCodes.Timeout, response.Code);
    }

    [Fact]
    public async Task Handlers_get_the_account_from_the_token_only()
    {
        RequestContext? seen = null;
        var harness = new Harness(extraHandlers: h => h[Operation.ApplyLockPolicies] = (context, _) =>
        {
            seen = context;
            return ValueTask.FromResult(ProtocolJson.Serialize(Response.Success()));
        });
        await harness.OneAsync(Callers.User(Callers.OtherSid), Json("""{"v":1,"op":"ApplyLockPolicies","args":{"groups":["lock"]}}"""));
        Assert.Equal(Callers.OtherSid, seen!.Caller.Sid);
    }

    [Fact]
    public async Task Passwords_codes_and_grants_never_reach_the_log()
    {
        var harness = new Harness { UnsafeInstall = true };
        const string password = "Tr0ub4dor&3-secret";
        await harness.OneAsync(Callers.User(), Json($$$"""{"v":1,"op":"SetAutoLogon","args":{"password":"{{{password}}}"}}"""));
        harness.UnsafeInstall = false;
        await harness.OneAsync(Callers.User(), Json($$$"""{"v":1,"op":"SetAutoLogon","args":{"password":"{{{password}}}","extra":1}}"""));
        await harness.OneAsync(Callers.User(), Json($$$"""{"v":1,"op":"SetRemoteFirewall","grant":"{{{Grant}}}","args":{"ports":[8080]}}"""));
        await harness.OneAsync(Callers.User(), Json("""{"v":1,"op":"VerifyCode","args":{"kind":"couch","sequence":["up","up","down","down","left","right"]}}"""));
        var log = harness.Log.All;
        Assert.DoesNotContain(password, log);
        Assert.DoesNotContain(Grant, log);
        Assert.DoesNotContain("\"up\"", log);
    }

    [Fact]
    public async Task A_handler_that_ignores_cancellation_still_answers_timeout_and_keeps_its_slot()
    {
        var never = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness(handlerTimeout: TimeSpan.FromMilliseconds(100),
            extraHandlers: h => h[Operation.MigrateTasks] = (_, _) => new ValueTask<byte[]>(never.Task));   // ignores its token

        for (var i = 0; i < harness.Work.PerAccount; i++)
        {
            Assert.Equal(ErrorCodes.Timeout, (await harness.OneAsync(Callers.User(), ProtocolJson.SerializeRequest(Operation.MigrateTasks))).Code);
        }
        // The abandoned handlers still count: the account gets "busy", not another handler; other accounts are not affected.
        Assert.Equal(harness.Work.PerAccount, harness.Work.RunningFor(Callers.UserSid));
        Assert.Equal(ErrorCodes.Busy, (await harness.OneAsync(Callers.User(), ProtocolJson.SerializeRequest(Operation.MigrateTasks))).Code);
        Assert.True((await harness.OneAsync(Callers.User(), Ping())).Ok);
        Assert.Equal(ErrorCodes.Timeout, (await harness.OneAsync(Callers.User(Callers.OtherSid), ProtocolJson.SerializeRequest(Operation.MigrateTasks))).Code);

        // When the work really ends, the slots come back.
        never.SetResult(ProtocolJson.Serialize(Response.Success()));
        for (var i = 0; i < 100 && harness.Work.RunningFor(Callers.UserSid) > 0; i++) await Task.Delay(20);
        Assert.Equal(0, harness.Work.RunningFor(Callers.UserSid));
    }

    [Fact]
    public async Task A_synchronously_blocking_handler_is_abandoned_too()
    {
        using var release = new ManualResetEventSlim();
        var harness = new Harness(handlerTimeout: TimeSpan.FromMilliseconds(100), extraHandlers: h => h[Operation.MigrateTasks] = (_, _) =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return ValueTask.FromResult(ProtocolJson.Serialize(Response.Success()));
        });
        var started = DateTime.UtcNow;
        Assert.Equal(ErrorCodes.Timeout, (await harness.OneAsync(Callers.User(), ProtocolJson.SerializeRequest(Operation.MigrateTasks))).Code);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        release.Set();
    }

    [Fact]
    public async Task An_operation_without_a_handler_costs_no_attempt_and_no_grant()
    {
        var grants = new FakeGrants(Grant);
        var harness = new Harness(grants: grants);
        var verify = Json("""{"v":1,"op":"VerifyCode","args":{"kind":"couch","sequence":["a","b","x","y","a","b"]}}""");
        for (var i = 0; i < 20; i++) Assert.Equal(ErrorCodes.NotImplemented, (await harness.OneAsync(Callers.User(), verify)).Code);
        Assert.Equal(RateVerdict.Allow, harness.Limiter.TryAcquire(Callers.UserSid, Operation.VerifyCode).Verdict);

        var firewall = Json($$$"""{"v":1,"op":"SetRemoteFirewall","grant":"{{{Grant}}}","args":{"ports":[8080]}}""");
        Assert.Equal(ErrorCodes.NotImplemented, (await harness.OneAsync(Callers.User(), firewall)).Code);
        Assert.Equal(0, grants.Consumed);
    }

    [Fact]
    public async Task A_rate_limited_request_does_not_use_up_the_grant()
    {
        var grants = new FakeGrants(Grant);
        var harness = new Harness(grants: grants, extraHandlers: h => h[Operation.SetRemoteFirewall] = Harness.Ok);
        var firewall = Json($$$"""{"v":1,"op":"SetRemoteFirewall","grant":"{{{Grant}}}","args":{"ports":[8080]}}""");
        for (var i = 0; i < 10; i++) harness.Limiter.TryAcquire(Callers.UserSid, Operation.SetRemoteFirewall);
        Assert.Equal(ErrorCodes.RateLimited, (await harness.OneAsync(Callers.User(), firewall)).Code);
        Assert.Equal(0, grants.Consumed);
    }

    [Fact]
    public async Task Refusals_are_throttled_per_account_with_a_summary()
    {
        var harness = new Harness();
        for (var i = 0; i < 50; i++) await harness.OneAsync(Callers.User(), Json("""{"v":1,"op":"Bogus"}"""));
        await harness.OneAsync(Callers.User(Callers.OtherSid), Json("""{"v":1,"op":"Bogus"}"""));
        var lines = harness.Log.Lines.ToList();
        Assert.Equal(Logging.DenyLog.PerWindow, lines.Count(l => l.Contains(Callers.UserSid)));
        Assert.Single(lines, l => l.Contains(Callers.OtherSid));

        harness.Clock.Advance(Logging.DenyLog.Window);
        await harness.OneAsync(Callers.User(), Json("""{"v":1,"op":"Bogus"}"""));
        Assert.Contains(harness.Log.Lines, l => l.Contains("30 suppressed") && l.Contains(Callers.UserSid));
    }

    [Fact]
    public async Task Log_lines_name_only_sid_and_session()
    {
        var harness = new Harness();
        await harness.OneAsync(Callers.User(), ProtocolJson.SerializeRequest(Operation.ReleaseAllAccounts));
        var line = Assert.Single(harness.Log.Lines);
        Assert.Contains($"sid={Callers.UserSid} session=1", line);
        Assert.DoesNotContain("pid", line);
        Assert.DoesNotContain(@"C:\", line);
    }
}