using System.Globalization;
using System.Text.Json;
using CouchHud.Core.Localization;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>One hit of "Search artwork": which game (<paramref name="Match"/>, what gets pinned), its name, year, platforms and a picture of it.</summary>
/// <param name="Match">"steam:&lt;app id&gt;" or "sgdb:&lt;game id&gt;" (see <see cref="ArtworkMatches"/>).</param>
/// <param name="Source">"Steam" or "SteamGridDB".</param>
/// <param name="Thumb">A small cover on an allowed artwork host; null when there is none.</param>
/// <param name="Best">The hit the automatic match would take for this search (<see cref="NameMatch.Best{T}"/>).</param>
public sealed record ArtworkHit(string Match, string Name, string Source, string? Year, IReadOnlyList<string> Platforms, string? Thumb, bool Best = false);

/// <summary>The answer of "Search artwork": the hits, why something is missing (offline, no key, a service down) and whether a SteamGridDB key is set.</summary>
public sealed record ArtworkSearchResult(IReadOnlyList<ArtworkHit> Hits, string? Note, bool HasKey);

/// <summary>What the edit menu shows about an entry's artwork game: "auto" or "pinned" with the game's name.</summary>
public sealed record ArtworkMatchView(string Mode, string? Match, string? Name);

/// <summary>
/// The game an entry's artwork comes from when the user picked it ("Search artwork"): "steam:&lt;app id&gt;" or "sgdb:&lt;SteamGridDB game id&gt;". Only
/// these two forms are stored; the bridge's value is checked here before anything is written.
/// </summary>
public static class ArtworkMatches
{
    public const int MaxName = 100;
    public const int MaxHits = 16;

    /// <summary>Hits of SteamGridDB that get a thumbnail (one request each).</summary>
    public const int MaxThumbs = 8;

    /// <summary>A pin from the bridge: null (automatic) or one of the two forms; anything else is refused.</summary>
    public static string? Validate(string? match)
    {
        if (match is null) return null;
        var text = match.Trim();
        if (Parse(text) is not null) return text;
        throw new ArgumentException(Strings.Get("artwork.matchInvalid"));
    }

    /// <summary>("steam", app id) or ("sgdb", game id); null for anything else (a hand-edited config counts as automatic).</summary>
    public static (string Source, string Id)? Parse(string? match)
    {
        if (string.IsNullOrEmpty(match)) return null;
        var colon = match.IndexOf(':');
        if (colon <= 0) return null;
        var source = match[..colon];
        var id = match[(colon + 1)..];
        return source switch
        {
            "steam" when StoreMatches.IsAppId(id) => ("steam", id),
            "sgdb" when IsGameId(id) => ("sgdb", id),
            _ => null
        };
    }

    /// <summary>A SteamGridDB game id: digits, no leading zero, at most 10.</summary>
    public static bool IsGameId(string? text) =>
        text is { Length: > 0 and <= 10 } && text[0] != '0' && text.All(char.IsAsciiDigit) && long.Parse(text, CultureInfo.InvariantCulture) <= uint.MaxValue;

    /// <summary>The pinned Steam app id, if the pin is a Steam one.</summary>
    public static string? SteamAppId(string? match) => Parse(match) is ("steam", var id) ? id : null;

    /// <summary>The pinned SteamGridDB game id, if the pin is one of SteamGridDB.</summary>
    public static string? SgdbGameId(string? match) => Parse(match) is ("sgdb", var id) ? id : null;

    /// <summary>Steam's own entries have their art from Steam; the pin is for everything else.</summary>
    public static bool CanChoose(LibraryItem item) => SteamStore.AppIdOf(item) is null;

    public static ArtworkMatchView View(string? match, string? name) =>
        Parse(match) is null ? new ArtworkMatchView("auto", null, null) : new ArtworkMatchView("pinned", match, name);

    /// <summary>SteamGridDB's store names as the hit list shows them.</summary>
    internal static string PlatformName(string type) => type.ToLowerInvariant() switch
    {
        "steam" => "Steam",
        "egs" => "Epic",
        "gog" => "GOG",
        "origin" => "EA",
        "uplay" => "Ubisoft",
        "bnet" => "Battle.net",
        "flashpoint" => "Flashpoint",
        _ => type.Length > 0 ? char.ToUpperInvariant(type[0]) + type[1..] : type
    };

    /// <summary>One game of SteamGridDB's search: id, name, release year and stores.</summary>
    internal sealed record SgdbGame(string Id, string Name, string? Year, IReadOnlyList<string> Platforms);

    /// <summary>Reads SteamGridDB's { data: [ { id, name, release_date, types } ] }; entries without a usable id or name are skipped, names are clipped.</summary>
    internal static List<SgdbGame> ParseSgdbSearch(JsonElement root)
    {
        var list = new List<SgdbGame>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number) continue;
            var id = idElement.GetRawText();
            if (!IsGameId(id)) continue;
            var name = entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? InfoOverrides.Clip(n.GetString(), MaxName) : null;
            if (name is null) continue;
            string? year = null;
            if (entry.TryGetProperty("release_date", out var date) && date.ValueKind == JsonValueKind.Number && date.TryGetInt64(out var seconds)
                && seconds > 0 && seconds < 253402300800)
            {
                year = DateTimeOffset.FromUnixTimeSeconds(seconds).Year.ToString(CultureInfo.InvariantCulture);
            }
            var platforms = new List<string>();
            if (entry.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
            {
                foreach (var type in types.EnumerateArray())
                {
                    if (type.ValueKind == JsonValueKind.String && InfoOverrides.Clip(type.GetString(), 20) is { } t && t.All(char.IsAsciiLetterOrDigit))
                        platforms.Add(PlatformName(t));
                    if (platforms.Count == 6) break;
                }
            }
            list.Add(new SgdbGame(id, name, year, platforms));
            if (list.Count == MaxHits) break;
        }
        return list;
    }
}

