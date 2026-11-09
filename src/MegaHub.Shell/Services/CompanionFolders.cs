using System.Security.AccessControl;
using System.Security.Principal;
using MegaHub.Core.Library;
using MegaHub.Core.Security;

namespace MegaHub.Shell.Services;

/// <summary>
/// Reads the ACLs the folder check of a companion program needs (the file, the folder it starts in, and every folder above them). The decision
/// is <see cref="CompanionPin.Writable"/>; any ACL that cannot be read counts as writable.
/// </summary>
public static class CompanionFolders
{
    /// <summary>Can others change the held program, its folder, its working folder or a folder above them.</summary>
    public static bool Writable(StartPlan plan, HeldProgram held) =>
        CompanionPin.Writable(Read, held.Path, CompanionPin.EffectiveFolder(plan, held.Path));

    private static (string? Owner, IReadOnlyList<AclEntry> Entries)? Read(string path)
    {
        try
        {
            FileSystemSecurity security = File.Exists(path) ? new FileInfo(path).GetAccessControl() : new DirectoryInfo(path).GetAccessControl();
            var owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
            var entries = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Where(r => r.AccessControlType == AccessControlType.Allow)
                .Select(r => new AclEntry(r.IdentityReference.Value, (int)r.FileSystemRights,
                    !r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly), r.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit)))
                .ToList();
            return (owner, entries);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
