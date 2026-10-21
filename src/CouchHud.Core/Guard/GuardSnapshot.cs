using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CouchHud.Core.Library;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Guard;

public enum GuardKind
{
    RegistryDword,
    RegistryString,
    VdfKey
}

/// <summary>
/// One intervention to apply. Location is a registry key ("HKCU\...") or a VDF file path;
/// Name is the registry value name or the VDF key path ("a/b/c"). Value is a decimal
/// number (invariant culture, 0..4294967295 or negative int) for DWORDs.
/// OnlyIfUnchanged is for a target that another program writes as well (Steam's own files): the restore puts the
/// original back only while the value is still the one we applied; if that program changed it since, it is left alone.
/// </summary>
public sealed record GuardChange(GuardKind Kind, string Location, string Name, string Value, bool OnlyIfUnchanged = false);

/// <summary>
/// A change plus the backed-up original (null = the value did not exist). For VDF keys OriginalRaw
/// is the value token as written in the file, MissingFrom the first path segment that did not exist
/// (parents from there on were created by us) and FileCreated whether the file itself was new.
/// </summary>
public sealed record GuardEntry(
    GuardKind Kind,
    string Location,
    string Name,
    string? Original,
    string Applied,
    string? OriginalRaw = null,
    int MissingFrom = 0,
    bool FileCreated = false,
    bool OnlyIfUnchanged = false);

/// <summary>Access to VDF keys for the guard; implemented on top of <see cref="VdfPatch"/>.</summary>
public interface IVdfStore
{
    bool Exists(string file);

    /// <summary>Looks the key up; a missing file counts as a missing key. Throws if the file can't be parsed.</summary>
    VdfLookup Lookup(string file, string keyPath);

    string? Read(string file, string keyPath) => Lookup(file, keyPath) is { Kind: VdfLookupKind.Value } l ? l.Value : null;

    void Write(string file, string keyPath, string value);

    /// <summary>Writes the value token verbatim (as returned in <see cref="VdfLookup.Raw"/>).</summary>
    void WriteRaw(string file, string keyPath, string rawToken);

    /// <summary>Deletes the key; parents at path index &gt;= pruneFrom go too if left empty.</summary>
    void Delete(string file, string keyPath, int? pruneFrom = null);

    /// <summary>Deletes the file if it exists and holds only whitespace.</summary>
    void DeleteFileIfBlank(string file);
}

/// <summary>Byte-preserving <see cref="IVdfStore"/> on the real file system. Files must be valid UTF-8.</summary>
public sealed class FileVdfStore : IVdfStore
{
    public bool Exists(string file) => File.Exists(file);

    public VdfLookup Lookup(string file, string keyPath) =>
        File.Exists(file)
            ? VdfPatch.Lookup(Load(file, out _), keyPath)
            : new VdfLookup(VdfLookupKind.Missing, null, null, 0);

    public void Write(string file, string keyPath, string value) =>
        Patch(file, text => VdfPatch.Set(text, keyPath, value));

    public void WriteRaw(string file, string keyPath, string rawToken) =>
        Patch(file, text => VdfPatch.SetRaw(text, keyPath, rawToken));

    public void Delete(string file, string keyPath, int? pruneFrom = null)
    {
        if (File.Exists(file)) Patch(file, text => VdfPatch.Delete(text, keyPath, pruneFrom));
    }

    public void DeleteFileIfBlank(string file)
    {
        if (File.Exists(file) && string.IsNullOrWhiteSpace(Load(file, out _))) File.Delete(file);
    }

    private static void Patch(string file, Func<string, string> edit)
    {
        var bom = false;
        var text = File.Exists(file) ? Load(file, out bom) : "";
        var patched = edit(text);
        if (patched != text || !File.Exists(file)) Save(file, patched, bom);
    }

    private static string Load(string file, out bool bom)
    {
        var bytes = File.ReadAllBytes(file);
        bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException($"{Path.GetFileName(file)} is not valid UTF-8; left untouched");
        }
    }

    private static void Save(string file, string text, bool bom)
    {
        var body = new UTF8Encoding(false).GetBytes(text);
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = file + ".couchhud.tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (bom) fs.Write([0xEF, 0xBB, 0xBF]);
            fs.Write(body);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, file, overwrite: true);
    }
}

