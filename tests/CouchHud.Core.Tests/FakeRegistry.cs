using CouchHud.Core.Platform;

namespace CouchHud.Core.Tests;

/// <summary>
/// In-memory registry. All values live in one typed store (string, int or byte[]) so writing a
/// different type over a value behaves like the real registry. <see cref="Values"/> and
/// <see cref="Binary"/> are assignment shortcuts for tests; reads return copies.
/// </summary>
internal sealed class FakeRegistry : IRegistry
{
    public Dictionary<string, Dictionary<string, object>> Data { get; } = new(StringComparer.OrdinalIgnoreCase);

    public StringValues Values { get; }
    public BinaryValues Binary { get; }

    public FakeRegistry()
    {
        Values = new(this);
        Binary = new(this);
    }

    private T? Get<T>(string key, string name) =>
        Data.TryGetValue(key, out var v) && v.TryGetValue(name, out var o) && o is T t ? t : default;

    private void Set(string key, string name, object value)
    {
        if (!Data.TryGetValue(key, out var v)) Data[key] = v = new(StringComparer.OrdinalIgnoreCase);
        v[name] = value;
    }

    public string? ReadString(string key, string name) => Get<string>(key, name);
    public int? ReadDword(string key, string name) => Data.TryGetValue(key, out var v) && v.TryGetValue(name, out var o) && o is int i ? i : null;
    public byte[]? ReadBinary(string key, string name) => Get<byte[]>(key, name);

    public IReadOnlyDictionary<string, string> ReadValues(string key) =>
        Data.TryGetValue(key, out var v)
            ? v.Where(p => p.Value is string).ToDictionary(p => p.Key, p => (string)p.Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>();

    public RegistryValueType GetValueType(string key, string name) =>
        !Data.TryGetValue(key, out var v) || !v.TryGetValue(name, out var o) ? RegistryValueType.Missing
        : o switch { string => RegistryValueType.String, int => RegistryValueType.Dword, _ => RegistryValueType.Other };

    public IReadOnlyList<string> SubKeys(string key) =>
        Data.Keys.Where(k => k.StartsWith(key + @"\", StringComparison.OrdinalIgnoreCase))
            .Select(k => k[(key.Length + 1)..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void WriteString(string key, string name, string value) => Set(key, name, value);
    public void WriteDword(string key, string name, int value) => Set(key, name, value);

    public void DeleteValue(string key, string name)
    {
        if (Data.TryGetValue(key, out var v)) v.Remove(name);
    }

    /// <summary>Keys that exist without values (created by hand in a test, or left behind by a delete).</summary>
    public bool KeyExists(string key) => Data.ContainsKey(key) || SubKeys(key).Count > 0;

    public void DeleteKeyIfEmpty(string key)
    {
        if (Data.TryGetValue(key, out var v) && v.Count == 0 && SubKeys(key).Count == 0) Data.Remove(key);
    }

    /// <summary>registry.Values[key] = new() { [name] = value } adds string values to the key.</summary>
    internal sealed class StringValues(FakeRegistry owner)
    {
        public Dictionary<string, string> this[string key]
        {
            get => new(owner.ReadValues(key), StringComparer.OrdinalIgnoreCase);
            set
            {
                foreach (var (name, v) in value) owner.Set(key, name, v);
            }
        }
    }

    /// <summary>registry.Binary[@"key\name"] = bytes.</summary>
    internal sealed class BinaryValues(FakeRegistry owner)
    {
        public byte[]? this[string path]
        {
            get
            {
                var i = path.LastIndexOf('\\');
                return owner.ReadBinary(path[..i], path[(i + 1)..]);
            }
            set
            {
                var i = path.LastIndexOf('\\');
                owner.Set(path[..i], path[(i + 1)..], value!);
            }
        }
    }
}
