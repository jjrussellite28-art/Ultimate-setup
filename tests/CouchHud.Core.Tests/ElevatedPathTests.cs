using System.Xml.Linq;
using CouchHud.Core.Guard;
using CouchHud.Core.Security;

namespace CouchHud.Core.Tests;

public class ElevatedGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-elev-" + Guid.NewGuid().ToString("N"));

    public ElevatedGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static GuardEntry Dword(string key, string name, string? original = null, string applied = "1") =>
        new(GuardKind.RegistryDword, key, name, original, applied);

    [Fact]
    public void The_fixed_places_of_the_lock_and_the_game_bar_are_accepted()
    {
        Assert.True(ElevatedGuard.Accepts(Dword(GameBarGuard.Key, GameBarGuard.Value, null, "0")));
        Assert.True(ElevatedGuard.Accepts(Dword(GameBarGuard.Key, GameBarGuard.Value, "1", "0")));
        Assert.True(ElevatedGuard.Accepts(Dword(LockHardening.ExplorerKey, "NoRun")));
        Assert.True(ElevatedGuard.Accepts(Dword(LockHardening.SystemKey, "DisableTaskMgr", "0")));
        Assert.True(ElevatedGuard.Accepts(Dword(GameBarGuard.CaptureKey, "AppCaptureEnabled", "1", "0")));
        Assert.True(ElevatedGuard.Accepts(Dword(@"hkcu\software\microsoft\gamebar", "usenexusforgamebarenabled")));
        Assert.True(ElevatedGuard.Accepts(new GuardEntry(GuardKind.RegistryString, LockHardening.DisallowRunKey, "4", null, "wt.exe")));
    }

    [Fact]
    public void Everything_else_is_refused()
    {
        // Machine-wide keys: an administrator process would write them.
        Assert.False(ElevatedGuard.Accepts(Dword(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "AutoAdminLogon")));
        Assert.False(ElevatedGuard.Accepts(Dword(@"HKLM\SOFTWARE\Policies\Evil", "Value")));
        // Any other HKCU place, even a plausible one.
        Assert.False(ElevatedGuard.Accepts(Dword(@"HKCU\Software\Evil", "Value")));
        Assert.False(ElevatedGuard.Accepts(Dword(LockHardening.ExplorerKey, "NoAutoUpdate")));
        Assert.False(ElevatedGuard.Accepts(Dword(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Evil")));
        // VDF files: a path of the file's choosing.
        Assert.False(ElevatedGuard.Accepts(new GuardEntry(GuardKind.VdfKey, @"C:\Windows\System32\evil.vdf", "a/b", "1", "0")));
        Assert.False(ElevatedGuard.Accepts(new GuardEntry(GuardKind.VdfKey, GameBarGuard.Key, GameBarGuard.Value, null, "0")));
        // String values: only our numbered DisallowRun entries, missing before, with one of our programs.
        Assert.False(ElevatedGuard.Accepts(new GuardEntry(GuardKind.RegistryString, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Evil", null, "cmd.exe")));
        Assert.False(ElevatedGuard.Accepts(new GuardEntry(GuardKind.RegistryString, LockHardening.DisallowRunKey, "1", "notepad.exe", "cmd.exe")));
        Assert.False(ElevatedGuard.Accepts(new GuardEntry(GuardKind.RegistryString, LockHardening.DisallowRunKey, "Evil", null, "cmd.exe")));
        Assert.False(ElevatedGuard.Accepts(new GuardEntry(GuardKind.RegistryString, LockHardening.DisallowRunKey, "1", null, "calc.exe")));
        // A DWORD that is not a number.
        Assert.False(ElevatedGuard.Accepts(Dword(GameBarGuard.Key, GameBarGuard.Value, "evil")));
    }

    [Fact]
    public void An_elevated_restore_takes_the_game_bar_and_leaves_the_planted_machine_wide_entry_alone()
    {
        var state = Path.Combine(_dir, "guard.json");
        var reg = new FakeRegistry();
        reg.WriteDword(GameBarGuard.Key, GameBarGuard.Value, 1);
        reg.WriteDword(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "AutoAdminLogon", 0);
        // The user's file as a program running as the user could have written it: the real change plus a planted one.
        new GuardSnapshot(reg, new FileVdfStore(), state).Apply(
        [
            GameBarGuard.Change,
            new GuardChange(GuardKind.RegistryDword, @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "AutoAdminLogon", "1")
        ]);
        Assert.Equal(0, reg.ReadDword(GameBarGuard.Key, GameBarGuard.Value));
        var log = new List<string>();

        var elevated = new GuardSnapshot(reg, new FileVdfStore(), state, log.Add, ElevatedGuard.Accepts);
        Assert.Empty(elevated.Restore());

        Assert.Equal(1, reg.ReadDword(GameBarGuard.Key, GameBarGuard.Value));
        // The planted entry was not written back (it would have set the original, 0, but it must not even be touched).
        Assert.Equal(1, reg.ReadDword(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "AutoAdminLogon"));
        Assert.Contains(log, l => l.Contains("not on the list"));
        // The file stays as it was for a later run without administrator rights.
        Assert.Equal(2, new GuardSnapshot(new FakeRegistry(), new FileVdfStore(), state).Entries.Count);
    }

    [Fact]
    public void A_snapshot_without_a_filter_still_restores_everything()
    {
        var state = Path.Combine(_dir, "guard.json");
        var reg = new FakeRegistry();
        reg.WriteDword(GameBarGuard.Key, GameBarGuard.Value, 1);
        new GuardSnapshot(reg, new FileVdfStore(), state).Apply([GameBarGuard.Change]);
        Assert.Empty(new GuardSnapshot(reg, new FileVdfStore(), state).Restore());
        Assert.Equal(1, reg.ReadDword(GameBarGuard.Key, GameBarGuard.Value));
        Assert.False(File.Exists(state));
    }
}

public class ElevationEnvironmentTests
{
    [Theory]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("CORECLR_PROFILER_PATH")]
    [InlineData("COR_PROFILER")]
    [InlineData("COMPlus_ReadyToRun")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("DOTNET_ROOT")]
    [InlineData("dotnet_root(x86)")]
    [InlineData("DOTNET_gcServer")]
    public void Variables_that_change_which_code_the_runtime_loads_are_found(string name)
    {
        Assert.Equal(name, ElevationEnvironment.Offending(["PATH", "TEMP", name]));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("CORE_COUNT")]
    [InlineData("DOTNET_CLI_TELEMETRY_OPTOUT")]
    [InlineData("DOTNET_NOLOGO")]
    public void Ordinary_variables_pass(string name)
    {
        Assert.Null(ElevationEnvironment.Offending([name, "TEMP"]));
    }
}

public class PerUserTaskTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string Other = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Theory]
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-1001", true)]
    [InlineData("S-1-12-1-1111-2222-3333-4444", true)]
    [InlineData("S-1-5-18", false)]
    [InlineData("S-1-1-0", false)]
    [InlineData("", false)]
    [InlineData("S-1-5-21-1-2-3-4)(A;;FA;;;WD", false)]
    [InlineData(@"..\evil", false)]
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-1001\\x", false)]
    public void Only_account_sids_get_tasks(string sid, bool valid)
    {
        Assert.Equal(valid, PolicyGroups.IsUserSid(sid));
        if (!valid) Assert.Throws<ArgumentException>(() => PolicyGroups.UserFolder(sid));
    }

    [Fact]
    public void Task_names_are_per_user()
    {
        Assert.Equal($@"CouchHud\{Sid}\Policy-lock-on", PolicyGroups.TaskName(PolicyGroups.Lock, true, Sid));
        Assert.Equal($@"CouchHud\{Sid}\Policy-taskmgr-off", PolicyGroups.TaskName(PolicyGroups.TaskManager, false, Sid));
        Assert.Equal($@"CouchHud\{Sid}\Autologon-off", PolicyGroups.AutoLogonOffTaskName(Sid));
        Assert.Equal($@"CouchHud\{Sid}\Policy-cleanup", PolicyGroups.CleanupTaskName(Sid));
        Assert.Equal($@"CouchHud\{Sid}\Couch-Foo", PolicyGroups.ServiceTaskName(Sid, couch: true, "Foo"));
        Assert.Equal($@"CouchHud\{Sid}\PC-Foo", PolicyGroups.ServiceTaskName(Sid, couch: false, "Foo"));
        Assert.NotEqual(PolicyGroups.TaskName(PolicyGroups.Lock, true, Sid), PolicyGroups.TaskName(PolicyGroups.Lock, true, Other));
    }

    [Fact]
    public void A_setup_recognizes_its_own_tasks_and_the_old_machine_wide_ones_only()
    {
        Assert.True(PolicyGroups.IsTaskOf($@"CouchHud\{Sid}\Policy-lock-on", Sid));
        Assert.True(PolicyGroups.IsTaskOf($@"\CouchHud\{Sid}\Couch-Foo", Sid));
        Assert.False(PolicyGroups.IsTaskOf($@"CouchHud\{Other}\Policy-lock-on", Sid));
        Assert.False(PolicyGroups.IsTaskOf(@"CouchHud\Policy-lock-on", Sid));
        // A SID that merely starts with ours (…1001 vs …10011) is another user.
        Assert.False(PolicyGroups.IsTaskOf($@"CouchHud\{Sid}9\Policy-lock-on", Sid));

        Assert.True(PolicyGroups.IsLegacyTask(@"CouchHud\Policy-lock-on"));
        Assert.True(PolicyGroups.IsLegacyTask(@"\CouchHud\Autologon-off"));
        Assert.True(PolicyGroups.IsLegacyTask(@"CouchHud\Dienste"));
        Assert.False(PolicyGroups.IsLegacyTask($@"CouchHud\{Sid}\Policy-lock-on"));
        Assert.False(PolicyGroups.IsLegacyTask(@"Microsoft\Windows\Defrag"));
    }

    [Fact]
    public void The_task_xml_runs_as_system_and_lets_exactly_that_user_start_it()
    {
        var xml = PolicyGroups.SystemTaskXml(Sid, @"C:\Windows\System32\reg.exe", PolicyGroups.ImportArguments(@"C:\Program Files\CouchHud\policy\" + Sid + @"\lock-on.reg"));
        var doc = XDocument.Parse(xml);
        Assert.Equal("S-1-5-18", doc.Descendants(Ns + "UserId").Single().Value);
        Assert.Equal("HighestAvailable", doc.Descendants(Ns + "RunLevel").Single().Value);
        // No interactive token: it runs in session 0, no console window on the couch.
        Assert.Empty(doc.Descendants(Ns + "LogonType"));
        Assert.Empty(doc.Descendants(Ns + "Triggers").Single().Elements());
        var sddl = doc.Descendants(Ns + "SecurityDescriptor").Single().Value;
        Assert.Equal($"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{Sid})", sddl);
        // Read and execute for the user, nothing that changes the task; nobody else (no Everyone, no Users, no Authenticated Users).
        Assert.DoesNotContain(";WD)", sddl);
        Assert.DoesNotContain(";BU)", sddl);
        Assert.DoesNotContain(";AU)", sddl);
        Assert.DoesNotContain(Other, sddl);
        var exec = doc.Descendants(Ns + "Exec").Single();
        Assert.Equal(@"C:\Windows\System32\reg.exe", exec.Element(Ns + "Command")!.Value);
        Assert.Equal("import \"C:\\Program Files\\CouchHud\\policy\\" + Sid + "\\lock-on.reg\"", exec.Element(Ns + "Arguments")!.Value);
        Assert.Equal("false", doc.Descendants(Ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("true", doc.Descendants(Ns + "AllowStartOnDemand").Single().Value);
    }

    [Fact]
    public void The_task_xml_refuses_a_value_that_is_no_sid()
    {
        Assert.Throws<ArgumentException>(() => PolicyGroups.SystemTaskXml("S-1-5-21-1-2-3-4)(A;;FA;;;WD", "a", "b"));
    }

    [Fact]
    public void The_service_task_xml_stops_one_service()
    {
        var doc = XDocument.Parse(PolicyGroups.SystemTaskXml(Sid, @"C:\Windows\System32\sc.exe", "stop Foo"));
        var exec = doc.Descendants(Ns + "Exec").Single();
        Assert.Equal("stop Foo", exec.Element(Ns + "Arguments")!.Value);
    }

    [Fact]
    public void The_reg_files_write_into_the_hive_of_that_user_and_nowhere_else()
    {
        var hive = PolicyGroups.UserHive(Sid);
        Assert.Equal($@"HKEY_USERS\{Sid}", hive);
        foreach (var group in PolicyGroups.All)
        {
            var apply = PolicyGroups.ApplyFile(group, hive);
            var release = PolicyGroups.ReleaseFile(group, hive);
            foreach (var text in new[] { apply, release })
            {
                Assert.DoesNotContain("HKEY_CURRENT_USER", text);
                Assert.DoesNotContain("HKEY_LOCAL_MACHINE", text);
                Assert.DoesNotContain(Other, text);
                var sections = text.Split("\r\n").Where(l => l.StartsWith('[')).ToList();
                Assert.NotEmpty(sections);
                Assert.All(sections, l => Assert.StartsWith($@"[{(l.StartsWith("[-") ? "-" : "")}HKEY_USERS\{Sid}\Software\", l));
            }
        }
        var lockApply = PolicyGroups.ApplyFile(PolicyGroups.Lock, hive);
        Assert.Contains($@"[HKEY_USERS\{Sid}\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer]", lockApply);
        Assert.Contains("\"NoRun\"=dword:00000001", lockApply);
        Assert.Contains($@"[-HKEY_USERS\{Sid}\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowRun]", PolicyGroups.ReleaseFile(PolicyGroups.Lock, hive));
        Assert.Contains("\"DisableTaskMgr\"=dword:00000001", PolicyGroups.ApplyFile(PolicyGroups.TaskManager, hive));
        Assert.Contains($@"[-HKEY_USERS\{Sid}\Software\Policies\Microsoft\Windows]", PolicyGroups.CleanupFile(["HKCU" + @"\Software\Policies\Microsoft\Windows"], hive));
    }

    [Fact]
    public void Without_a_root_the_files_stay_the_current_user_ones()
    {
        Assert.Contains("[HKEY_CURRENT_USER\\", PolicyGroups.ApplyFile(PolicyGroups.Lock));
        Assert.Throws<ArgumentException>(() => PolicyGroups.UserHive("S-1-5-18"));
    }
}
