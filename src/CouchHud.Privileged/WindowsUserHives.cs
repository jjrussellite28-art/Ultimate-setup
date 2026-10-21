using System.ComponentModel;
using System.Runtime.InteropServices;
using CouchHud.Core.Guard;
using CouchHud.Core.Platform;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Privileged;

/// <summary>
/// The hives below HKEY_USERS. A signed-in account's hive is used as Windows loaded it; an account that is not signed in is loaded
/// from its NTUSER.DAT (RegLoadKey, needs SeBackup and SeRestore, which the service has) under a random mount name and unloaded again.
/// </summary>
/// <remarks>
/// The profile folder belongs to its user, so the file is checked before it is loaded: the path comes from the machine's ProfileList
/// (only administrators write it), must be a local absolute path, and neither the folder nor the file may be a reparse point, and the
/// file must have exactly one name (a hard link could point at another account's hive). A user who swaps the file between the check
/// and the load gains nothing: loading is only used by ReleaseAllAccounts (an administrator, at uninstall), which removes nothing but
/// the fixed values the service set for that SID.
/// </remarks>
public sealed class WindowsUserHives(IEventSink events) : IUserHives
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public IUserHive? OpenLoaded(string sid)
    {
        if (!PolicyGroups.IsUserSid(sid)) return null;
        try
        {
            using var hive = Registry.Users.OpenSubKey(sid);
            return hive is null ? null : new Hive(new SafeHiveRegistry(sid, sid), null, events);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            events.Error($"[hive] {sid}: {ex.GetType().Name}");
            return null;
        }
    }

    public IUserHive? OpenOrLoad(string sid)
    {
        if (OpenLoaded(sid) is { } loaded) return loaded;
        if (!PolicyGroups.IsUserSid(sid)) return null;
        var file = HiveFile(sid);
        if (file is null) return null;
        var mount = "CouchHud-" + Guid.NewGuid().ToString("N");
        try
        {
            // The privileges only for the call itself: enabled, used, disabled again.
            var code = Privileges.WithBackupRestore(() => RegLoadKey(HkeyUsers, mount, file));
            if (code != 0)
            {
                events.Error($"[hive] {sid} not loaded: Windows error {code}");
                return null;
            }
            return new Hive(new SafeHiveRegistry(mount, sid), mount, events);
        }
        catch (Win32Exception ex)
        {
            events.Error($"[hive] {sid} not loaded: {ex.NativeErrorCode}");
            return null;
        }
    }

    /// <summary>
    /// Unloads a hive the service loaded: Windows refuses while a key of it is still open somewhere (also briefly after our own keys
    /// closed), so a few tries with a pause. Every failure is logged; true when it is unloaded.
    /// </summary>
    internal static bool Unload(Func<int> unload, string mount, IEventSink events, int tries = 5, Action<int>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        for (var attempt = 1; attempt <= tries; attempt++)
        {
            var code = unload();
            if (code == 0) return true;
            events.Error($"[hive] {mount} not unloaded (try {attempt} of {tries}): Windows error {code}");
            if (attempt < tries) sleep(200 * attempt);
        }
        return false;
    }

    /// <summary>NTUSER.DAT of the profile, or null when it is not there or cannot be trusted (see the remarks of the class).</summary>
    private string? HiveFile(string sid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ProfileList}\{sid}");
            if (key?.GetValue("ProfileImagePath") is not string raw) return null;
            var profile = Environment.ExpandEnvironmentVariables(raw);
            if (!Path.IsPathFullyQualified(profile) || profile.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var file = Path.Combine(profile, "NTUSER.DAT");
            var folder = new DirectoryInfo(profile);
            if (!folder.Exists || folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return Refuse(sid, "profile folder");
            if (!File.Exists(file) || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) return Refuse(sid, "hive file");
            if (LinkCount(file) != 1) return Refuse(sid, "hive file links");
            return file;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            events.Error($"[hive] {sid}: {ex.GetType().Name}");
            return null;
        }
    }

    private string? Refuse(string sid, string what)
    {
        events.Error($"[hive] {sid} not loaded: the {what} cannot be trusted");
        return null;
    }

    /// <summary>The number of names of the file (opened without following a reparse point); 0 when it cannot be read.</summary>
    private static uint LinkCount(string path)
    {
        using var handle = CreateFile(path, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FlagOpenReparsePoint | FlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return 0;
        return GetFileInformationByHandle(handle, out var info) ? info.NumberOfLinks : 0;
    }

    private sealed class Hive(IRegistry registry, string? mount, IEventSink events) : IUserHive
    {
        private int _disposed;

        public IRegistry Registry { get; } = registry;

        public void Dispose()
        {
            if (mount is null || Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Open keys of the mount would keep it loaded; SafeHiveRegistry closes every key it opens before it returns.
            try
            {
                Unload(() => Privileges.WithBackupRestore(() => RegUnLoadKey(HkeyUsers, mount)), mount, events);
            }
            catch (Win32Exception ex)
            {
                events.Error($"[hive] {mount} not unloaded: {ex.NativeErrorCode}");
            }
        }
    }

    private static readonly IntPtr HkeyUsers = unchecked((IntPtr)(int)0x80000003);
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint FlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegLoadKeyW")]
    private static extern int RegLoadKey(IntPtr key, string subKey, string file);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegUnLoadKeyW")]
    private static extern int RegUnLoadKey(IntPtr key, string subKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}

/// <summary>Enables a privilege the process token holds (the service's are fixed by <c>sc privs</c>).</summary>
internal static class Privileges
{
    private const uint AdjustPrivileges = 0x20;
    private const uint Query = 0x8;
    private const uint Enabled = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    private static readonly Lock Gate = new();

    /// <summary>
    /// Runs <paramref name="action"/> with SeBackup and SeRestore enabled and disables them again right after (also on failure), so
    /// the service holds them enabled only for the hive load or unload. Serialized: the token is the process's.
    /// </summary>
    public static T WithBackupRestore<T>(Func<T> action)
    {
        lock (Gate)
        {
            try
            {
                Set("SeBackupPrivilege", true);
                Set("SeRestorePrivilege", true);
                return action();
            }
            finally
            {
                TryDisable("SeRestorePrivilege");
                TryDisable("SeBackupPrivilege");
            }
        }
    }

    private static void TryDisable(string name)
    {
        try
        {
            Set(name, false);
        }
        catch (Win32Exception)
        {
            // Not held at all: nothing to disable.
        }
    }

    public static void Set(string name, bool enable)
    {
        if (!OpenProcessToken(GetCurrentProcess(), AdjustPrivileges | Query, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = enable ? Enabled : 0 };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            // ERROR_NOT_ALL_ASSIGNED: the token does not hold it.
            var last = Marshal.GetLastWin32Error();
            if (last != 0) throw new Win32Exception(last);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returnLength);
}
