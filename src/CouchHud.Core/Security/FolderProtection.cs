namespace CouchHud.Core.Security;

/// <summary>One allow entry of a folder's ACL, reduced to what the check needs.</summary>
/// <param name="Sid">The trustee as a SID string (S-1-5-32-545 …).</param>
/// <param name="Rights">FileSystemRights bits.</param>
/// <param name="AppliesHere">False for an inherit-only entry: it is for the children, not for this folder.</param>
public readonly record struct AclEntry(string Sid, int Rights, bool AppliesHere = true);

/// <summary>
/// Decides from the ACL whether a folder is protected, i.e. only administrators (and the system) can change what is in it.
/// A write probe cannot answer that: the check for an elevated helper runs in the elevated process, where the probe
/// succeeds in every folder the administrator may write to, protected or not. So the question is "who is allowed to
/// write", not "can I write right now".
/// </summary>
public static class FolderProtection
{
    // FileSystemRights bits.
    public const int WriteData = 0x2;
    public const int AppendData = 0x4;
    public const int DeleteChild = 0x40;
    public const int Delete = 0x10000;
    public const int ChangePermissions = 0x40000;
    public const int TakeOwnership = 0x80000;

    /// <summary>Whatever lets someone add, change or replace files in the folder, or change who may.</summary>
    public const int WriteMask = WriteData | AppendData | DeleteChild | Delete | ChangePermissions | TakeOwnership;

    /// <summary>What it takes to swap a folder (or a file in it) without being allowed to write inside it.</summary>
    public const int ReplaceMask = DeleteChild | Delete | ChangePermissions | TakeOwnership;

    private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18",      // SYSTEM
        "S-1-5-32-544",  // Administrators
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // TrustedInstaller
        "S-1-3-0"        // CREATOR OWNER: only a placeholder for whoever creates something, nobody can create anything here
    };

    public static bool IsTrusted(string sid) => Trusted.Contains(sid);

    // Generic rights as they can stand in an ACE, and the file rights they mean (FILE_GENERIC_*, FILE_ALL_ACCESS).
    public const int GenericAll = 0x10000000;
    public const int GenericExecute = 0x20000000;
    public const int GenericWrite = 0x40000000;
    public const int GenericRead = unchecked((int)0x80000000);
    public const int FileAllAccess = 0x1F01FF;
    public const int FileGenericWrite = 0x120116;
    public const int FileGenericRead = 0x120089;
    public const int FileGenericExecute = 0x1200A0;

    /// <summary>
    /// The specific rights an entry grants: GENERIC_WRITE or GENERIC_ALL in an ACE grant write access although none of the
    /// specific write bits is set, so they are mapped before any mask is applied.
    /// </summary>
    public static int MapGeneric(int rights)
    {
        var mapped = rights & ~(GenericAll | GenericExecute | GenericWrite | GenericRead);
        if ((rights & GenericAll) != 0) mapped |= FileAllAccess;
        if ((rights & GenericWrite) != 0) mapped |= FileGenericWrite;
        if ((rights & GenericRead) != 0) mapped |= FileGenericRead;
        if ((rights & GenericExecute) != 0) mapped |= FileGenericExecute;
        return mapped;
    }

    /// <summary>
    /// True when nobody but administrators, SYSTEM and TrustedInstaller may write (<paramref name="mask"/>) according
    /// to <paramref name="entries"/> (allow entries only; deny entries cannot make an untrusted writer harmless here)
    /// and the owner is one of them (an owner can always change the ACL).
    /// </summary>
    public static bool IsProtected(string? ownerSid, IEnumerable<AclEntry> entries, int mask = WriteMask)
    {
        if (ownerSid is null || !IsTrusted(ownerSid)) return false;
        return !entries.Any(e => e.AppliesHere && (MapGeneric(e.Rights) & mask) != 0 && !IsTrusted(e.Sid));
    }
}
