using CouchHud.Core.Platform;
using Microsoft.Win32;

namespace CouchHud.Privileged;

/// <param name="userHive">
/// Where "HKCU\…" points: null is the HKCU of the process, otherwise the hive below HKEY_USERS with that name (a SID, or the
/// name a hive of a user who is not signed in was loaded under) - for an administrator process that tidies up other accounts.
/// </param>
public sealed class WindowsRegistry(string? userHive = null) : IRegistry
{
    public string? ReadString(string key, string name)
    {
        var (hive, path) = Split(key);
        using var k = hive.OpenSubKey(path);
        return k?.GetValue(name) as string;
    }

    public IReadOnlyDictionary<string, string> ReadValues(string key)
    {
        var (hive, path) = Split(key);
        using var k = hive.OpenSubKey(path);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (k is null) return values;
        foreach (var name in k.GetValueNames())
        {
            if (k.GetValue(name) is { } value) values[name] = value.ToString() ?? "";
        }
        return values;
    }

    public IReadOnlyList<string> SubKeys(string key)
    {
        var (hive, path) = Split(key);
        using var k = hive.OpenSubKey(path);
        return k?.GetSubKeyNames() ?? Array.Empty<string>();
    }

    public byte[]? ReadBinary(string key, string name)
    {
        var (hive, path) = Split(key);
        using var k = hive.OpenSubKey(path);
        return k?.GetValue(name) as byte[];
    }

    public int? ReadDword(string key, string name)
    {
        var (hive, path) = Split(key);
        using var k = hive.OpenSubKey(path);
        return k?.GetValue(name) is int i ? i : null;
    }

    public RegistryValueType GetValueType(string key, string name)
    {
        var (hive, path) = Split(key);
        using var k = hive.OpenSubKey(path);
        if (k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is null) return RegistryValueType.Missing;
        return k.GetValueKind(name) switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => RegistryValueType.String,
            RegistryValueKind.DWord => RegistryValueType.Dword,
            _ => RegistryValueType.Other
        };
    }

    public void WriteDword(string key, string name, int value)
    {
        var (hive, path) = Split(key);
        using var k = hive.CreateSubKey(path, writable: true);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    public void WriteString(string key, string name, string value)
    {
        var (hive, path) = Split(key);
        using var k = hive.CreateSubKey(path, writable: true);
        k.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string key, string name)
    {
        var (hive, path) = Split(key);
        using var k = hive.OpenSubKey(path, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public bool KeyExists(string key)
    {
        var (hive, path) = Split(key);
        using var k = hive.OpenSubKey(path);
        return k is not null;
    }

    public void DeleteKeyIfEmpty(string key)
    {
        var (hive, path) = Split(key);
        using (var k = hive.OpenSubKey(path))
        {
            if (k is null || k.SubKeyCount > 0 || k.ValueCount > 0) return;
        }
        hive.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    private (RegistryKey Hive, string Path) Split(string key)
    {
        var index = key.IndexOf('\\');
        if (index < 0) throw new ArgumentException($"invalid registry key {key}", nameof(key));
        var hive = key[..index].ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" when userHive is not null => Registry.Users,
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            var other => throw new ArgumentException($"unsupported hive {other}", nameof(key))
        };
        var path = key[(index + 1)..];
        if (userHive is not null && ReferenceEquals(hive, Registry.Users)) path = userHive + "\\" + path;
        return (hive, path);
    }
}
