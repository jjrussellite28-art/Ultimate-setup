using System.Text;
using CouchHud.Core.Security;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.State;

namespace CouchHud.Service.Tests;

/// <summary>P9: the code store on disk (codes, modes, lockouts, the administrator backoff) and the validator's new fields.</summary>
public class CodeStoreTests
{
    [Fact]
    public async Task A_damaged_code_file_closes_everything_and_is_kept()
    {
        using var rig = new CodeRig();
        File.WriteAllText(Path.Combine(rig.Folder, CodeStore.FileName), "{\"version\":1,\"accounts\":{\"S-1-5-18\":{}}}");
        var store = CodeStore.Open(rig.Folder, rig.Log, rig.Clock, () => 1_000_000, Codes.FastVerify);
        Assert.False(store.IsAvailable);
        Assert.True(store.IsEnrolled(Callers.UserSid));
        Assert.True(store.Has(Callers.UserSid, CodeKind.Parent));
        Assert.Equal(CodeCheck.Unavailable, store.Verify(Callers.UserSid, CodeKind.Couch, Codes.A).Result);
        Assert.Equal(CodeSet.Unavailable, store.Set(Callers.UserSid, CodeKind.Couch, Codes.HashA, enroll: true));
        Assert.Contains("S-1-5-18", File.ReadAllText(Path.Combine(rig.Folder, CodeStore.FileName)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":2,\"accounts\":{}}")]
    [InlineData("{\"version\":1,\"accounts\":{\"S-1-5-21-1-2-3-1001\":{\"couch\":{\"hash\":\"plain\"}}}}")]
    [InlineData("{\"version\":1,\"accounts\":{\"S-1-5-21-1-2-3-1001\":{\"couch\":{\"hash\":\"x\",\"failures\":-1}}}}")]
    [InlineData("{\"version\":1,\"accounts\":{},\"extra\":1}")]
    public void Anything_that_is_not_exactly_right_is_refused(string content)
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, CodeStore.FileName), content);
        Assert.False(CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify).IsAvailable);
    }

    [Fact]
    public void No_trusted_folder_means_unavailable_and_fail_closed()
    {
        var store = CodeStore.Open(null, new CapturingSink(), new FakeClock(), () => 1);
        Assert.False(store.IsAvailable);
        Assert.True(store.IsEnrolled(Callers.UserSid));
    }

    [Fact]
    public void Writes_are_atomic_and_the_hash_format_is_checked()
    {
        // Exactly the format the shell's Passcode.Hash writes, nothing else (not the marker the config keeps afterwards).
        Assert.True(RequestValidator.IsPasscodeHash(Passcode.Hash(Codes.A)));
        Assert.False(RequestValidator.IsPasscodeHash(Passcode.HeldByService));
        using var dir = new TempDir();
        var store = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        Assert.Equal(CodeSet.BadHash, store.Set(Callers.UserSid, CodeKind.Couch, "pbkdf2-sha256$1000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", enroll: true));
        Assert.Equal(CodeSet.Saved, store.Set(Callers.UserSid, CodeKind.Couch, Codes.HashA, enroll: true));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        Assert.Equal(CodeSet.Exists, store.Set(Callers.UserSid, CodeKind.Couch, Codes.HashB, enroll: true));
        Assert.Equal(CodeSet.Missing, store.Set(Callers.UserSid, CodeKind.Parent, Codes.HashB, enroll: false));
        Assert.Equal(CodeSet.Unavailable, store.Set("S-1-5-18", CodeKind.Couch, Codes.HashA, enroll: true));
        // Read back from disk.
        Assert.True(CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify).Has(Callers.UserSid, CodeKind.Couch));
    }

    [Fact]
    public void A_try_is_written_before_it_is_checked()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, CodeStore.FileName);
        string? during = null;
        var store = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, (s, h) =>
        {
            during = File.ReadAllText(file);
            return Codes.FastVerify(s, h);
        });
        store.Set(Callers.UserSid, CodeKind.Couch, Codes.HashA, enroll: true);
        store.Verify(Callers.UserSid, CodeKind.Couch, Codes.Wrong);
        // A service killed during the check has the try counted already.
        Assert.Contains("\"failures\": 1", during);
        // The right code is counted too (as the second try in a row) and the count is reset after the check.
        store.Verify(Callers.UserSid, CodeKind.Couch, Codes.A);
        Assert.Contains("\"failures\": 2", during);
        Assert.Contains("\"failures\": 0", File.ReadAllText(file));
    }

    [Fact]
    public void A_lockout_survives_a_restart_of_the_service_and_of_the_machine()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var store = CodeStore.Open(dir.Path, new CapturingSink(), clock, () => 5_000_000, Codes.FastVerify);
        store.Set(Callers.UserSid, CodeKind.Couch, Codes.HashA, enroll: true);
        for (var i = 0; i < CodeStore.FreeFailures + 1; i++)
        {
            store.Verify(Callers.UserSid, CodeKind.Couch, Codes.Wrong);
            clock.Advance(TimeSpan.FromSeconds(31));
        }
        // Six failures: 60 s, 31 s of it gone.
        var serviceRestart = CodeStore.Open(dir.Path, new CapturingSink(), clock, () => 5_000_000, Codes.FastVerify);
        var again = serviceRestart.Verify(Callers.UserSid, CodeKind.Couch, Codes.A);
        Assert.Equal(CodeCheck.LockedOut, again.Result);
        Assert.InRange(again.Wait.TotalSeconds, 28, 30);
        // After a reboot the monotonic clock starts anew: the wait starts again in full (never shorter).
        var rebootClock = new FakeClock { Now = TimeSpan.FromSeconds(10) };
        var machineRestart = CodeStore.Open(dir.Path, new CapturingSink(), rebootClock, () => 9_000_000, Codes.FastVerify);
        var afterBoot = machineRestart.Verify(Callers.UserSid, CodeKind.Couch, Codes.A);
        Assert.Equal(CodeCheck.LockedOut, afterBoot.Result);
        Assert.Equal(60, afterBoot.Wait.TotalSeconds, 0.5);
    }

    [Fact]
    public void The_real_pbkdf2_check_is_the_default()
    {
        using var dir = new TempDir();
        var store = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1);
        store.Set(Callers.UserSid, CodeKind.Couch, Codes.HashA, enroll: true);
        Assert.Equal(CodeCheck.Wrong, store.Verify(Callers.UserSid, CodeKind.Couch, Codes.B).Result);
        Assert.Equal(CodeCheck.Granted, store.Verify(Callers.UserSid, CodeKind.Couch, Codes.A).Result);
    }

    [Fact]
    public void The_number_of_accounts_is_bounded()
    {
        using var dir = new TempDir();
        var store = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        for (var i = 0; i < CodeStore.MaxAccounts; i++)
        {
            Assert.Equal(CodeSet.Saved, store.Set($"S-1-5-21-1111111111-2222222222-3333333333-{3000 + i}", CodeKind.Couch, Codes.HashA, enroll: true));
        }
        Assert.Equal(CodeSet.Unavailable, store.Set($"S-1-5-21-1111111111-2222222222-3333333333-{9999}", CodeKind.Couch, Codes.HashA, enroll: true));
    }

    [Fact]
    public void Modes_are_kept_and_an_unknown_mode_follows_the_couch_code()
    {
        using var dir = new TempDir();
        var store = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        Assert.Equal(new LockView(LockModes.Off, false, false), store.View(Callers.UserSid));
        Assert.False(store.IsEnrolled(Callers.UserSid));
        store.Set(Callers.UserSid, CodeKind.Couch, Codes.HashA, enroll: true);
        // Never set, but a couch code exists: counts as the code lock (fail-closed).
        Assert.Equal(LockModes.Code, store.View(Callers.UserSid).Mode);
        Assert.Null(store.StoredMode(Callers.UserSid));
        Assert.Equal(CodeSet.Saved, store.SetMode(Callers.OtherSid, LockModes.Windows));
        // The Windows lock without any code protects the couch too.
        Assert.True(store.IsEnrolled(Callers.OtherSid));
        Assert.False(store.HasAnyCode(Callers.OtherSid));
        var reopened = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        Assert.Equal(LockModes.Windows, reopened.StoredMode(Callers.OtherSid));
        Assert.Equal(CodeSet.Unavailable, reopened.SetMode(Callers.UserSid, "sometimes"));
    }

    [Fact]
    public void An_unreadable_store_is_the_strictest_view()
    {
        var store = CodeStore.Open(null, new CapturingSink(), new FakeClock(), () => 1);
        Assert.Equal(LockView.Closed, store.View(Callers.UserSid));
    }

    [Fact]
    public void Both_codes_at_once_and_a_code_set_with_approval_clears_the_other()
    {
        using var dir = new TempDir();
        var store = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        Assert.Equal(CodeSet.Saved, store.Set(Callers.UserSid, [CodeKind.Couch, CodeKind.Parent], Codes.HashA, enroll: true));
        Assert.Equal(CodeSet.Exists, store.Set(Callers.UserSid, [CodeKind.Couch, CodeKind.Parent], Codes.HashB, enroll: true));
        Assert.Equal(CodeSet.Saved, store.Set(Callers.UserSid, CodeKind.Couch, Codes.HashB, enroll: false, clearOther: true));
        Assert.False(store.Has(Callers.UserSid, CodeKind.Parent));
        Assert.True(store.Has(Callers.UserSid, CodeKind.Couch));
    }

    [Theory]
    [InlineData("""{"v":1,"op":"RequestWindowsGrant","args":{"scope":"unlock","password":"eA=="}}""")]
    [InlineData("""{"v":1,"op":"RequestWindowsGrant","args":{"scope":"unlock","ops":["SetAutoLogon"]}}""")]
    [InlineData("""{"v":1,"op":"RequestWindowsGrant","args":{"scope":"admin","name":"Admin","ops":["SetAutoLogon"]}}""")]
    [InlineData("""{"v":1,"op":"RequestWindowsGrant","args":{"scope":"admin","password":"eA==","ops":["SetAutoLogon"]}}""")]
    [InlineData("""{"v":1,"op":"RequestWindowsGrant","args":{"scope":"admin","name":"Admin","password":"eA=="}}""")]
    [InlineData("""{"v":1,"op":"RequestWindowsGrant","args":{"scope":"admin","name":"Admin","password":"eA==","ops":["Hello"]}}""")]
    [InlineData("""{"v":1,"op":"RequestWindowsGrant","args":{"scope":"admin","name":"Admin","password":"eA==","ops":["SetAutoLogon","SetAutoLogon","SetAutoLogon","SetAutoLogon","SetAutoLogon"]}}""")]
    [InlineData("""{"v":1,"op":"RequestWindowsGrant","args":{"scope":"admin","name":"a\u0001b","password":"eA==","ops":["SetAutoLogon"]}}""")]
    [InlineData("""{"v":1,"op":"RequestWindowsGrant","args":{"scope":"admin","name":"a\\b\\c","password":"eA==","ops":["SetAutoLogon"]}}""")]
    [InlineData("""{"v":1,"op":"RequestWindowsGrant","args":{"scope":"admin","name":"Admin","password":"eAA=","ops":["SetAutoLogon"]}}""")]
    [InlineData("""{"v":1,"op":"RequestWindowsGrant","args":{"scope":"admin","name":"Admin","password":"/w==","ops":["SetAutoLogon"]}}""")]
    [InlineData("""{"v":1,"op":"RequestWindowsGrant","args":{"scope":"parent"}}""")]
    [InlineData("""{"v":1,"op":"RequestWindowsGrant","args":{"scope":"admin","name":"Admin","password":"eA==","ops":["SetAutoLogon"],"domain":"PC"}}""")]
    [InlineData("""{"v":1,"op":"EnrollPasscode","args":{"hash":"x","kind":"couch"}}""")]
    [InlineData("""{"v":1,"op":"EnrollPasscode","args":{"hash":"pbkdf2-sha256$100000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=","kind":"admin"}}""")]
    [InlineData("""{"v":1,"op":"EnrollPasscode","args":{"kind":"couch","sequence":["a","b","x","y","up","down"]}}""")]
    [InlineData("""{"v":1,"op":"ChangePasscode","args":{"kind":"parent","sequence":["a","b","x","y","up","down"]}}""")]
    [InlineData("""{"v":1,"op":"EnrollPasscode","args":{"kind":"parent","sequence":["a","b"]}}""")]
    [InlineData("""{"v":1,"op":"VerifyCode","args":{"kind":"couch","sequence":["a","b","x","y","up","down"],"ops":["SetAutoLogon"]}}""")]
    [InlineData("""{"v":1,"op":"VerifyCode","args":{"kind":"parent","sequence":["a","b","x","y","up","down"],"ops":["ResetServiceState"]}}""")]
    [InlineData("""{"v":1,"op":"SetLockMode","args":{"mode":"kiosk"}}""")]
    public void The_validator_keeps_the_new_fields_tight(string json) =>
        Assert.False(new RequestValidator(_ => false).Validate(Encoding.UTF8.GetBytes(json)).Ok);

    [Theory]
    [InlineData("""{"v":1,"op":"RequestAdminApproval","args":{"ops":["SetAutoLogon","SetLockMode"]}}""")]
    [InlineData("""{"v":1,"op":"RequestAdminApproval","args":{"ops":[]}}""")]
    [InlineData("""{"v":1,"op":"CollectAdminApproval"}""")]
    [InlineData("""{"v":1,"op":"ChangePasscode","args":{"hash":"pbkdf2-sha256$100000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=","kind":"both"}}""")]
    [InlineData("""{"v":1,"op":"VerifyCode","args":{"kind":"parent","sequence":["a","b","x","y","up","down"],"ops":["SetAutoLogon"]}}""")]
    [InlineData("""{"v":1,"op":"SetLockMode","args":{"mode":"windows"}}""")]
    public void The_validator_takes_the_new_requests(string json) =>
        Assert.True(new RequestValidator(_ => false).Validate(Encoding.UTF8.GetBytes(json)).Ok);

    [Fact]
    public void Secrets_stay_out_of_to_string()
    {
        Assert.DoesNotContain("pbkdf2", new PasscodeHashArgs(Codes.HashA, CodeKinds.Parent).ToString());
        Assert.DoesNotContain("up", new VerifyCodeArgs(CodeKinds.Parent, Codes.A, ["SetAutoLogon"]).ToString());
    }
}
