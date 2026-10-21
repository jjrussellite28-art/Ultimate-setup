using CouchHud.Core.Guard;
using CouchHud.Service.Contracts;

namespace CouchHud.ServiceClient.Tests;

public class ReplyParserTests
{
    private static ServiceReply Parse(string json) => ReplyParser.Parse(FakePipeServer.Raw(json));

    [Fact]
    public void Ok_and_refusal_are_read()
    {
        Assert.Equal(CallOutcome.Ok, Parse("""{"v":1,"ok":true,"code":"ok"}""").Outcome);
        var refused = Parse("""{"v":1,"ok":false,"code":"denied"}""");
        Assert.Equal(CallOutcome.Refused, refused.Outcome);
        Assert.Equal("denied", refused.Code);
        Assert.Equal(CallOutcome.Ok, Parse("""{"v":1,"ok":true,"code":"streamReady"}""").Outcome);
    }

    [Fact]
    public void Data_comes_back_typed()
    {
        var reply = ReplyParser.Parse(FakePipeServer.Hello("1.2.3"));
        Assert.True(reply.Ok);
        Assert.Equal("1.2.3", reply.DataAs(ProtocolJsonContext.Default.HelloData)!.ServiceVersion);
        // Data of another shape is "none", never an exception.
        Assert.Null(reply.DataAs(ProtocolJsonContext.Default.JobData));
    }

    [Theory]
    [InlineData("""{"v":1,"ok":true,"code":"denied"}""")]          // ok with an error code
    [InlineData("""{"v":1,"ok":false,"code":"ok"}""")]             // failure that says ok
    [InlineData("""{"v":2,"ok":true,"code":"ok"}""")]              // other protocol
    [InlineData("""{"ok":true,"code":"ok"}""")]                    // no version
    [InlineData("""{"v":1,"ok":true,"code":"ok","extra":1}""")]    // unknown field
    [InlineData("""{"v":1,"ok":true,"ok":true,"code":"ok"}""")]    // a field twice
    [InlineData("""{"v":1,"ok":"yes","code":"ok"}""")]
    [InlineData("""{"v":1,"ok":false,"code":"failed","data":{"x":1}}""")]  // data with a failure
    [InlineData("""{"v":1,"ok":false,"code":"no such code!"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""not json""")]
    [InlineData("""{"v":1,"ok":true,"code":"ok"} trailing""")]
    public void Anything_against_the_protocol_is_inconsistent(string json)
    {
        Assert.Equal(CallOutcome.Inconsistent, Parse(json).Outcome);
    }

    [Fact]
    public void Empty_is_inconsistent() => Assert.Equal(CallOutcome.Inconsistent, ReplyParser.Parse([]).Outcome);

    [Fact]
    public void An_unknown_code_of_a_newer_service_is_a_plain_failure()
    {
        var reply = Parse("""{"v":1,"ok":false,"code":"somethingNew"}""");
        Assert.Equal(CallOutcome.Refused, reply.Outcome);
        Assert.Equal(FailureKind.Failed, ErrorMap.Classify(reply));
    }
}

public class ServerCheckTests
{
    [Fact]
    public void Only_a_running_service_with_a_process_is_connected_to()
    {
        Assert.Null(ServerCheck.BeforeConnect(new ScmInfo(ScmState.Running, 1234)));
        Assert.Equal(CallOutcome.Unreachable, ServerCheck.BeforeConnect(new ScmInfo(ScmState.Running, 0)));
        Assert.Equal(CallOutcome.NotInstalled, ServerCheck.BeforeConnect(ScmInfo.NotInstalled));
        Assert.Equal(CallOutcome.NotRunning, ServerCheck.BeforeConnect(new ScmInfo(ScmState.Stopped, 0)));
        Assert.Equal(CallOutcome.NotRunning, ServerCheck.BeforeConnect(new ScmInfo(ScmState.StartPending, 0)));
        Assert.Equal(CallOutcome.Unreachable, ServerCheck.BeforeConnect(ScmInfo.Unknown));
    }

