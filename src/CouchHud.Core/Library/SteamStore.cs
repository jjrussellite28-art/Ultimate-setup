using System.Text.Json;

namespace CouchHud.Core.Library;

/// <summary>Public Steam store endpoints, no API key needed.</summary>
public static class SteamStore
{
    /// <summary>
    /// App id of the store entry that is the same game by name (see <see cref="NameMatch"/>),
    /// or null. Near matches are rejected on purpose: wrong data is worse than none.
    /// </summary>
    public static async Task<string?> FindAppIdAsync(HttpClient http, string name, CancellationToken ct)
    {
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&l=english&cc=US";
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;

        foreach (var candidate in items.EnumerateArray())
        {
            // DLC, soundtracks and bundles come back from the search too; only the game itself has the art.
            if (candidate.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() != "app") continue;
            if (candidate.TryGetProperty("name", out var n) && NameMatch.Same(n.GetString() ?? "", name))
            {
                return candidate.GetProperty("id").GetRawText();
            }
        }
        return null;
    }

    /// <summary>App id for library items that come from Steam, null otherwise.</summary>
    public static string? AppIdOf(Sdk.LibraryItem item) =>
        item.Id.StartsWith("steam:", StringComparison.Ordinal) ? item.Id["steam:".Length..] : null;
}

/// <summary>
/// Name to Steam app id, remembered on disk (hits 90 days, misses 7), so each name is searched
/// once however many languages, screens and restarts ask for it.
/// </summary>
public sealed class SteamAppIds
{
    private static readonly TimeSpan HitTtl = TimeSpan.FromDays(90);
    private static readonly TimeSpan MissTtl = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly string _file;
    private readonly Dictionary<string, Entry> _cache;
    private readonly object _lock = new();

    internal sealed record Entry(string? AppId, DateTimeOffset Checked);

    public SteamAppIds(HttpClient http, string file)
    {
        _http = http;
        _file = file;
        try
        {
            _cache = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _cache = new();
        }
    }

    /// <summary>The cached answer without asking: (known, appId). Expired entries count as unknown.</summary>
    public (bool Known, string? AppId) Peek(string name)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(NameMatch.Canonical(name), out var e)
                && DateTimeOffset.UtcNow - e.Checked < (e.AppId is null ? MissTtl : HitTtl))
            {
                return (true, e.AppId);
            }
        }
        return (false, null);
    }

    public async Task<string?> FindAsync(string name, CancellationToken ct)
    {
        if (Peek(name) is { Known: true } known) return known.AppId;
        var id = await SteamStore.FindAppIdAsync(_http, name, ct);
        lock (_lock)
        {
            _cache[NameMatch.Canonical(name)] = new Entry(id, DateTimeOffset.UtcNow);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
                File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(_cache));
                File.Move(_file + ".tmp", _file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[steam] could not write id cache: {ex.Message}");
            }
        }
        return id;
    }
}
