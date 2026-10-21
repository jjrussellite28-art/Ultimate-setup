using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CouchHud.Core.Saves;

/// <summary>Games of the manifest by store id and by name. Ambiguous names never match: a wrong game's saves are worse than none.</summary>
public sealed class ManifestIndex
{
    private readonly Dictionary<long, ManifestEntry> _bySteam = new();
    private readonly Dictionary<long, ManifestEntry> _byGog = new();
    private readonly Dictionary<string, ManifestEntry> _byExactName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ManifestEntry>> _byNormalized = new();
    private readonly Dictionary<string, List<ManifestEntry>> _byInstallDir = new();

    public int Count => _byExactName.Count;

    public static ManifestIndex Load(TextReader reader)
    {
        var index = new ManifestIndex();
        foreach (var entry in LudusaviManifestReader.Read(reader)) index.Add(entry);
        return index;
    }

    public void Add(ManifestEntry entry)
    {
        _byExactName[entry.Name] = entry;
        AddTo(_byNormalized, NormalizeName(entry.Name), entry);
        if (entry.InstallDir is { Length: > 0 }) AddTo(_byInstallDir, NormalizeName(entry.InstallDir), entry);
        if (entry.SteamId is { } id) _bySteam.TryAdd(id, entry);
        foreach (var extra in entry.ExtraSteamIds) _bySteam.TryAdd(extra, entry);
        if (entry.GogId is { } gog) _byGog.TryAdd(gog, entry);
    }

    private static void AddTo(Dictionary<string, List<ManifestEntry>> map, string key, ManifestEntry entry)
    {
        if (key.Length == 0) return;
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(entry);
    }

    private static ManifestEntry? Unique(Dictionary<string, List<ManifestEntry>> map, string key) =>
        map.TryGetValue(key, out var list) && list.Count == 1 ? list[0] : null;

    /// <summary>
    /// Store id first (Steam, GOG), then the exact name, then the normalised name and last the install
    /// folder name; the last two only if exactly one game has it. Follows aliases (a few hops).
    /// </summary>
    public ManifestEntry? Find(long? steamAppId, string name, long? gogId = null)
    {
        ManifestEntry? entry = null;
        if (steamAppId is { } sid) _bySteam.TryGetValue(sid, out entry);
        if (entry is null && gogId is { } gid) _byGog.TryGetValue(gid, out entry);
        if (entry is null) _byExactName.TryGetValue(name, out entry);
        if (entry is null)
        {
            var normalized = NormalizeName(name);
            entry = Unique(_byNormalized, normalized) ?? Unique(_byInstallDir, normalized);
        }
        for (var hops = 0; hops < 4 && entry?.Alias is { Length: > 0 } alias && _byExactName.TryGetValue(alias, out var target); hops++)
        {
            entry = target;
        }
        return entry;
    }

    /// <summary>Lowercase letters and digits only, so "The Witcher® 3: Wild Hunt" matches "The Witcher 3 Wild Hunt".</summary>
    public static string NormalizeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}

/// <summary>
/// The manifest on disk: downloaded at most once a week (conditional request with ETag), parsed
/// lazily on first use and kept in memory. Offline or a failed update keeps the old file.
/// </summary>
public sealed class ManifestStore(HttpClient http, string directory)
{
    public const string Url = "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(7);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ManifestIndex? _index;

    private string FilePath => Path.Combine(directory, "manifest.yaml");
    private string MetaPath => Path.Combine(directory, "manifest.meta.json");

    internal sealed record Meta(string? ETag, DateTimeOffset Checked);

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The parsed manifest, or null if there is none yet and none could be downloaded.</summary>
    public async Task<ManifestIndex?> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var updated = await UpdateIfDueAsync(ct);
            if (_index is not null && !updated) return _index;
            if (!File.Exists(FilePath)) return null;
            _index = await Task.Run(() =>
            {
                using var reader = new StreamReader(FilePath, Encoding.UTF8, true, 64 * 1024);
                return ManifestIndex.Load(reader);
            }, ct);
            return _index;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <returns>True if a new file was stored.</returns>
    internal async Task<bool> UpdateIfDueAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var meta = ReadMeta();
        var have = File.Exists(FilePath);
        if (have && meta is not null && Now() - meta.Checked < UpdateInterval) return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            if (have && meta?.ETag is { Length: > 0 } etag && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                WriteMeta(new Meta(meta?.ETag, Now()));
                return false;
            }
            response.EnsureSuccessStatusCode();

            var temp = FilePath + ".part";
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await source.CopyToAsync(target, ct);
            }
            File.Move(temp, FilePath, true);
            WriteMeta(new Meta(response.Headers.ETag?.ToString(), Now()));
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            // Offline or GitHub hiccup: the old file does fine. Try again next time.
            Console.Error.WriteLine($"[saves] manifest update: {ex.Message}");
            return false;
        }
    }

    private Meta? ReadMeta()
    {
        try
        {
            return File.Exists(MetaPath) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private void WriteMeta(Meta meta)
    {
        try
        {
            File.WriteAllText(MetaPath, JsonSerializer.Serialize(meta));
        }
        catch (IOException)
        {
        }
    }
}