    [Fact]
    public void A_service_that_stopped_because_its_pipe_was_taken_means_squatting()
    {
        var info = new ScmInfo(ScmState.Stopped, 0, Squatting.ServiceSpecificError, Squatting.ExitCode);
        Assert.True(info.StoppedBySquatting);
        Assert.Equal(CallOutcome.Squatted, ServerCheck.BeforeConnect(info));
        // Another service-specific code (pipe failed for another reason) is just "not running".
        Assert.Equal(CallOutcome.NotRunning, ServerCheck.BeforeConnect(new ScmInfo(ScmState.Stopped, 0, Squatting.ServiceSpecificError, 0xC0D2)));
    }

    [Theory]
    [InlineData(100, 100, ServerVerdict.Service)]
    [InlineData(100, 200, ServerVerdict.Retry)]
    [InlineData(null, 100, ServerVerdict.Squatted)]
    [InlineData(0, 100, ServerVerdict.Squatted)]
    [InlineData(0, 0, ServerVerdict.Squatted)]
    [InlineData(100, 0, ServerVerdict.Squatted)]
    public void The_server_must_be_the_services_process(int? server, int service, ServerVerdict expected)
    {
        Assert.Equal(expected, ServerCheck.AfterConnect(server, service));
    }
}

public class ErrorMapTests
{
    [Fact]
    public void Every_code_of_the_protocol_has_a_kind()
    {
        foreach (var code in ErrorCodes.All)
        {
            var kind = ErrorMap.ClassifyCode(code);
            Assert.Equal(code is ErrorCodes.Ok or ErrorCodes.StreamReady, kind == FailureKind.None);
        }
    }

    [Theory]
    [InlineData(ErrorCodes.Busy, FailureKind.Retry)]
    [InlineData(ErrorCodes.RateLimited, FailureKind.Retry)]
    [InlineData(ErrorCodes.NotImplemented, FailureKind.NotSupported)]
    [InlineData(ErrorCodes.Denied, FailureKind.Refused)]
    [InlineData(ErrorCodes.NeedGrant, FailureKind.NeedGrant)]
    [InlineData(ErrorCodes.UnsafeInstall, FailureKind.UnsafeInstall)]
    [InlineData(ErrorCodes.BadArgument, FailureKind.Invalid)]
    [InlineData(ErrorCodes.Timeout, FailureKind.Timeout)]
    [InlineData(ErrorCodes.VerificationFailed, FailureKind.Failed)]
    public void Codes_map_to_their_kind(string code, FailureKind kind) => Assert.Equal(kind, ErrorMap.ClassifyCode(code));

    [Fact]
    public void Squatting_and_contradiction_are_alarms_unavailability_is_not()
    {
        Assert.True(ErrorMap.IsAlarm(FailureKind.Squatted));
        Assert.True(ErrorMap.IsAlarm(FailureKind.Inconsistent));
        Assert.False(ErrorMap.IsAlarm(FailureKind.Unreachable));
        Assert.True(ErrorMap.IsUnavailable(FailureKind.NotRunning));
        Assert.False(ErrorMap.IsUnavailable(FailureKind.Refused));
    }

    [Fact]
    public void Every_kind_has_its_own_text_key()
    {
        var keys = Enum.GetValues<FailureKind>().Select(ErrorMap.MessageKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.StartsWith("service.error.", k));
    }

    [Fact]
    public void Every_text_key_exists_in_both_languages()
    {
        foreach (var key in ErrorMap.AllMessageKeys().Concat(ServiceTexts.StateKeys))
        {
            CouchHud.Core.Localization.Strings.Use("de");
            var de = CouchHud.Core.Localization.Strings.Get(key);
            CouchHud.Core.Localization.Strings.Use("en");
            var en = CouchHud.Core.Localization.Strings.Get(key);
            Assert.NotEqual(key, de);
            Assert.NotEqual(key, en);
        }
    }
}

public class ProbeRulesTests
{
    private static readonly Version App = new(0, 4, 1);

