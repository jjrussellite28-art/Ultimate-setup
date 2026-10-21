using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using CouchHud.Core.Security;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Privileged;

/// <summary>
/// One-time cleanup on uninstall of a stale <c>ProgramData\CouchHud\Service</c> (where development builds kept the service's state;
/// it now lives in <c>{app}\service\data</c>, see <see cref="ServiceInstall.DataFolder"/>). Runs as an administrator in a place where
/// users may create folders (ProgramData), so it fails closed and follows nothing:
/// <list type="bullet">
/// <item><c>ProgramData\CouchHud</c> is opened as itself (never through a link), must be owned by SYSTEM/Administrators and must not let
/// anyone else rename or replace what is in it (<see cref="FolderProtection.ReplaceMask"/>).</item>
/// <item><c>Service</c> must be really protected: a protected DACL and nobody but SYSTEM/Administrators/TrustedInstaller may write
/// (<see cref="FolderProtection.IsProtected"/>, owner included). Otherwise it stays.</item>
/// <item>Everything below is opened relative to the handle of its folder (NtCreateFile with a root directory, FILE_OPEN_REPARSE_POINT),
/// the reparse attribute is read from that handle, a link is deleted as the link and never entered. A subfolder whose owner is not
/// SYSTEM/Administrators is left alone (and so is everything above it).</item>
/// <item>Files go with POSIX delete semantics ignoring the read-only attribute: no attribute is changed on a file, so a hard link
/// to a file elsewhere cannot be used to change that file. Only a file with a single link has its read-only attribute cleared when
/// the system does not support that flag.</item>
/// </list>
/// </summary>
public static class ServiceDataCleanup
{
    /// <summary>What the cleanup trusts; replaced by tests, where a temp folder is owned by whoever runs them.</summary>
    internal sealed record Rules(
        Func<RawSecurityDescriptor, bool> ParentSafe,
        Func<RawSecurityDescriptor, bool> FolderSafe,
        Func<SecurityIdentifier?, bool> TrustedOwner,
        Action<string, string>? BeforeOpen = null)
    {
        public static readonly Rules Real = new(
            sd => IsProtected(sd, FolderProtection.ReplaceMask, requireProtectedDacl: false),
            sd => IsProtected(sd, FolderProtection.WriteMask, requireProtectedDacl: true),
            IsTrustedOwner);
    }

    /// <summary>True when the state folder is gone (or was never there).</summary>
    public static bool Delete(string commonApplicationData, IEventSink log) => Delete(commonApplicationData, log, Rules.Real);

