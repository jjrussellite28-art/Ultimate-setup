using CouchHud.Core.Security;
using CouchHud.Privileged;

namespace CouchHud.Privileged.Tests;

/// <summary>Every file of the service folder counts, and generic rights count as the write rights they stand for.</summary>
public sealed class InstallProtectionTests : IDisposable
{
    private const string Users = "S-1-5-32-545";
    private const string Admins = "S-1-5-32-544";
    private const string SystemSid = "S-1-5-18";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "couchhud-ip-" + Guid.NewGuid().ToString("N"));
    private readonly string _exe;
    private readonly string _dll;
    private readonly string _nestedDll;

    public InstallProtectionTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "service", "runtimes"));
        _exe = Path.Combine(_root, "service", "CouchHud.Service.exe");
        _dll = Path.Combine(_root, "service", "CouchHud.Service.Contracts.dll");
        _nestedDll = Path.Combine(_root, "service", "runtimes", "native.dll");
        foreach (var file in new[] { _exe, _dll, _nestedDll }) File.WriteAllBytes(file, [0]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly (string?, IReadOnlyList<AclEntry>) Locked =
        (SystemSid, [new AclEntry(SystemSid, FolderProtection.FileAllAccess), new AclEntry(Admins, FolderProtection.FileAllAccess), new AclEntry(Users, FolderProtection.FileGenericRead | FolderProtection.FileGenericExecute)]);

    private static Func<FileSystemInfo, (string?, IReadOnlyList<AclEntry>)> AllLockedExcept(string path, (string?, IReadOnlyList<AclEntry>) acl) =>
        item => string.Equals(item.FullName.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase) ? acl : Locked;

    [Fact]
    public void A_locked_folder_with_all_files_locked_is_protected() =>
        Assert.True(InstallProtection.IsProtected(_exe, new CapturingEvents(), allFiles: true, _ => Locked));

    [Fact]
    public void A_user_writable_dll_next_to_the_exe_makes_it_unprotected()
    {
        var writable = (SystemSid, (IReadOnlyList<AclEntry>)[new AclEntry(Users, FolderProtection.WriteData)]);
        Assert.False(InstallProtection.IsProtected(_exe, new CapturingEvents(), allFiles: true, AllLockedExcept(_dll, writable)));
        Assert.False(InstallProtection.IsProtected(_exe, new CapturingEvents(), allFiles: true, AllLockedExcept(_nestedDll, writable)));
        // Without allFiles only the exe and the folders are checked (the shell's check, unchanged).
        Assert.True(InstallProtection.IsProtected(_exe, new CapturingEvents(), allFiles: false, AllLockedExcept(_dll, writable)));
    }

    [Fact]
    public void A_dll_owned_by_a_user_makes_it_unprotected()
    {
        var owned = ("S-1-5-21-1-2-3-1001", Locked.Item2);
        Assert.False(InstallProtection.IsProtected(_exe, new CapturingEvents(), allFiles: true, AllLockedExcept(_dll, owned)));
    }

    [Theory]
    [InlineData(FolderProtection.GenericWrite)]
    [InlineData(FolderProtection.GenericAll)]
    public void Generic_write_rights_count_as_write(int rights)
    {
        var generic = (SystemSid, (IReadOnlyList<AclEntry>)[new AclEntry(Users, rights)]);
        Assert.False(InstallProtection.IsProtected(_exe, new CapturingEvents(), allFiles: true, AllLockedExcept(_dll, generic)));
        Assert.False(InstallProtection.IsProtected(_exe, new CapturingEvents(), allFiles: false, AllLockedExcept(_exe, generic)));
        Assert.False(FolderProtection.IsProtected(SystemSid, [new AclEntry(Users, rights)]));
    }

    [Fact]
    public void Generic_read_and_execute_are_not_write()
    {
        Assert.True(FolderProtection.IsProtected(SystemSid, [new AclEntry(Users, FolderProtection.GenericRead | FolderProtection.GenericExecute)]));
        Assert.Equal(FolderProtection.FileAllAccess, FolderProtection.MapGeneric(FolderProtection.GenericAll));
        Assert.Equal(FolderProtection.FileGenericWrite | FolderProtection.Delete, FolderProtection.MapGeneric(FolderProtection.GenericWrite | FolderProtection.Delete));
    }

    [Fact]
    public void Unreadable_acls_are_unprotected()
    {
        var events = new CapturingEvents();
        Assert.False(InstallProtection.IsProtected(_exe, events, allFiles: true, item =>
            item.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? throw new UnauthorizedAccessException(@"C:\Users\player\secret") : Locked));
        Assert.DoesNotContain("player", string.Join("\n", events.Errors));
    }

    private sealed class CapturingEvents : IEventSink
    {
        public List<string> Errors { get; } = [];

        public void Info(string message)
        {
        }

        public void Error(string message) => Errors.Add(message);
    }
}
