namespace CouchHud.Core.Platform;

/// <summary>
/// Registry access behind an interface so Core stays testable off Windows.
/// Keys are written like "HKCU\Software\Valve\Steam".
/// </summary>
public interface IRegistry
{
    string? ReadString(string key, string name);

    IReadOnlyDictionary<string, string> ReadValues(string key);

    byte[]? ReadBinary(string key, string name);

    /// <summary>The DWORD value, or null if the value is missing or not a DWORD.</summary>
    int? ReadDword(string key, string name);

    /// <summary>
    /// Registry type of a value (Missing if absent). The default derives it from the typed readers;
    /// real registries should override it with the actual kind.
    /// </summary>
    RegistryValueType GetValueType(string key, string name) =>
        ReadDword(key, name) is not null ? RegistryValueType.Dword
        : ReadString(key, name) is not null ? RegistryValueType.String
        : ReadBinary(key, name) is not null ? RegistryValueType.Other
        : RegistryValueType.Missing;

    /// <summary>Names of the direct subkeys; empty if the key doesn't exist.</summary>
    IReadOnlyList<string> SubKeys(string key) => Array.Empty<string>();

    void WriteString(string key, string name, string value);

    void WriteDword(string key, string name, int value);

    void DeleteValue(string key, string name);

    /// <summary>Whether the key exists (even without values). The default only sees keys that have values or subkeys.</summary>
    bool KeyExists(string key) => ReadValues(key).Count > 0 || SubKeys(key).Count > 0;

    /// <summary>Removes the key if it has no values and no subkeys; anything else (or a missing key) is left alone.</summary>
    void DeleteKeyIfEmpty(string key)
    {
    }
}

public enum RegistryValueType
{
    Missing,
    String,
    Dword,
    /// <summary>Binary, multi-string, QWORD, ... anything we don't model.</summary>
    Other
}

public sealed class NullRegistry : IRegistry
{
    public static NullRegistry Instance { get; } = new();

    public string? ReadString(string key, string name) => null;

    public IReadOnlyDictionary<string, string> ReadValues(string key) => new Dictionary<string, string>();

    public byte[]? ReadBinary(string key, string name) => null;

    public int? ReadDword(string key, string name) => null;

    public void WriteString(string key, string name, string value) =>
        throw new PlatformNotSupportedException("Registry is only available on Windows");

    public void WriteDword(string key, string name, int value) =>
        throw new PlatformNotSupportedException("Registry is only available on Windows");

    public void DeleteValue(string key, string name) =>
        throw new PlatformNotSupportedException("Registry is only available on Windows");
}
