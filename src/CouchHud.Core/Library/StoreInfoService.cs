using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

public sealed record StoreInfo(
    string? Description,
    IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers,
    string? ReleaseDate,
    IReadOnlyList<string> Genres)
{
    /// <summary>Trailer video URLs (mp4), best first; empty if the store has none.</summary>
    public IReadOnlyList<string> Trailers { get; init; } = [];

    /// <summary>Screenshot URLs in full size.</summary>
    public IReadOnlyList<string> Screenshots { get; init; } = [];

    /// <summary>Age rating (USK where the store has one, else Steam's required age); 0 = none given.</summary>
    public int? RequiredAge { get; init; }

    /// <summary>The age fields were read (entries cached by older versions lack them and are fetched again).</summary>
    public bool RatingKnown { get; init; }
}

/// <summary>
/// Description, developer, release date and genres from the Steam store, in
/// the active language (German or English) where available. Non-Steam entries are matched by exact name.
/// Results (including "not found") are cached on disk.
/// </summary>
public sealed partial class StoreInfoService
{
    private static readonly TimeSpan HitTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissTtl = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly string _cacheFile;
    private readonly Dictionary<string, CacheEntry> _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal sealed record CacheEntry(StoreInfo? Info, DateTimeOffset Checked);

    private readonly Func<string> _language;
    private readonly SteamAppIds _ids;

    /// <param name="language">"de" or "en" (the store texts come in that language); German when not given.</param>
    public StoreInfoService(HttpClient http, string cacheFile, Func<string>? language = null, SteamAppIds? ids = null)
    {
        _language = language ?? (() => "de");
        _http = http;
        _ids = ids ?? new SteamAppIds(http, Path.ChangeExtension(cacheFile, ".steamids.json"));
        _cacheFile = cacheFile;
        _cache = LoadCache(cacheFile);
    }

    /// <summary>German entries keep the plain id (older caches), English ones get a suffix: each language has its own cache.</summary>
    internal static string KeyOf(string itemId, string language) => language == "en" ? itemId + "@en" : itemId;

    /// <summary>What is cached already, without asking the store (null when unknown). Falls back to the other language (good enough for ratings).</summary>
    public StoreInfo? Cached(LibraryItem item)
    {
        try
        {
            var lang = _language();
            if (_cache.TryGetValue(KeyOf(item.Id, lang), out var e) && e.Info is not null) return e.Info;
            return _cache.TryGetValue(KeyOf(item.Id, lang == "en" ? "de" : "en"), out var other) ? other.Info : null;
        }
        catch (InvalidOperationException)
        {
            return null; // a write is in progress
        }
    }

    public async Task<StoreInfo?> GetAsync(LibraryItem item, CancellationToken ct = default)
    {
        var language = _language();
        var key = KeyOf(item.Id, language);
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out var cached)
                && (cached.Info is null || cached.Info.RatingKnown)
                && DateTimeOffset.UtcNow - cached.Checked < (cached.Info is null ? MissTtl : HitTtl))
            {
                return cached.Info;
            }

            StoreInfo? info;
            try
            {
                var appId = SteamStore.AppIdOf(item) ?? await _ids.FindAsync(item.Name, ct);
                info = appId is null ? null : await FetchDetails(appId, language, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                // Offline: show what we have, try again next time.
                Console.Error.WriteLine($"[store] {item.Name}: {ex.Message}");
                return cached?.Info;
            }

            _cache[key] = new CacheEntry(info, DateTimeOffset.UtcNow);
            SaveCache();
            return info;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StoreInfo?> FetchDetails(string appId, string language, CancellationToken ct)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&{(language == "en" ? "l=english&cc=US" : "l=german&cc=DE")}";
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(doc.RootElement, appId);
    }

    internal static StoreInfo? Parse(JsonElement root, string appId)
    {
        if (!root.TryGetProperty(appId, out var entry)
            || !entry.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True
            || !entry.TryGetProperty("data", out var data))
        {
            return null;
        }

        string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        IReadOnlyList<string> Strings(string name) =>
            data.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
                : [];

        var genres = data.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array
            ? g.EnumerateArray().Select(x => Str(x, "description")).OfType<string>().ToList()
            : [];
        var release = data.TryGetProperty("release_date", out var r) ? Str(r, "date") : null;

        return new StoreInfo(
            CleanText(Str(data, "short_description")),
            Strings("developers"),
            Strings("publishers"),
            string.IsNullOrWhiteSpace(release) ? null : release,
            genres)
        {
            RatingKnown = true,
            RequiredAge = ParseAge(data),
            Trailers = ParseTrailers(data),
            Screenshots = data.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array
                ? shots.EnumerateArray().Select(x => Str(x, "path_full")).OfType<string>().Take(8).ToList()
                : []
        };
    }

    /// <summary>The USK rating if the store lists one, else Steam's own required age.</summary>
    internal static int? ParseAge(JsonElement data)
    {
        static int Number(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(e.GetString(), out var n) => n,
            _ => 0
        };
        if (data.TryGetProperty("ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Object
            && ratings.TryGetProperty("usk", out var usk) && usk.ValueKind == JsonValueKind.Object
            && usk.TryGetProperty("rating", out var rating) && rating.ValueKind is JsonValueKind.Number or JsonValueKind.String)
        {
            return Math.Clamp(Number(rating), 0, 21);
        }
        return data.TryGetProperty("required_age", out var age) && Number(age) > 0 ? Math.Clamp(Number(age), 0, 21) : null;
    }

    /// <summary>One mp4 per trailer, preferring the highest quality the store offers.</summary>
    private static List<string> ParseTrailers(JsonElement data)
    {
        var result = new List<string>();
        if (!data.TryGetProperty("movies", out var movies) || movies.ValueKind != JsonValueKind.Array) return result;
        foreach (var movie in movies.EnumerateArray())
        {
            if (!movie.TryGetProperty("mp4", out var mp4) || mp4.ValueKind != JsonValueKind.Object) continue;
            foreach (var quality in new[] { "max", "480" })
            {
                if (mp4.TryGetProperty(quality, out var url) && url.ValueKind == JsonValueKind.String && url.GetString() is { Length: > 0 } u)
                {
                    result.Add(u.Replace("http://", "https://", StringComparison.Ordinal));
                    break;
                }
            }
            if (result.Count == 3) break;
        }
        return result;
    }

    /// <summary>Store texts contain HTML entities and the odd tag.</summary>
    internal static string? CleanText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = WebUtility.HtmlDecode(Tags().Replace(html, " "));
        return Whitespace().Replace(text, " ").Trim();
    }

    private static Dictionary<string, CacheEntry> LoadCache(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            File.WriteAllText(_cacheFile + ".tmp", JsonSerializer.Serialize(_cache));
            File.Move(_cacheFile + ".tmp", _cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[store] could not write cache: {ex.Message}");
        }
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
