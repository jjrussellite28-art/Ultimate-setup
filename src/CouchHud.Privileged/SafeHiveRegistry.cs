using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using CouchHud.Core.Platform;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Privileged;

/// <summary>Whether a registry key below a user's Policies keys is protected the way Windows protects them. Pure.</summary>
public static class PolicyKeyRules
{
    private static readonly IReadOnlySet<string> Trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ProtectedFolderRules.SystemSid, ProtectedFolderRules.AdministratorsSid, ProtectedFolderRules.TrustedInstallerSid };

    // KEY_SET_VALUE, KEY_CREATE_SUB_KEY, KEY_CREATE_LINK, DELETE, WRITE_DAC, WRITE_OWNER, GENERIC_ALL, GENERIC_WRITE.
    private const int WriteRights = 0x2 | 0x4 | 0x20 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000;

    /// <summary>Why the key cannot be trusted (its owner, or someone else may change it), or null.</summary>
    public static string? Problem(string? ownerSid, IEnumerable<AceFacts> aces)
    {
        if (ownerSid is null || !Trusted.Contains(ownerSid)) return "foreign owner";
        foreach (var ace in aces)
        {
            if (!ace.Allow || ace.InheritOnly || Trusted.Contains(ace.Sid)) continue;
            if ((ace.Mask & WriteRights) != 0) return "others may write";
        }
        return null;
    }

    /// <summary>The access list of a Policies key the service creates: owner Administrators, protected, the account and users may read.</summary>
    public static string CreateSddl(string accountSid) => $"O:BAG:SYD:P(A;CI;KA;;;SY)(A;CI;KA;;;BA)(A;CI;KR;;;{accountSid})(A;CI;KR;;;BU)";
}

/// <summary>
/// A user's hive (<c>HKEY_USERS\&lt;name&gt;</c>, keys written as <c>HKCU\…</c>) that never follows a registry symbolic link: every key is
/// reached component by component from the hive's root with REG_OPTION_OPEN_LINK, and a component that is a link is refused. From
/// the first component named <c>Policies</c> on, every key must also be owned by SYSTEM, Administrators or TrustedInstaller and
/// writable by nobody else (<see cref="PolicyKeyRules"/>); one the service has to create gets <see cref="PolicyKeyRules.CreateSddl"/>.
/// Operations then work on the handle, so nothing can be swapped between the check and the write. A refusal is an
/// <see cref="UnauthorizedAccessException"/>; nothing is written then.
/// </summary>
public sealed class SafeHiveRegistry(string hiveName, string accountSid) : IRegistry
{
    private const int ReadAccess = 0x20019;               // KEY_READ (includes READ_CONTROL)
    private const int WriteAccess = 0x20019 | 0x20006 | 0x10000;   // KEY_READ | KEY_WRITE | DELETE
    private const int OptionOpenLink = 0x8;
    private const int ErrorFileNotFound = 2;
    private const int RegLink = 6;
    private const int OpenedExistingKey = 2;
    private static readonly IntPtr HkeyUsers = unchecked((IntPtr)(int)0x80000003);

    public string? ReadString(string key, string name) => Read(key, k => k.GetValue(name) as string);