    [Fact]
    public void The_scm_alone_says_what_is_wrong()
    {
        Assert.Null(ServiceProbe.FromScm(new ScmInfo(ScmState.Running, 5)));
        Assert.Equal(ServiceState.NotInstalled, ServiceProbe.FromScm(ScmInfo.NotInstalled));
        Assert.Equal(ServiceState.Disabled, ServiceProbe.FromScm(new ScmInfo(ScmState.Stopped, 0, Disabled: true)));
        Assert.Equal(ServiceState.Stopped, ServiceProbe.FromScm(new ScmInfo(ScmState.Stopped, 0)));
        Assert.Equal(ServiceState.Squatted, ServiceProbe.FromScm(new ScmInfo(ScmState.Stopped, 0, Squatting.ServiceSpecificError, Squatting.ExitCode)));
        Assert.Equal(ServiceState.Starting, ServiceProbe.FromScm(new ScmInfo(ScmState.StartPending, 0)));
        Assert.Equal(ServiceState.NotResponding, ServiceProbe.FromScm(ScmInfo.Unknown));
    }

    [Fact]
    public void Hello_decides_between_running_old_and_unsafe()
    {
        Assert.Equal(ServiceState.Running, ServiceProbe.FromHello(ReplyParser.Parse(FakePipeServer.Hello("0.4.1")), App, out var v));
        Assert.Equal("0.4.1", v);
        Assert.Equal(ServiceState.Running, ServiceProbe.FromHello(ReplyParser.Parse(FakePipeServer.Hello("0.5.0")), App, out _));
        Assert.Equal(ServiceState.OldVersion, ServiceProbe.FromHello(ReplyParser.Parse(FakePipeServer.Hello("0.4.0")), App, out _));
        Assert.Equal(ServiceState.OldVersion, ServiceProbe.FromHello(ReplyParser.Parse(FakePipeServer.Hello("0.4.1", protocol: 2)), App, out _));
        Assert.Equal(ServiceState.UnsafeInstall, ServiceProbe.FromHello(ReplyParser.Parse(FakePipeServer.Hello("0.4.1", unsafeInstall: true)), App, out _));
        Assert.Equal(ServiceState.OldVersion, ServiceProbe.FromHello(ReplyParser.Parse(FakePipeServer.Fail(ErrorCodes.NotImplemented)), App, out _));
        Assert.Equal(ServiceState.Squatted, ServiceProbe.FromHello(ServiceReplyOf(CallOutcome.Squatted), App, out _));
        Assert.Equal(ServiceState.Inconsistent, ServiceProbe.FromHello(ReplyParser.Parse(FakePipeServer.Ok()), App, out _));
        Assert.Equal(ServiceState.NotResponding, ServiceProbe.FromHello(ServiceReplyOf(CallOutcome.Timeout), App, out _));
    }

    [Theory]
    [InlineData("0.4.0", true)]
    [InlineData("0.4.1", false)]
    [InlineData("0.4.1.7", false)]
    [InlineData("1.0", false)]
    [InlineData("garbage", true)]
    public void Older_versions_are_recognised(string service, bool older) => Assert.Equal(older, ServiceProbe.IsOlder(service, App));

    internal static ServiceReply ServiceReplyOf(CallOutcome outcome) =>
        outcome == CallOutcome.Squatted ? ReplyParserTestsAccess.Local(outcome) : ReplyParserTestsAccess.Local(outcome);
}

/// <summary>Reaches the internal factory of local replies.</summary>
internal static class ReplyParserTestsAccess
{
    public static ServiceReply Local(CallOutcome outcome) => ServiceReply.Local(outcome);
}

public class FailClosedTests
{
    [Fact]
    public void Nothing_opens_without_a_healthy_service()
    {
        foreach (var state in Enum.GetValues<ServiceState>())
        {
            var healthy = state == ServiceState.Running;
            var noService = state == ServiceState.NotExpected;
            Assert.Equal(healthy || noService, FailClosed.MayUnlock(couchLockOn: true, state));
            Assert.Equal(healthy || noService, FailClosed.MayRemoveShell(state));
            Assert.Equal(healthy, FailClosed.MayReleasePolicies(state));
            // Without a lock there is nothing to keep closed.
            Assert.True(FailClosed.MayUnlock(couchLockOn: false, state));
        }
    }