public sealed partial class ArtworkResolver
{
    /// <summary>The game the user pinned for an entry's artwork (see <see cref="ArtworkMatches"/>), by item id; null: automatic. Set by the library.</summary>
    public Func<string, string?> ArtworkMatchOf { get; set; } = _ => null;

    /// <summary>The pin of an entry, if it is a valid one (Steam's own entries have none).</summary>
    private string? PinOf(LibraryItem item) =>
        SteamStore.AppIdOf(item) is null && ArtworkMatches.Parse(ArtworkMatchOf(item.Id)) is not null ? ArtworkMatchOf(item.Id) : null;

    /// <summary>
    /// The SteamGridDB route of an entry: a pinned game, a pinned or own Steam app id, the store match; else a search by name whose best hit
    /// must be the game (<see cref="NameMatch.Best{T}"/>): no route at all is better than a wrong game's pictures.
    /// </summary>
    private async Task<string?> SgdbRouteAsync(LibraryItem item, CancellationToken ct)
    {
        var pin = PinOf(item);
        if (ArtworkMatches.SgdbGameId(pin) is { } game) return $"game/{game}";
        if (ArtworkMatches.SteamAppId(pin) is { } pinnedApp) return $"steam/{pinnedApp}";
        if (SteamStore.AppIdOf(item) is { } own) return $"steam/{own}";
        if (StoreMatchOf(item.Id) is { } match && StoreMatches.IsAppId(match)) return $"steam/{match}";
        if (NameMatch.Canonical(item.Name).Length == 0) return null;
        List<ArtworkMatches.SgdbGame> games;
        try
        {
            using var search = await GetJson($"{SteamGridDb}/search/autocomplete/{Uri.EscapeDataString(item.Name)}", ct, auth: true);
            games = ArtworkMatches.ParseSgdbSearch(search.RootElement);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        return NameMatch.Best(item.Name, games, g => g.Name) is { } best ? $"game/{best.Id}" : null;
    }

    /// <summary>
    /// "Search artwork": the Steam store's and (with a key) SteamGridDB's games for a search the user typed, with year, platforms and a small
    /// cover each, the surest hits first. Nothing is asked when online artwork is off (Settings › Privacy); a service that fails leaves a note.
    /// </summary>
    public async Task<ArtworkSearchResult> SearchAsync(string query, CancellationToken ct = default)
    {
        query = StoreMatches.CheckQuery(query);
        if (!Online) return new ArtworkSearchResult([], Strings.Get("artwork.onlineOff"), _apiKey.Length > 0);
        var hits = new List<ArtworkHit>();
        string? note = null;
        try
        {
            foreach (var c in await SteamStore.SearchWithFallbackAsync(_http, query, ct))
            {
                var thumb = c.Thumb is { } t && ArtworkSources.IsAllowed(t) ? t : null;
                hits.Add(new ArtworkHit($"steam:{c.AppId}", c.Name, "Steam", null, ["Steam"], thumb));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            note = Strings.Get("artwork.steamDown");
        }

        if (_apiKey.Length > 0)
        {
            try
            {
                using var search = await GetJson($"{SteamGridDb}/search/autocomplete/{Uri.EscapeDataString(query)}", ct, auth: true);
                var games = ArtworkMatches.ParseSgdbSearch(search.RootElement);
                // The covers of the first hits, at most four requests at a time; a cover that fails or takes too long is just missing, the hit stays.
                var thumbs = new string?[games.Count];
                var timedOut = false;
                using var gate = new SemaphoreSlim(4);
                await Task.WhenAll(games.Take(ArtworkMatches.MaxThumbs).Select(async (game, i) =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var first = (await ListChoices($"{SteamGridDb}/grids/game/{game.Id}?dimensions=600x900,342x482,660x930", ct)).FirstOrDefault();
                        thumbs[i] = first is not null && ArtworkSources.IsAllowed(first.Thumb) ? first.Thumb : null;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        thumbs[i] = null;
                        if (ex is TaskCanceledException) timedOut = true;
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
                for (var i = 0; i < games.Count; i++)
                    hits.Add(new ArtworkHit($"sgdb:{games[i].Id}", games[i].Name, "SteamGridDB", games[i].Year, games[i].Platforms, thumbs[i]));
                if (timedOut) note ??= Strings.Get("artwork.sgdbPartial");
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                note = Strings.Get("artwork.sgdbKeyRejected");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                note ??= Strings.Get("artwork.sgdbDown");
            }
        }
        else
        {
            note ??= Strings.Get("artwork.searchNoKey");
        }
        return new ArtworkSearchResult(Rank(query, hits), note, _apiKey.Length > 0);
    }

    /// <summary>The surest hits first (each service's own order inside), at most <see cref="ArtworkMatches.MaxHits"/>; the automatic pick is marked.</summary>
    internal static IReadOnlyList<ArtworkHit> Rank(string query, IReadOnlyList<ArtworkHit> hits)
    {
        var best = NameMatch.Best(query, hits, h => h.Name);
        return hits
            .Select((h, i) => (Hit: h with { Best = ReferenceEquals(h, best) }, Score: NameMatch.Confidence(query, h.Name), Index: i))
            .OrderByDescending(x => x.Hit.Best)
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Hit)
            .Take(ArtworkMatches.MaxHits)
            .ToList();
    }
}
