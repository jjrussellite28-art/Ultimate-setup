using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CouchHud.Service.Authorization;

/// <summary>
/// Who may send the service a user-defined control (SERVICE_USER_DEFINED_CONTROL, "CR") or take it over (generic all, generic execute,
/// WRITE_DAC, WRITE_OWNER): an administrator's approval rests on it (<see cref="AdminApprovals"/>). Checked at every start against the
/// service's real access list (QueryServiceObjectSecurity) as an allowlist (review F3): only SYSTEM and Administrators may hold those rights;
/// anybody else in any allow entry (A, XA, OA, ZA), or no access list at all (NO_ACCESS_CONTROL, a NULL DACL), makes the service ignore every
/// control and raise an alarm.
/// </summary>
public static class ServiceDacl
{
    // SERVICE_USER_DEFINED_CONTROL, SERVICE_CHANGE_CONFIG, WRITE_DAC, WRITE_OWNER, GENERIC_ALL, GENERIC_EXECUTE, GENERIC_WRITE (the generic ones
    // include the control or the configuration for a service).
    private const uint Dangerous = 0x100 | 0x2 | 0x40000 | 0x80000 | 0x10000000 | 0x20000000 | 0x40000000;

    /// <summary>The two-letter rights of SDDL and their masks (the standard, generic, file and registry ones Windows writes for combined masks).</summary>
    private static readonly IReadOnlyDictionary<string, uint> RightCodes = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["GA"] = 0x10000000, ["GR"] = 0x80000000, ["GW"] = 0x40000000, ["GX"] = 0x20000000,
        ["RC"] = 0x20000, ["SD"] = 0x10000, ["WD"] = 0x40000, ["WO"] = 0x80000,
        ["CC"] = 0x1, ["DC"] = 0x2, ["LC"] = 0x4, ["SW"] = 0x8, ["RP"] = 0x10, ["WP"] = 0x20, ["DT"] = 0x40, ["LO"] = 0x80, ["CR"] = 0x100,
        ["FA"] = 0x1F01FF, ["FR"] = 0x120089, ["FW"] = 0x120116, ["FX"] = 0x1200A0,
        ["KA"] = 0xF003F, ["KR"] = 0x20019, ["KW"] = 0x20006, ["KX"] = 0x20019,
    };

    private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase) { "SY", "BA", "S-1-5-18", "S-1-5-32-544" };
    private static readonly HashSet<string> AllowTypes = new(StringComparer.OrdinalIgnoreCase) { "A", "XA", "OA", "ZA" };

    /// <summary>
    /// The trustees other than SYSTEM and Administrators that may control or take over the service by this SDDL (empty: none). An access
    /// list that allows everything (NO_ACCESS_CONTROL) is reported as such. Null when it cannot be read at all.
    /// </summary>
    public static IReadOnlyList<string>? UntrustedControllers(string? sddl)
    {
        if (string.IsNullOrWhiteSpace(sddl)) return null;
        var dacl = sddl.IndexOf("D:", StringComparison.Ordinal);
        if (dacl < 0) return null;
        var end = sddl.IndexOf("S:", dacl, StringComparison.Ordinal);
        var part = end < 0 ? sddl[(dacl + 2)..] : sddl[(dacl + 2)..end];
        var header = part.Split('(')[0];
        if (header.Contains("NO_ACCESS_CONTROL", StringComparison.OrdinalIgnoreCase)) return ["NO_ACCESS_CONTROL"];
        var found = new List<string>();
        foreach (var ace in Aces(part))
        {
            var fields = ace.Split(';');
            if (fields.Length < 6) return ["unreadable entry"];
            if (!AllowTypes.Contains(fields[0])) continue;
            var sid = fields[5];
            if (Trusted.Contains(sid) || !Grants(fields[2])) continue;
            if (!found.Contains(sid)) found.Add(sid);
        }
        return found;
    }

    /// <summary>The entries of a DACL string, each without its outer parentheses; a conditional entry's own parentheses stay inside.</summary>
    private static IEnumerable<string> Aces(string dacl)
    {
        var depth = 0;
        var start = -1;
        for (var i = 0; i < dacl.Length; i++)
        {
            if (dacl[i] == '(')
            {
                if (depth == 0) start = i + 1;
                depth++;
            }
            else if (dacl[i] == ')' && depth > 0)
            {
                depth--;
                if (depth == 0) yield return dacl[start..i];
            }
        }
    }

    /// <summary>Whether the rights include a dangerous one; anything that cannot be read (an unknown code, odd length, bad hex) counts as granting.</summary>
    private static bool Grants(string rights)
    {
        if (rights.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return !uint.TryParse(rights[2..], System.Globalization.NumberStyles.HexNumber, null, out var mask) || (mask & Dangerous) != 0;
        }
        if (rights.Length % 2 != 0) return true;
        for (var i = 0; i < rights.Length; i += 2)
        {
            if (!RightCodes.TryGetValue(rights.Substring(i, 2), out var mask) || (mask & Dangerous) != 0) return true;
        }
        return false;
    }

    private const uint ScManagerConnect = 0x1;
    private const uint ReadControl = 0x20000;
    private const int DaclSecurityInformation = 4;
    private const int SddlRevision1 = 1;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceObjectSecurity(IntPtr service, int information, byte[]? descriptor, int size, out int needed);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(byte[] descriptor, int revision, int information, out IntPtr text, out int length);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>The service's own access list as SDDL (D: part), null when it cannot be read.</summary>
    public static string? Read(string serviceName)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return null;
        try
        {
            var service = OpenService(manager, serviceName, ReadControl);
            if (service == IntPtr.Zero) return null;
            try
            {
                QueryServiceObjectSecurity(service, DaclSecurityInformation, null, 0, out var needed);
                if (needed is <= 0 or > 64 * 1024) return null;
                var descriptor = new byte[needed];
                if (!QueryServiceObjectSecurity(service, DaclSecurityInformation, descriptor, descriptor.Length, out _)) return null;
                if (!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, SddlRevision1, DaclSecurityInformation, out var text, out _)) return null;
                try
                {
                    return Marshal.PtrToStringUni(text);
                }
                finally
                {
                    LocalFree(text);
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }
}