    [Fact]
    public void Squatting_and_contradiction_are_alarms()
    {
        Assert.Equal(ServiceHealth.Alarm, FailClosed.Classify(ServiceState.Squatted));
        Assert.Equal(ServiceHealth.Alarm, FailClosed.Classify(ServiceState.Inconsistent));
        Assert.Equal(ServiceHealth.Unavailable, FailClosed.Classify(ServiceState.NotInstalled));
        Assert.Equal(ServiceHealth.Unavailable, FailClosed.Classify(ServiceState.OldVersion));
        Assert.True(FailClosed.OfferRepair(ServiceState.Stopped));
        Assert.True(FailClosed.OfferRepair(ServiceState.Squatted));
        Assert.False(FailClosed.OfferRepair(ServiceState.Running));
        Assert.False(FailClosed.OfferRepair(ServiceState.NotExpected));
        Assert.False(FailClosed.OfferRepair(ServiceState.Unknown));
    }

    [Fact]
    public void An_unreachable_service_warns_once_then_locks_windows_after_the_grace()
    {
        var watch = new LockWatch(TimeSpan.FromSeconds(30));
        var t0 = DateTimeOffset.UnixEpoch;
        Assert.Equal(LockWatchAction.Warn, watch.Observe(t0, policiesWanted: true, ServiceState.NotResponding));
        Assert.Equal(LockWatchAction.None, watch.Observe(t0.AddSeconds(10), true, ServiceState.NotResponding));
        Assert.Equal(LockWatchAction.LockWindows, watch.Observe(t0.AddSeconds(30), true, ServiceState.Stopped));
        // Once per incident.
        Assert.Equal(LockWatchAction.None, watch.Observe(t0.AddSeconds(90), true, ServiceState.Stopped));
        // Healthy again ends it; the next outage is a new incident.
        Assert.Equal(LockWatchAction.None, watch.Observe(t0.AddSeconds(100), true, ServiceState.Running));
        Assert.False(watch.InIncident);
        Assert.Equal(LockWatchAction.Warn, watch.Observe(t0.AddSeconds(110), true, ServiceState.NotInstalled));
    }

    [Fact]
    public void Squatting_locks_windows_at_once()
    {
        var watch = new LockWatch(TimeSpan.FromSeconds(30));
        Assert.Equal(LockWatchAction.LockWindows, watch.Observe(DateTimeOffset.UnixEpoch, true, ServiceState.Squatted));
        Assert.Equal(LockWatchAction.None, watch.Observe(DateTimeOffset.UnixEpoch.AddSeconds(1), true, ServiceState.Squatted));
    }

    [Fact]
    public void A_starting_service_gets_the_grace_without_a_warning()
    {
        var watch = new LockWatch(TimeSpan.FromSeconds(30));
        var t0 = DateTimeOffset.UnixEpoch;
        Assert.Equal(LockWatchAction.None, watch.Observe(t0, true, ServiceState.Starting));
        Assert.Equal(LockWatchAction.None, watch.Observe(t0.AddSeconds(20), true, ServiceState.Starting));
        Assert.Equal(LockWatchAction.LockWindows, watch.Observe(t0.AddSeconds(31), true, ServiceState.Starting));
    }

    [Fact]
    public void Nothing_happens_when_the_lock_does_not_need_the_service()
    {
        var watch = new LockWatch(TimeSpan.FromSeconds(30));
        Assert.Equal(LockWatchAction.None, watch.Observe(DateTimeOffset.UnixEpoch, policiesWanted: false, ServiceState.Squatted));
        Assert.Equal(LockWatchAction.None, watch.Observe(DateTimeOffset.UnixEpoch, true, ServiceState.NotExpected));
        Assert.Equal(LockWatchAction.None, watch.Observe(DateTimeOffset.UnixEpoch, true, ServiceState.Unknown));
    }
}

