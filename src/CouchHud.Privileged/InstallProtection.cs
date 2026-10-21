using System.Security.AccessControl;
using System.Security.Principal;
using CouchHud.Core.Security;

namespace CouchHud.Privileged;

/// <summary>
/// Whether CouchHud.exe sits where only administrators can change it. Read from the ACLs (who may write), not tried
/// out: the elevated helper runs as an administrator, and for an administrator every probe succeeds.
/// The folder, the exe and every folder above (which could be renamed away and replaced) are checked; with
/// <c>allFiles</c> also every file and folder below the exe's folder (the DLLs a self-contained service loads).
/// Generic rights in an entry (GENERIC_WRITE, GENERIC_ALL) count as the write rights they stand for.
/// </summary>
public static class InstallProtection
{
    /// <summary>More files than this in the folder: not checked one by one, so not called protected.</summary>
    public const int MaxFiles = 20_000;

    public static bool IsProtected() => IsProtected(Environment.ProcessPath);

    public static bool IsProtected(string? exe, IEventSink? events = null, bool allFiles = false) =>
        IsProtected(exe, events, allFiles, ReadAcl);

    /// <summary>The check with the way an ACL is read passed in (tests hand in fake ACLs).</summary>
    internal static bool IsProtected(string? exe, IEventSink? events, bool allFiles, Func<FileSystemInfo, (string? Owner, IReadOnlyList<AclEntry> Entries)> aclOf)
    {
        if (exe is null) return false;
        try
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(exe)!);
            if (!dir.Exists || !File.Exists(exe)) return false;
            if (!Protected(aclOf(dir), FolderProtection.WriteMask)) return false;
            if (!Protected(aclOf(new FileInfo(exe)), FolderProtection.WriteMask)) return false;
            for (var parent = dir.Parent; parent is not null; parent = parent.Parent)
            {
                if (!Protected(aclOf(parent), FolderProtection.ReplaceMask)) return false;
            }
            if (allFiles)
            {
                var seen = 0;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
                foreach (var item in dir.EnumerateFileSystemInfos("*", options))
                {
                    if (++seen > MaxFiles) return false;
                    // A reparse point inside the install folder could point anywhere.
                    if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                    if (!Protected(aclOf(item), FolderProtection.WriteMask)) return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PlatformNotSupportedException or IdentityNotMappedException)
        {
            // Not readable: better "not protected" than a guess.
            (events ?? ConsoleEventSink.Instance).Error($"[setup] install folder ACL not readable: {ex.GetType().Name}");
            return false;
        }
    }

    private static bool Protected((string? Owner, IReadOnlyList<AclEntry> Entries) acl, int mask) => FolderProtection.IsProtected(acl.Owner, acl.Entries, mask);

    /// <summary>
    /// One folder on its own: owned by SYSTEM, the Administrators or TrustedInstaller, and nobody else may write, delete or change
    /// the ACL (<see cref="FolderProtection.WriteMask"/>). False when the ACL cannot be read.
    /// </summary>
    public static bool IsFolderProtected(string directory)
    {
        try
        {
            var dir = new DirectoryInfo(directory);
            return dir.Exists && Protected(ReadAcl(dir), FolderProtection.WriteMask);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PlatformNotSupportedException or IdentityNotMappedException)
        {
            return false;
        }
    }

    private static (string? Owner, IReadOnlyList<AclEntry> Entries) ReadAcl(FileSystemInfo item)
    {
        FileSystemSecurity security = item is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)item).GetAccessControl();
        var owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        var entries = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => new AclEntry(r.IdentityReference.Value, (int)r.FileSystemRights, !r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)))
            .ToList();
        return (owner, entries);
    }
}
