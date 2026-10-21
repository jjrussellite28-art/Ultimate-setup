using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Privileged.Tests;

/// <summary>The Windows edges the service's operations added: boot time, hive unload, Policies key rules, registry links.</summary>
public class ServiceEdgeTests
{
    private sealed class Sink : IEventSink
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) => Lines.Add("I " + message);
        public void Error(string message) => Lines.Add("E " + message);
    }

    [Fact]
    public void The_boot_time_comes_from_the_kernel_and_does_not_move()
    {
        var first = SystemBoot.TimeMs();
        Assert.NotNull(first);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.InRange(first!.Value, now - (long)TimeSpan.FromDays(3650).TotalMilliseconds, now);
        Thread.Sleep(20);
        Assert.Equal(first, SystemBoot.TimeMs());
    }

    [Fact]
    public void An_unload_is_tried_again_and_every_failure_is_logged()
    {
        var sink = new Sink();
        var codes = new Queue<int>([32, 32, 0]);
        var pauses = new List<int>();
        Assert.True(WindowsUserHives.Unload(() => codes.Dequeue(), "CouchHud-x", sink, tries: 5, sleep: pauses.Add));
        Assert.Equal(2, sink.Lines.Count(l => l.Contains("not unloaded")));
        Assert.Equal(2, pauses.Count);

        var never = new Sink();
        var calls = 0;
        Assert.False(WindowsUserHives.Unload(() => { calls++; return 5; }, "CouchHud-y", never, tries: 3, sleep: _ => { }));
        Assert.Equal(3, calls);
        Assert.Equal(3, never.Lines.Count);
    }

    [Fact]
    public void A_policy_key_must_be_owned_by_administrators_and_writable_by_nobody_else()
    {
        AceFacts[] windows =
        [
            new(ProtectedFolderRules.SystemSid, true, 0xF003F, false),
            new(ProtectedFolderRules.AdministratorsSid, true, 0xF003F, false),
            new("S-1-5-21-1-2-3-1001", true, 0x20019, false),   // the user: read
            new("S-1-5-12", true, 0x20019, false),              // RESTRICTED: read
        ];
        Assert.Null(PolicyKeyRules.Problem(ProtectedFolderRules.AdministratorsSid, windows));
        Assert.Null(PolicyKeyRules.Problem(ProtectedFolderRules.SystemSid, windows));
        Assert.Equal("foreign owner", PolicyKeyRules.Problem("S-1-5-21-1-2-3-1001", windows));
        Assert.Equal("foreign owner", PolicyKeyRules.Problem(null, windows));
        foreach (var mask in new[] { 0x2, 0x4, 0x20, 0x10000, 0x40000, 0x80000, 0x10000000, 0x40000000, 0xF003F })
        {
            Assert.Equal("others may write", PolicyKeyRules.Problem(ProtectedFolderRules.AdministratorsSid, [.. windows, new("S-1-5-21-1-2-3-1001", true, mask, false)]));
        }
        Assert.Contains("(A;CI;KR;;;S-1-5-21-1-2-3-1001)", PolicyKeyRules.CreateSddl("S-1-5-21-1-2-3-1001"));
        Assert.StartsWith("O:BAG:SYD:P(", PolicyKeyRules.CreateSddl("S-1-5-21-1-2-3-1001"));
    }

    [Fact]
    public void Only_HKCU_keys_reach_a_users_hive()
    {
        var registry = new SafeHiveRegistry("S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1001");
        Assert.Throws<ArgumentException>(() => registry.ReadDword(@"HKLM\Software\Microsoft", "x"));
    }

    /// <summary>A volatile registry link in the test user's own HKCU (no administrator needed), removed again at the end.</summary>
    [Fact]
    public void A_registry_link_on_the_way_is_refused_and_nothing_is_written_through_it()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var root = @"Software\CouchHud.Tests-" + Guid.NewGuid().ToString("N");
        using (var target = Registry.CurrentUser.CreateSubKey(root + @"\target", RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.Volatile))
        {
            target.SetValue("v", 7, RegistryValueKind.DWord);
        }
        var linked = false;
        try
        {
            linked = CreateLink(root + @"\link", $@"\REGISTRY\USER\{sid}\{root}\target");
            if (!linked) return;   // the platform did not let us create a link: nothing to check here
            var registry = new SafeHiveRegistry(sid, sid);
            Assert.Equal(7, registry.ReadDword($@"HKCU\{root}\target", "v"));
            Assert.Throws<UnauthorizedAccessException>(() => registry.ReadDword($@"HKCU\{root}\link", "v"));
            Assert.Throws<UnauthorizedAccessException>(() => registry.WriteDword($@"HKCU\{root}\link\below", "w", 1));
            using var target = Registry.CurrentUser.OpenSubKey(root + @"\target")!;
            Assert.Empty(target.GetSubKeyNames());
        }
        finally
        {
            if (linked) DeleteLink(root + @"\link");
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    private static bool CreateLink(string path, string target)
    {
        const int CreateLinkOption = 0x2 | 0x1;   // REG_OPTION_CREATE_LINK | REG_OPTION_VOLATILE
        const int Access = 0x20019 | 0x20006 | 0x20;   // KEY_READ | KEY_WRITE | KEY_CREATE_LINK
        if (RegCreateKeyEx(HkeyCurrentUser, path, 0, null, CreateLinkOption, Access, IntPtr.Zero, out var key, out _) != 0) return false;
        using (key)
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(target);
            return RegSetValueEx(key, "SymbolicLinkValue", 0, 6, bytes, bytes.Length) == 0;
        }
    }

    private static void DeleteLink(string path)
    {
        if (RegOpenKeyEx(HkeyCurrentUser, path, 0x8, 0x10000, out var key) != 0) return;
        using (key) NtDeleteKey(key);
    }

    private static readonly IntPtr HkeyCurrentUser = unchecked((IntPtr)(int)0x80000001);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegCreateKeyExW")]
    private static extern int RegCreateKeyEx(IntPtr key, string subKey, int reserved, string? className, int options, int access, IntPtr security, out SafeRegistryHandle result, out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW")]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string name, int reserved, int type, byte[] data, int size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    private static extern int RegOpenKeyEx(IntPtr key, string subKey, int options, int access, out SafeRegistryHandle result);

    [DllImport("ntdll.dll")]
    private static extern int NtDeleteKey(SafeRegistryHandle key);
}