public class EmergencyRepairTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    [Fact]
    public void Only_values_with_the_locks_own_data_are_stuck()
    {
        var registry = new FakeRegistry();
        registry.WriteDword(LockHardening.ExplorerKey, "NoRun", 1);
        registry.WriteDword(LockHardening.ExplorerKey, "NoControlPanel", 0);   // the user's own value: stays
        registry.WriteString(LockHardening.DisallowRunKey, "1", "cmd.exe");
        registry.WriteString(LockHardening.DisallowRunKey, "2", "mygame.exe");  // the user's own entry: stays
        registry.WriteDword(LockHardening.SystemKey, "DisableTaskMgr", 1);
        var stuck = EmergencyRepair.StuckValues(registry, ["lock", "taskmgr"]);
        Assert.Contains(new EmergencyRepair.StuckValue(LockHardening.ExplorerKey, "NoRun"), stuck);
        Assert.Contains(new EmergencyRepair.StuckValue(LockHardening.DisallowRunKey, "1"), stuck);
        Assert.Contains(new EmergencyRepair.StuckValue(LockHardening.SystemKey, "DisableTaskMgr"), stuck);
        Assert.DoesNotContain(stuck, v => v.Name == "NoControlPanel");
        Assert.DoesNotContain(stuck, v => v.Name == "2");
    }

    [Fact]
    public void The_cleanup_is_one_cmd_without_autorun_and_only_reg_delete_in_the_own_hive()
    {
        var launch = EmergencyRepair.StuckPolicyCleanup(@"C:\Windows\system32", Sid,
            [new(LockHardening.ExplorerKey, "NoRun"), new(LockHardening.DisallowRunKey, "3")])!;
        Assert.Equal(@"C:\Windows\system32\cmd.exe", launch.FileName);
        Assert.StartsWith("/d /s /c \"", launch.Arguments);
        Assert.Contains($"\"C:\\Windows\\system32\\reg.exe\" delete \"HKU\\{Sid}\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer\" /v NoRun /f", launch.Arguments);
        Assert.Contains(@"\Policies\Explorer\DisallowRun"" /v 3 /f", launch.Arguments);
        Assert.DoesNotContain("%", launch.Arguments);
        Assert.DoesNotContain("HKCU", launch.Arguments);
    }

    [Fact]
    public void Nothing_to_delete_starts_nothing() => Assert.Null(EmergencyRepair.StuckPolicyCleanup(@"C:\Windows\system32", Sid, []));

    [Theory]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoRun & calc")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoRun\"")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "%PATH%")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "SomethingElse")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowRun", "x1")]
    [InlineData(@"HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoRun")]
    [InlineData(@"HKCU\Software\Classes\exefile\shell\open\command", "NoRun")]
    public void Anything_outside_the_fixed_list_is_refused(string key, string name)
    {
        Assert.Throws<ArgumentException>(() => EmergencyRepair.StuckPolicyCleanup(@"C:\Windows\system32", Sid, [new(key, name)]));
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-21-1-2-3-4\" & calc")]
    [InlineData("")]
    public void Only_a_user_sid(string sid)
    {
        Assert.Throws<ArgumentException>(() => EmergencyRepair.StuckPolicyCleanup(@"C:\Windows\system32", sid, [new(LockHardening.ExplorerKey, "NoRun")]));
    }

    [Fact]
    public void A_strange_system_directory_is_refused()
    {
        Assert.Throws<ArgumentException>(() => EmergencyRepair.StuckPolicyCleanup(@"C:\Win&dows", Sid, [new(LockHardening.ExplorerKey, "NoRun")]));
    }

    [Fact]
    public void The_repair_runs_the_script_from_the_program_folder_without_profile()
    {
        var launch = EmergencyRepair.RepairService(@"C:\Windows\system32", @"C:\Program Files\CouchHud");
        Assert.Equal(@"C:\Windows\system32\cmd.exe", launch.FileName);
        Assert.Equal(@"/d /c """"C:\Program Files\CouchHud\repair-service.cmd""""", launch.Arguments);
        Assert.Throws<ArgumentException>(() => EmergencyRepair.RepairService(@"C:\Windows\system32", "C:\\x\" & calc"));
        Assert.Throws<ArgumentException>(() => EmergencyRepair.RepairService(@"C:\Windows\system32", @"C:\%TEMP%"));
        Assert.Throws<ArgumentException>(() => EmergencyRepair.RepairService(@"C:\Windows\system32", @"C:\a&b"));
    }
}