    internal static bool Delete(string commonApplicationData, IEventSink log, Rules rules)
    {
        var parentPath = Path.Combine(commonApplicationData, "CouchHud");
        try
        {
            using var parent = Native.OpenPath(parentPath);
            if (parent is null) return true;
            if (Native.IsReparsePoint(parent))
            {
                log.Error($"[uninstall] {parentPath} is a link (junction or symbolic link) and is left alone");
                return false;
            }
            if (!rules.ParentSafe(Native.Descriptor(parent)))
            {
                log.Error($"[uninstall] {parentPath} is not protected (owner or access list) and is left alone");
                return false;
            }
            var done = true;
            using (var folder = Native.OpenChild(parent, "Service"))
            {
                if (folder is not null)
                {
                    if (Native.IsReparsePoint(folder) || !Native.IsDirectory(folder))
                    {
                        done = Native.DeleteEntry(folder, log, "Service");
                        log.Error($"[uninstall] {parentPath}\\Service was not a folder; removed it as it is");
                    }
                    else if (!rules.FolderSafe(Native.Descriptor(folder)))
                    {
                        log.Error($"[uninstall] {parentPath}\\Service is not protected (owner or access list) and is left alone");
                        return false;
                    }
                    else
                    {
                        done = DeleteChildren(folder, $"{parentPath}\\Service", rules, log) && Native.DeleteEntry(folder, log, "Service");
                        if (done) log.Info($"[uninstall] removed {parentPath}\\Service");
                    }
                }
            }
            if (done && Native.List(parent).Count == 0) Native.DeleteEntry(parent, log, parentPath);
            return done;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.Error($"[uninstall] service state: {ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    private static bool DeleteChildren(SafeFileHandle dir, string where, Rules rules, IEventSink log)
    {
        var done = true;
        foreach (var name in Native.List(dir))
        {
            rules.BeforeOpen?.Invoke(where, name);
            var path = $"{where}\\{name}";
            try
            {
                done &= DeleteOne(dir, name, path, rules, log);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
            {
                // In use or not accessible: this one stays, the others are still tried.
                log.Error($"[uninstall] {path}: {ex.Message}");
                done = false;
            }
        }
        return done;
    }

    private static bool DeleteOne(SafeFileHandle dir, string name, string path, Rules rules, IEventSink log)
    {
        var done = true;
        {
            using var entry = Native.OpenChild(dir, name);
            if (entry is null) return true;   // gone meanwhile
            // A link (junction, symbolic link, mount point) is deleted as the link and never entered.
            if (Native.IsReparsePoint(entry)) return Native.DeleteEntry(entry, log, path);
            if (!Native.IsDirectory(entry)) return Native.DeleteEntry(entry, log, path);
            if (!rules.TrustedOwner(Native.Descriptor(entry).Owner))
            {
                log.Error($"[uninstall] {path} is not owned by SYSTEM or the administrators and is left alone");
                return false;
            }
            done = DeleteChildren(entry, path, rules, log) && Native.DeleteEntry(entry, log, path);
        }
        return done;
    }

    private static readonly SecurityIdentifier[] TrustedOwners =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null)
    ];

    internal static bool IsTrustedOwner(SecurityIdentifier? owner) => owner is not null && TrustedOwners.Contains(owner);

    /// <summary>
    /// The folder's security descriptor judged by <see cref="FolderProtection.IsProtected"/> (owner and allow entries, generic rights
    /// mapped); with <paramref name="requireProtectedDacl"/> the DACL must also not inherit from above.
    /// </summary>
    internal static bool IsProtected(RawSecurityDescriptor sd, int mask, bool requireProtectedDacl)
    {
        if (sd.DiscretionaryAcl is null) return false;   // a NULL DACL allows everyone everything
        if (requireProtectedDacl && !sd.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)) return false;
        var entries = sd.DiscretionaryAcl.OfType<CommonAce>()
            .Where(a => a.AceQualifier == AceQualifier.AccessAllowed)
            .Select(a => new AclEntry(a.SecurityIdentifier.Value, a.AccessMask, !a.AceFlags.HasFlag(AceFlags.InheritOnly)));
        // Allow entries of other kinds (object or callback ACEs) are not judged: not protected.
        if (sd.DiscretionaryAcl.OfType<GenericAce>().Any(a => a is not CommonAce && a.AceType != AceType.AccessDenied)) return false;
        return FolderProtection.IsProtected(sd.Owner?.Value, entries, mask);
    }

    /// <summary>NT file handles: open as itself, relative to a folder handle, read attributes and security, delete on the handle.</summary>
    private static class Native
    {
        private const uint Delete = 0x10000, ReadControl = 0x20000, Synchronize = 0x100000;
        private const uint ListDirectory = 0x1, ReadAttributes = 0x80, WriteAttributes = 0x100;
        private const uint Access = Delete | ReadControl | Synchronize | ListDirectory | ReadAttributes | WriteAttributes;
        private const uint ShareAll = 0x7;
        private const uint FileAttributeReadOnly = 0x1, FileAttributeDirectory = 0x10, FileAttributeReparsePoint = 0x400, FileAttributeNormal = 0x80;

        public static SafeFileHandle? OpenPath(string path)
        {
            // OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS (folders) | FILE_FLAG_OPEN_REPARSE_POINT (the link itself, never its target).
            var handle = CreateFileW(path, Access, ShareAll, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is 2 or 3) return null;   // not found
            throw new Win32Exception(error);
        }

        public static SafeFileHandle? OpenChild(SafeFileHandle dir, string name)
        {
            if (name.Length == 0 || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0 || name is "." or "..") throw new ArgumentException("bad name", nameof(name));
            var buffer = Marshal.StringToHGlobalUni(name);
            var unicode = new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = buffer };
            var unicodePtr = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            try
            {
                Marshal.StructureToPtr(unicode, unicodePtr, false);
                var added = false;
                dir.DangerousAddRef(ref added);
                try
                {
                    var attributes = new ObjectAttributes
                    {
                        Length = Marshal.SizeOf<ObjectAttributes>(),
                        RootDirectory = dir.DangerousGetHandle(),
                        ObjectName = unicodePtr,
                        Attributes = 0x40 // OBJ_CASE_INSENSITIVE
                    };
                    // FILE_OPEN; FILE_OPEN_REPARSE_POINT | FILE_SYNCHRONOUS_IO_NONALERT.
                    var status = NtCreateFile(out var handle, Access, ref attributes, out _, IntPtr.Zero, 0, ShareAll, 1, 0x00200000 | 0x20, IntPtr.Zero, 0);
                    if (status >= 0) return handle;
                    handle.Dispose();
                    // STATUS_OBJECT_NAME_NOT_FOUND, STATUS_OBJECT_PATH_NOT_FOUND, STATUS_DELETE_PENDING.
                    if ((uint)status is 0xC0000034 or 0xC000003A or 0xC0000056) return null;
                    throw new Win32Exception(RtlNtStatusToDosError(status));
                }
                finally
                {
                    if (added) dir.DangerousRelease();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(unicodePtr);
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static uint AttributesOf(SafeFileHandle handle)
        {
            // FileAttributeTagInfo = 9: { DWORD FileAttributes; DWORD ReparseTag; }
            var info = new uint[2];
            if (!GetFileInformationByHandleEx(handle, 9, info, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return info[0];
        }

        public static bool IsReparsePoint(SafeFileHandle handle) => (AttributesOf(handle) & FileAttributeReparsePoint) != 0;

        public static bool IsDirectory(SafeFileHandle handle) => (AttributesOf(handle) & FileAttributeDirectory) != 0;

        private static uint LinkCount(SafeFileHandle handle)
        {
            // FileStandardInfo = 1: { LARGE_INTEGER AllocationSize; LARGE_INTEGER EndOfFile; DWORD NumberOfLinks; BOOLEAN DeletePending; BOOLEAN Directory; }
            var info = new uint[6];
            if (!GetFileInformationByHandleEx(handle, 1, info, 24)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return info[4];
        }

        public static RawSecurityDescriptor Descriptor(SafeFileHandle handle)
        {
            // SE_FILE_OBJECT; OWNER | DACL.
            var error = GetSecurityInfo(handle, 1, 0x1 | 0x4, out _, out _, out _, out _, out var sd);
            if (error != 0) throw new Win32Exception((int)error);
            try
            {
                var bytes = new byte[GetSecurityDescriptorLength(sd)];
                Marshal.Copy(sd, bytes, 0, bytes.Length);
                return new RawSecurityDescriptor(bytes, 0);
            }
            finally
            {
                LocalFree(sd);
            }
        }

        /// <summary>The names in the folder (without . and ..), read from its handle.</summary>
        public static List<string> List(SafeFileHandle dir)
        {
            var names = new List<string>();
            var buffer = new byte[64 * 1024];
            // FileFullDirectoryRestartInfo = 15 first, then FileFullDirectoryInfo = 14.
            for (var infoClass = 15; ; infoClass = 14)
            {
                if (!GetFileInformationByHandleEx(dir, infoClass, buffer, (uint)buffer.Length))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 18) return names;   // ERROR_NO_MORE_FILES
                    throw new Win32Exception(error);
                }
                // FILE_FULL_DIR_INFO: NextEntryOffset @0, FileNameLength @60, FileName @68.
                for (var offset = 0; ;)
                {
                    var next = BitConverter.ToInt32(buffer, offset);
                    var length = BitConverter.ToInt32(buffer, offset + 60);
                    var name = System.Text.Encoding.Unicode.GetString(buffer, offset + 68, length);
                    if (name is not ("." or "..")) names.Add(name);
                    if (next == 0) break;
                    offset += next;
                }
            }
        }

        /// <summary>Deletes what the handle stands for (a link as the link). No attribute is changed on a file with more than one link.</summary>
        public static bool DeleteEntry(SafeFileHandle handle, IEventSink log, string what)
        {
            // FileDispositionInfoEx = 21: DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE.
            var flags = new uint[] { 0x1 | 0x2 | 0x10 };
            if (SetFileInformationByHandle(handle, 21, flags, 4)) return true;
            var error = Marshal.GetLastWin32Error();
            if (error is 87 or 50 or 1)   // the flags are not supported here: classic delete
            {
                var attributes = AttributesOf(handle);
                if ((attributes & FileAttributeReadOnly) != 0)
                {
                    if ((attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) == 0 && LinkCount(handle) > 1)
                    {
                        log.Error($"[uninstall] {what} is read-only and has other links; left alone");
                        return false;
                    }
                    // FileBasicInfo = 0: four times (0 = unchanged), then the attributes; 40 bytes.
                    var basic = new uint[10];
                    var cleared = attributes & ~FileAttributeReadOnly;
                    basic[8] = cleared == 0 ? FileAttributeNormal : cleared;
                    if (!SetFileInformationByHandle(handle, 0, basic, 40)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                // FileDispositionInfo = 4: BOOLEAN DeleteFile.
                if (SetFileInformationByHandle(handle, 4, [1u], 1)) return true;
                error = Marshal.GetLastWin32Error();
            }
            log.Error($"[uninstall] {what} not deleted (error {error})");
            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            public IntPtr Status;
            public IntPtr Information;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock status,
            IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);

        [DllImport("ntdll.dll")]
        private static extern int RtlNtStatusToDosError(int status);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, [Out] uint[] info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, [Out] byte[] info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, uint[] info, uint size);

        [DllImport("advapi32.dll")]
        private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr sd);

        [DllImport("advapi32.dll")]
        private static extern uint GetSecurityDescriptorLength(IntPtr sd);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