    public IReadOnlyDictionary<string, string> ReadValues(string key) => Read(key, k =>
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in k.GetValueNames())
        {
            if (k.GetValue(name) is { } value) values[name] = value.ToString() ?? "";
        }
        return (IReadOnlyDictionary<string, string>)values;
    }) ?? new Dictionary<string, string>();

    public byte[]? ReadBinary(string key, string name) => Read(key, k => k.GetValue(name) as byte[]);

    public int? ReadDword(string key, string name) => Read(key, k => k.GetValue(name) is int i ? i : (int?)null);

    public RegistryValueType GetValueType(string key, string name) => Read(key, k =>
    {
        if (k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is null) return RegistryValueType.Missing;
        return k.GetValueKind(name) switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => RegistryValueType.String,
            RegistryValueKind.DWord => RegistryValueType.Dword,
            _ => RegistryValueType.Other
        };
    });

    public IReadOnlyList<string> SubKeys(string key) => Read(key, k => (IReadOnlyList<string>)k.GetSubKeyNames()) ?? [];

    public bool KeyExists(string key) => Read(key, _ => true);

    public void WriteString(string key, string name, string value)
    {
        using var k = Open(key, create: true, WriteAccess)!;
        k.SetValue(name, value, RegistryValueKind.String);
    }

    public void WriteDword(string key, string name, int value)
    {
        using var k = Open(key, create: true, WriteAccess)!;
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    public void DeleteValue(string key, string name)
    {
        using var k = Open(key, create: false, WriteAccess);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void DeleteKeyIfEmpty(string key)
    {
        var slash = key.LastIndexOf('\\');
        if (slash <= 0) return;
        using (var k = Open(key, create: false, ReadAccess))
        {
            if (k is null || k.SubKeyCount > 0 || k.ValueCount > 0) return;
        }
        using var parent = Open(key[..slash], create: false, WriteAccess);
        // The child was just checked to be no link (and below Policies, nobody but administrators can replace it).
        parent?.DeleteSubKey(key[(slash + 1)..], throwOnMissingSubKey: false);
    }

    private T? Read<T>(string key, Func<RegistryKey, T> read)
    {
        using var k = Open(key, create: false, ReadAccess);
        return k is null ? default : read(k);
    }

    /// <summary>The key, reached without following a link; null when it (or a key above it) is missing and <paramref name="create"/> is false.</summary>
    private RegistryKey? Open(string key, bool create, int access)
    {
        if (!key.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("only HKCU keys", nameof(key));
        var parts = key[5..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("no key", nameof(key));
        var policies = Array.FindIndex(parts, p => p.Equals("Policies", StringComparison.OrdinalIgnoreCase));
        Check(RegOpenKeyEx(HkeyUsers, hiveName, 0, ReadAccess, out var current), key);
        try
        {
            for (var i = 0; i < parts.Length; i++)
            {
                var last = i == parts.Length - 1;
                var want = last ? access : ReadAccess;
                var code = RegOpenKeyEx(current, parts[i], OptionOpenLink, want, out var next);
                if (code == ErrorFileNotFound)
                {
                    if (!create) return null;
                    next = Create(current, parts[i], want, protect: policies >= 0 && i >= policies, out var existed);
                    if (existed)
                    {
                        // Someone created it in the meantime: RegCreateKeyEx may have followed a link, so open it again without following.
                        next.Dispose();
                        Check(RegOpenKeyEx(current, parts[i], OptionOpenLink, want, out next), key);
                    }
                }
                else
                {
                    Check(code, key);
                }
                current.Dispose();
                current = next;
                if (IsLink(current)) throw new UnauthorizedAccessException($"registry link refused in {key}");
                if (policies >= 0 && i >= policies && Problem(current) is { } problem) throw new UnauthorizedAccessException($"policy key not protected ({problem}): {key}");
            }
            var result = RegistryKey.FromHandle(current);
            current = null!;
            return result;
        }
        finally
        {
            current?.Dispose();
        }
    }

    private SafeRegistryHandle Create(SafeRegistryHandle parent, string name, int access, bool protect, out bool existed)
    {
        SafeRegistryHandle created;
        int code;
        int disposition;
        if (protect)
        {
            var descriptor = new RawSecurityDescriptor(PolicyKeyRules.CreateSddl(accountSid));
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
                code = RegCreateKeyEx(parent, name, 0, null, 0, access, ref attributes, out created, out disposition);
            }
            finally
            {
                pinned.Free();
            }
        }
        else
        {
            var none = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
            code = RegCreateKeyEx(parent, name, 0, null, 0, access, ref none, out created, out disposition);
        }
        Check(code, name);
        existed = disposition == OpenedExistingKey;
        return created;
    }

    private static bool IsLink(SafeRegistryHandle key)
    {
        var size = 0;
        var code = RegQueryValueEx(key, "SymbolicLinkValue", IntPtr.Zero, out var type, IntPtr.Zero, ref size);
        return code == 0 && type == RegLink;
    }

    private static string? Problem(SafeRegistryHandle key)
    {
        const int OwnerAndDacl = 0x1 | 0x4;
        var size = 0;
        RegGetKeySecurity(key, OwnerAndDacl, null, ref size);
        if (size <= 0) return "unreadable";
        var buffer = new byte[size];
        if (RegGetKeySecurity(key, OwnerAndDacl, buffer, ref size) != 0) return "unreadable";
        var descriptor = new RawSecurityDescriptor(buffer, 0);
        var aces = new List<AceFacts>();
        if (descriptor.DiscretionaryAcl is { } dacl)
        {
            foreach (var entry in dacl)
            {
                if (entry is not CommonAce ace) return "unknown entry";
                aces.Add(new AceFacts(ace.SecurityIdentifier.Value, ace.AceType == AceType.AccessAllowed, ace.AccessMask, ace.AceFlags.HasFlag(AceFlags.InheritOnly)));
            }
        }
        else
        {
            return "no access list";
        }
        return PolicyKeyRules.Problem(descriptor.Owner?.Value, aces);
    }

    private static void Check(int code, string what)
    {
        if (code == 0) return;
        if (code == 5) throw new UnauthorizedAccessException($"access denied: {what}");
        throw new IOException($"registry error {code}: {what}", new Win32Exception(code));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    private static extern int RegOpenKeyEx(IntPtr key, string subKey, int options, int access, out SafeRegistryHandle result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    private static extern int RegOpenKeyEx(SafeRegistryHandle key, string subKey, int options, int access, out SafeRegistryHandle result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegCreateKeyExW")]
    private static extern int RegCreateKeyEx(SafeRegistryHandle key, string subKey, int reserved, string? className, int options, int access,
        ref SecurityAttributes attributes, out SafeRegistryHandle result, out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved, out int type, IntPtr data, ref int size);

    [DllImport("advapi32.dll")]
    private static extern int RegGetKeySecurity(SafeRegistryHandle key, int information, byte[]? descriptor, ref int size);
}
