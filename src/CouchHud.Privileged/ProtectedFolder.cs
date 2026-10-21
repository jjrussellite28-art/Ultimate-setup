using System.Security.AccessControl;
using System.Security.Principal;

namespace CouchHud.Privileged;

/// <summary>One entry of a folder's access list, reduced to what the decision needs.</summary>
public sealed record AceFacts(string Sid, bool Allow, int Mask, bool InheritOnly);

/// <summary>What a folder looks like: whether it exists, is a reparse point, who owns it and who may do what.</summary>
public sealed record FolderFacts(bool Exists, bool IsReparsePoint, string? OwnerSid, bool DaclProtected, IReadOnlyList<AceFacts> Aces)
{
    public static readonly FolderFacts Missing = new(false, false, null, false, []);
}

/// <summary>
/// When the service trusts a folder of its data chain (<c>{app}\service</c> and <c>{app}\service\data</c>): no reparse point, owned by
/// SYSTEM, Administrators or TrustedInstaller, and nobody else may write, delete or change its access list. Pure: the facts come from
/// <see cref="WindowsProtectedFolder.Read"/>.
/// </summary>
public static class ProtectedFolderRules
{
    public const string SystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>The data folder's access list when the service has to create it: owner SYSTEM, protected, SYSTEM and Administrators only.</summary>
    public const string DataFolderSddl = "O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

    private static readonly IReadOnlySet<string> Trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SystemSid, AdministratorsSid, TrustedInstallerSid };

    // Every right that changes the folder or what is in it: write data / add file, append data / add subfolder, write extended
    // attributes, delete child, write attributes, DELETE, WRITE_DAC, WRITE_OWNER, GENERIC_WRITE, GENERIC_ALL.
    private const int WriteRights = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000;

    /// <summary>Why the folder cannot be trusted, or null.</summary>
    public static string? Problem(FolderFacts facts)
    {
        if (!facts.Exists) return "missing";
        if (facts.IsReparsePoint) return "reparse point";
        if (facts.OwnerSid is null || !Trusted.Contains(facts.OwnerSid)) return "foreign owner";
        foreach (var ace in facts.Aces)
        {
            // Inherit-only entries (CREATOR OWNER of Program Files) do not apply to the folder itself; deny entries take nothing from us.
            if (!ace.Allow || ace.InheritOnly || Trusted.Contains(ace.Sid)) continue;
            if ((ace.Mask & WriteRights) != 0) return "others may write";
        }
        return null;
    }
}

/// <summary>
/// The service's data folder: <paramref name="name"/> below the service's own folder <paramref name="serviceDirectory"/> (<c>{app}\service</c>
/// in Program Files, which the installer protects). Both folders of the chain are checked; the data folder is created with
/// <see cref="ProtectedFolderRules.DataFolderSddl"/> in one step when it is missing (the installer normally creates it). Anything else:
/// refused (null) with an alarm in the event log that names the folder; nothing is moved or repaired.
/// </summary>
public sealed class WindowsProtectedFolder(string serviceDirectory, string name, IEventSink events) : IProtectedFolder
{
    public string Path { get; } = System.IO.Path.Combine(serviceDirectory, name);

    public string? Ensure()
    {
        var current = serviceDirectory;
        try
        {
            if (ProtectedFolderRules.Problem(Read(serviceDirectory)) is { } parentProblem) return Alarm(serviceDirectory, parentProblem);
            current = Path;
            var facts = Read(Path);
            if (!facts.Exists)
            {
                var security = new DirectorySecurity();
                security.SetSecurityDescriptorSddlForm(ProtectedFolderRules.DataFolderSddl);
                // CreateDirectory with the security descriptor: there is no moment in which the folder has another access list.
                security.CreateDirectory(Path);
                facts = Read(Path);
            }
            if (ProtectedFolderRules.Problem(facts) is { } problem) return Alarm(Path, problem);
            return Path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or InvalidOperationException or PrivilegeNotHeldException)
        {
            return Alarm(current, ex.GetType().Name);
        }
    }

    private string? Alarm(string path, string problem)
    {
        events.Error($"[alarm] service data folder refused, the service keeps no state: {path} ({problem})");
        return null;
    }

    /// <summary>The facts of a folder as Windows reports them (the reparse point of the folder itself, not of its target).</summary>
    public static FolderFacts Read(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            // A dangling junction or a file of that name is no folder to use either.
            return File.Exists(path) || (info.Attributes != (FileAttributes)(-1) && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                ? new FolderFacts(true, true, null, false, [])
                : FolderFacts.Missing;
        }
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return new FolderFacts(true, true, null, false, []);
        var security = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        var aces = new List<AceFacts>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            aces.Add(new AceFacts(rule.IdentityReference.Value, rule.AccessControlType == AccessControlType.Allow, (int)rule.FileSystemRights,
                rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)));
        }
        return new FolderFacts(true, false, owner, security.AreAccessRulesProtected, aces);
    }
}