/// <summary>
/// Reversible set of interventions (registry values, VDF keys). The first Apply of a target backs up
/// its original; later Applies never overwrite that backup. State is persisted as JSON (with a .bak of
/// the previous version) before anything is changed, so <see cref="RestoreLeftovers"/> can undo it after
/// a crash. A lock file serialises processes. Targets whose current state we cannot restore exactly
/// (wrong registry type, VDF block, unparseable file) are not touched and reported as errors. Methods
/// return error messages instead of throwing; entries that could not be restored stay in the snapshot.
/// </summary>
public sealed class GuardSnapshot
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IRegistry _registry;
    private readonly IVdfStore _vdf;
    private readonly string _statePath;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly List<GuardEntry> _entries = [];
    private readonly Func<GuardEntry, bool>? _accept;
    private int _rejected;

    /// <param name="accept">
    /// Only for an elevated process that reads a file a normal program could have written (see <see cref="ElevatedGuard"/>):
    /// entries it refuses are ignored, and the file is then left as it is (nothing is saved over it).
    /// </param>
    public GuardSnapshot(IRegistry registry, IVdfStore vdf, string statePath, Action<string>? log = null, Func<GuardEntry, bool>? accept = null)
    {
        _registry = registry;
        _vdf = vdf;
        _statePath = statePath;
        _log = log;
        _accept = accept;
        Load();
    }

    /// <summary>How long to wait for another process holding the state lock.</summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public IReadOnlyList<GuardEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public bool HasPending
    {
        get { lock (_gate) return _entries.Count > 0; }
    }

    /// <summary>Backs up originals (first time per target only), persists, then applies the values.</summary>
    public IReadOnlyList<string> Apply(IEnumerable<GuardChange> changes)
    {
        var errors = new List<string>();
        lock (_gate)
        {
            try
            {
                using var _ = AcquireLock();
                Load();
                ApplyLocked(changes, errors);
            }
            catch (Exception ex)
            {
                errors.Add($"guard state: {ex.Message}");
            }
        }
        Log(errors);
        return errors;
    }

    private void ApplyLocked(IEnumerable<GuardChange> changes, List<string> errors)
    {
        var before = _entries.ToList();
        var todo = new List<(GuardChange Change, bool IsNew)>();
        foreach (var change in changes)
        {
            try
            {
                if (change.Kind == GuardKind.RegistryDword) ParseDword(change.Value);
                if (_entries.Any(e => SameTarget(e, change)))
                {
                    todo.Add((change, false));
                    continue;
                }
                _entries.Add(Probe(change));
                todo.Add((change, true));
            }
            catch (Exception ex)
            {
                errors.Add(Describe(change.Kind, change.Location, change.Name, ex));
            }
        }

        if (!Save(_entries, errors)) // never touch the system if we can't undo it later
        {
            _entries.Clear();
            _entries.AddRange(before);
            return;
        }

        var dirty = false;
        foreach (var (change, isNew) in todo)
        {
            try
            {
                Write(change);
                if (!isNew)
                {
                    var i = _entries.FindIndex(e => SameTarget(e, change));
                    if (_entries[i].Applied != change.Value)
                    {
                        _entries[i] = _entries[i] with { Applied = change.Value };
                        dirty = true;
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(Describe(change.Kind, change.Location, change.Name, ex));
                if (isNew)
                {
                    _entries.RemoveAll(e => SameTarget(e, change)); // nothing to undo for a write that failed
                    dirty = true;
                }
            }
        }
        if (dirty) Save(_entries, errors);
    }

    /// <summary>Puts every original back (deleting values that did not exist) and clears the snapshot.</summary>
    public IReadOnlyList<string> Restore()
    {
        var errors = new List<string>();
        lock (_gate)
        {
            try
            {
                using var _ = AcquireLock();
                Load();
                var remaining = _entries.ToList();
                for (var i = remaining.Count - 1; i >= 0; i--)
                {
                    var e = remaining[i];
                    try
                    {
                        RestoreEntry(e);
                        remaining.RemoveAt(i);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(Describe(e.Kind, e.Location, e.Name, ex));
                    }
                }
                // Memory follows the file: only drop entries once the new state is on disk.
                if (Save(remaining, errors))
                {
                    _entries.Clear();
                    _entries.AddRange(remaining);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"guard state: {ex.Message}");
            }
        }
        Log(errors);
        return errors;
    }

    /// <summary>Undoes what an earlier (crashed) run left behind; same as <see cref="Restore"/> after loading.</summary>
    public IReadOnlyList<string> RestoreLeftovers() => Restore();

    /// <summary>
    /// Puts entries back into the snapshot after a restore turned out not to hold (another program wrote the old values
    /// again right after it): the next <see cref="Restore"/> tries them once more. Targets already in the snapshot are kept as they are.
    /// </summary>
    public IReadOnlyList<string> Reinstate(IEnumerable<GuardEntry> entries)
    {
        var errors = new List<string>();
        lock (_gate)
        {
            try
            {
                using var _ = AcquireLock();
                Load();
                foreach (var e in entries)
                {
                    if (!_entries.Any(x => x.Kind == e.Kind && string.Equals(x.Location, e.Location, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(x.Name, e.Name, StringComparison.OrdinalIgnoreCase)))
                        _entries.Add(e);
                }
                Save(_entries, errors);
            }
            catch (Exception ex)
            {
                errors.Add($"guard state: {ex.Message}");
            }
        }
        Log(errors);
        return errors;
    }

    // ---- helpers ---------------------------------------------------------------------

    private static bool SameTarget(GuardEntry e, GuardChange c) =>
        e.Kind == c.Kind
        && string.Equals(e.Location, c.Location, StringComparison.OrdinalIgnoreCase)
        && string.Equals(e.Name, c.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the current state as a backup entry; throws if it could not be restored exactly.</summary>
    private GuardEntry Probe(GuardChange c)
    {
        switch (c.Kind)
        {
            case GuardKind.RegistryDword:
            case GuardKind.RegistryString:
                var wanted = c.Kind == GuardKind.RegistryDword ? RegistryValueType.Dword : RegistryValueType.String;
                var actual = _registry.GetValueType(c.Location, c.Name);
                if (actual != RegistryValueType.Missing && actual != wanted)
                    throw new InvalidOperationException($"existing value is {actual}, expected {wanted}; not touched");
                string? original = null;
                if (actual != RegistryValueType.Missing)
                {
                    original = c.Kind == GuardKind.RegistryDword
                        ? FormatDword(_registry.ReadDword(c.Location, c.Name) ?? throw new InvalidOperationException("DWORD vanished while reading"))
                        : _registry.ReadString(c.Location, c.Name) ?? throw new InvalidOperationException("value vanished while reading");
                }
                return new GuardEntry(c.Kind, c.Location, c.Name, original, c.Value, OnlyIfUnchanged: c.OnlyIfUnchanged);

            default:
                var fileExisted = _vdf.Exists(c.Location);
                var lookup = _vdf.Lookup(c.Location, c.Name);
                return lookup.Kind switch
                {
                    VdfLookupKind.Value => new GuardEntry(c.Kind, c.Location, c.Name, lookup.Value, c.Value, lookup.Raw, OnlyIfUnchanged: c.OnlyIfUnchanged),
                    VdfLookupKind.Missing => new GuardEntry(c.Kind, c.Location, c.Name, null, c.Value,
                        MissingFrom: lookup.ExistingDepth, FileCreated: !fileExisted, OnlyIfUnchanged: c.OnlyIfUnchanged),
                    VdfLookupKind.Block => throw new InvalidOperationException("target is a block, not a value; not touched"),
                    _ => throw new InvalidOperationException("a parent of the target is a plain value, not a block; not touched")
                };
        }
    }

    private void Write(GuardChange c)
    {
        switch (c.Kind)
        {
            case GuardKind.RegistryDword: _registry.WriteDword(c.Location, c.Name, ParseDword(c.Value)); break;
            case GuardKind.RegistryString: _registry.WriteString(c.Location, c.Name, c.Value); break;
            default: _vdf.Write(c.Location, c.Name, c.Value); break;
        }
    }

    /// <summary>Whether the target still holds the value we applied (asked only for entries with OnlyIfUnchanged).</summary>
    public bool StillApplied(GuardEntry e) => e.Kind switch
    {
        GuardKind.RegistryDword => _registry.GetValueType(e.Location, e.Name) == RegistryValueType.Dword
                                   && _registry.ReadDword(e.Location, e.Name) is { } v && FormatDword(v) == FormatDword(ParseDword(e.Applied)),
        GuardKind.RegistryString => _registry.ReadString(e.Location, e.Name) == e.Applied,
        _ => _vdf.Lookup(e.Location, e.Name) is { Kind: VdfLookupKind.Value } l && l.Value == e.Applied
    };

    private void RestoreEntry(GuardEntry e)
    {
        // Somebody else (Steam) wrote the value since: it is theirs now, not ours to put back.
        if (e.OnlyIfUnchanged && !StillApplied(e)) return;
        switch (e.Kind)
        {
            case GuardKind.RegistryDword:
                if (e.Original is null) _registry.DeleteValue(e.Location, e.Name);
                else _registry.WriteDword(e.Location, e.Name, ParseDword(e.Original));
                break;
            case GuardKind.RegistryString:
                if (e.Original is null) _registry.DeleteValue(e.Location, e.Name);
                else _registry.WriteString(e.Location, e.Name, e.Original);
                break;
            default:
                if (e.Original is null)
                {
                    _vdf.Delete(e.Location, e.Name, e.MissingFrom);
                    if (e.FileCreated) _vdf.DeleteFileIfBlank(e.Location);
                }
                else if (e.OriginalRaw is not null) _vdf.WriteRaw(e.Location, e.Name, e.OriginalRaw);
                else _vdf.Write(e.Location, e.Name, e.Original);
                break;
        }
    }

    // DWORDs travel as unsigned decimal text (invariant culture); the registry API wants the int bit pattern.
    private static string FormatDword(int v) => unchecked((uint)v).ToString(CultureInfo.InvariantCulture);

    private static int ParseDword(string s)
    {
        var v = long.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (v < int.MinValue || v > uint.MaxValue) throw new OverflowException($"'{s}' is not a DWORD");
        return unchecked((int)(uint)(v & 0xFFFFFFFFL));
    }

    private static string Describe(GuardKind kind, string location, string name, Exception ex) =>
        $"{kind} {location} {name}: {ex.Message}";

    private void Log(List<string> errors)
    {
        foreach (var e in errors) _log?.Invoke(e);
    }

    // ---- persistence -----------------------------------------------------------------

    private FileStream AcquireLock()
    {
        var dir = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(_statePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"state lock held by another process: {ex.Message}");
            }
        }
    }

    /// <summary>Reloads the entries from disk; falls back to the .bak copy if the state file is corrupt.</summary>
    private void Load()
    {
        _entries.Clear();
        _rejected = 0;
        if (File.Exists(_statePath))
        {
            try
            {
                _entries.AddRange(Accepted(Read(_statePath)));
                return;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"guard state unreadable: {ex.Message}");
                try
                {
                    File.Move(_statePath, _statePath + ".bad", overwrite: true);
                }
                catch
                {
                    // best effort
                }
            }
        }
        var bak = _statePath + ".bak";
        if (!File.Exists(bak)) return;
        try
        {
            _entries.AddRange(Accepted(Read(bak)));
            _log?.Invoke("guard state recovered from backup");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"guard state backup unreadable, starting empty: {ex.Message}");
        }
    }

    private List<GuardEntry> Accepted(List<GuardEntry> read)
    {
        if (_accept is null) return read;
        var kept = read.Where(_accept).ToList();
        _rejected = read.Count - kept.Count;
        if (_rejected > 0) _log?.Invoke($"guard state: {_rejected} entries are not on the list of an elevated restore, left alone");
        return kept;
    }

    private static List<GuardEntry> Read(string path) =>
        JsonSerializer.Deserialize<List<GuardEntry>>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException("empty state");

    private bool Save(List<GuardEntry> entries, List<string> errors)
    {
        // Entries were left out when loading: the file must keep them for a later run without administrator rights.
        if (_rejected > 0) return true;
        try
        {
            var bak = _statePath + ".bak";
            if (entries.Count == 0)
            {
                if (File.Exists(_statePath)) File.Delete(_statePath);
                if (File.Exists(bak)) File.Delete(bak);
                return true;
            }
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(_statePath)) File.Copy(_statePath, bak, overwrite: true);
            var tmp = _statePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(entries, Json)));
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, _statePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            errors.Add($"guard state not saved: {ex.Message}");
            return false;
        }
    }
}
