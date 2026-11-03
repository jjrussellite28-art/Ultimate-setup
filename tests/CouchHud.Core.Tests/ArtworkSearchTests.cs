using System.Net;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// "Search artwork" and the automatic match: a search hit that is not surely the game gives no pictures ("Mortal Kombat 1" once showed
/// "Mortal Kombat Arcade Kollection", SteamGridDB's first hit), the user's pick is pinned per entry and goes before everything else.
/// </summary>
public class ArtworkSearchTests
{
    private static string TempCache() => Path.Combine(Directory.CreateTempSubdirectory("couchhud-artsearch-").FullName, "cache.json");

    private static LibraryItem Game(string id, string name, Artwork? art = null) => new()
    {
        Id = id,
        Source = "folder",
        Name = name,
        Type = ItemType.Game,
        Launch = new LaunchTarget(LaunchKind.Url, "https://example.com"),
        Art = art ?? Artwork.Empty
    };

    private sealed class Host(params (string Part, string Json)[] routes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            foreach (var (part, json) in routes)
                if (url.Contains(part, StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            if (url.Contains("storesearch")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "items": [] }""", Encoding.UTF8, "application/json") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private const string Kollection = """{ "data": [ { "id": 1, "name": "Mortal Kombat Arcade Kollection" } ] }""";
    private const string Grid = """{ "data": [ { "url": "https://cdn2.steamgriddb.com/grid/x.png", "thumb": "https://cdn2.steamgriddb.com/thumb/x.png" } ] }""";

    [Fact]
    public async Task A_search_hit_that_only_starts_like_the_name_gives_no_pictures()
    {
        var host = new Host(("autocomplete", Kollection), ("/grids/", Grid), ("/heroes/", Grid), ("/logos/", Grid));
        var resolver = new ArtworkResolver(new HttpClient(host), TempCache(), "key");

        var result = await resolver.ResolveAsync([Game("folder:mk1", "Mortal Kombat 1")]);

        Assert.Empty((result ?? []).SelectMany(i => i.Art.Cover));
        Assert.DoesNotContain(host.Requests, r => r.Contains("/game/1"));
    }

    [Fact]
    public async Task The_sure_hit_is_taken_even_when_it_is_not_the_first_and_a_typo_still_finds_it()
    {
        var search = """{ "data": [ { "id": 1, "name": "Mortal Kombat Arcade Kollection" }, { "id": 2, "name": "Mortal Kombat 1" } ] }""";
        var host = new Host(("autocomplete", search), ("/grids/game/2", Grid));
        var resolver = new ArtworkResolver(new HttpClient(host), TempCache(), "key");

        var result = await resolver.ResolveAsync([Game("folder:mk1", "Mortal Combat 1")]);

        Assert.Equal(["https://cdn2.steamgriddb.com/grid/x.png"], result![0].Art.Cover);
        Assert.DoesNotContain(host.Requests, r => r.Contains("/game/1"));
    }

    [Fact]
    public async Task A_pinned_steamgriddb_game_goes_before_the_name_and_has_its_own_cache_line()
    {
        var host = new Host(("autocomplete", Kollection), ("/grids/game/99", Grid));
        var cache = TempCache();
        var resolver = new ArtworkResolver(new HttpClient(host), cache, "key") { ArtworkMatchOf = id => id == "folder:mk1" ? "sgdb:99" : null };

        var result = await resolver.ResolveAsync([Game("folder:mk1", "Mortal Kombat 1")]);

        Assert.Equal(["https://cdn2.steamgriddb.com/grid/x.png"], result![0].Art.Cover);
        Assert.DoesNotContain(host.Requests, r => r.Contains("autocomplete") || r.Contains("storesearch"));
        // Offline, the pin finds its art in the cache; without the pin the same entry has none (its name never matched).
        var offline = new ArtworkResolver(new HttpClient(new Host()), cache, "key") { ArtworkMatchOf = _ => "sgdb:99" };
        Assert.NotEmpty(offline.ApplyCached([Game("folder:mk1", "Mortal Kombat 1")])[0].Art.Cover);
        var unpinned = new ArtworkResolver(new HttpClient(new Host()), cache, "key");
        Assert.Empty(unpinned.ApplyCached([Game("folder:mk1", "Mortal Kombat 1")])[0].Art.Cover);
    }

    [Fact]
    public async Task A_pinned_steam_app_takes_the_steam_store_art_without_a_search()
    {
        var host = new Host();
        var resolver = new ArtworkResolver(new HttpClient(host), TempCache(), null) { ArtworkMatchOf = _ => "steam:1971870" };

        var result = await resolver.ResolveAsync([Game("folder:mk1", "Mortal Combat 1")]);

        Assert.Contains(result![0].Art.Cover, c => c.EndsWith("/1971870/library_600x900.jpg"));
        Assert.DoesNotContain(host.Requests, r => r.Contains("storesearch") || r.Contains("autocomplete"));
    }

    [Fact]
    public async Task Steams_own_entries_ignore_a_pin()
    {
        var resolver = new ArtworkResolver(new HttpClient(new Host()), TempCache(), null) { ArtworkMatchOf = _ => "steam:1" };

        var result = await resolver.ResolveAsync([Game("steam:620", "Portal 2")]);

        Assert.Contains(result![0].Art.Cover, c => c.Contains("/620/"));
        Assert.DoesNotContain(result[0].Art.Cover, c => c.Contains("/1/"));
    }

    [Fact]
    public async Task The_search_lists_both_services_with_year_platforms_and_thumbnails_the_surest_first()
    {
        var store = """{ "items": [ { "id": 2, "type": "app", "name": "Mortal Kombat Arcade Kollection", "tiny_image": "https://shared.akamai.steamstatic.com/a.jpg" }, { "id": 1971870, "type": "app", "name": "Mortal Kombat 1", "tiny_image": "https://evil.example/x.jpg" } ] }""";
        var sgdb = """{ "data": [ { "id": 5, "name": "Mortal Kombat 1", "release_date": 1694995200, "types": ["steam", "egs", "<b>"] }, { "id": "6", "name": "string id" }, { "id": 7 } ] }""";
        var host = new Host(("storesearch", store), ("autocomplete", sgdb), ("/grids/game/5", Grid));
        var resolver = new ArtworkResolver(new HttpClient(host), TempCache(), "key");

        var result = await resolver.SearchAsync("Mortal Kombat 1");

        Assert.Null(result.Note);
        Assert.True(result.HasKey);
        Assert.Equal(["steam:1971870", "sgdb:5", "steam:2"], result.Hits.Select(h => h.Match));
        Assert.True(result.Hits[0].Best);
        Assert.Null(result.Hits[0].Thumb); // not an artwork host
        var sgdbHit = result.Hits[1];
        Assert.Equal("2023", sgdbHit.Year);
        Assert.Equal(["Steam", "Epic"], sgdbHit.Platforms);
        Assert.Equal("https://cdn2.steamgriddb.com/thumb/x.png", sgdbHit.Thumb);
        Assert.Equal("https://shared.akamai.steamstatic.com/a.jpg", result.Hits[2].Thumb);
    }

    [Fact]
    public async Task The_search_asks_nothing_while_online_artwork_is_off_and_says_why_without_a_key()
    {
        var host = new Host();
        var off = new ArtworkResolver(new HttpClient(host), TempCache(), "key") { Online = false };
        var result = await off.SearchAsync("Hades");
        Assert.Empty(result.Hits);
        Assert.NotNull(result.Note);
        Assert.Empty(host.Requests);

        var noKey = new ArtworkResolver(new HttpClient(host), TempCache(), null);
        var steamOnly = await noKey.SearchAsync("Hades");
        Assert.False(steamOnly.HasKey);
        Assert.NotNull(steamOnly.Note);
        Assert.DoesNotContain(host.Requests, r => r.Contains("steamgriddb"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => noKey.SearchAsync("bad\u202Equery"));
    }

    [Theory]
    [InlineData("steam:1971870", true)]
    [InlineData("sgdb:5321", true)]
    [InlineData(" sgdb:5321 ", true)]
    [InlineData("steam:0", false)]
    [InlineData("sgdb:01", false)]
    [InlineData("sgdb:99999999999", false)]
    [InlineData("steam:12a", false)]
    [InlineData("gog:1", false)]
    [InlineData("https://evil", false)]
    [InlineData("none", false)]
    public void Only_the_two_pin_forms_are_taken(string match, bool ok)
    {
        if (ok) Assert.Equal(match.Trim(), ArtworkMatches.Validate(match));
        else Assert.Throws<ArgumentException>(() => ArtworkMatches.Validate(match));
        Assert.Null(ArtworkMatches.Validate(null));
    }

    [Fact]
    public void The_search_answer_is_read_defensively()
    {
        using var doc = JsonDocument.Parse("""
            { "data": [
              { "id": 1, "name": "Ok", "release_date": -5, "types": "steam" },
              { "id": 0, "name": "zero" },
              { "id": 2.5, "name": "fraction" },
              { "id": 3, "name": "   " },
              { "id": 4, "name": "Bidi\u202Ename" },
              "nonsense"
            ] }
            """);

        var games = ArtworkMatches.ParseSgdbSearch(doc.RootElement);

        Assert.Equal(["1", "4"], games.Select(g => g.Id));
        Assert.Null(games[0].Year);
        Assert.Empty(games[0].Platforms);
        Assert.Equal("Bidiname", games[1].Name);
    }

    [Fact]
    public async Task The_pin_is_stored_per_entry_survives_a_rescan_drops_online_picks_and_goes_back_to_automatic()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-artsearch-").FullName;
        var config = new ConfigStore(dir);
        var resolver = new ArtworkResolver(new HttpClient(new Host()), Path.Combine(dir, "cache.json"), null) { Online = false };
        resolver.ArtworkMatchOf = id => config.Current.Overrides.GetValueOrDefault(id)?.ArtworkMatch;
        var service = new LibraryService(config, resolver, [new Provider(Game("folder:mk1", "Mortal Kombat 1"), Game("steam:620", "Portal 2"))]);
        await service.ScanAsync();
        config.Update(c => c.Overrides["folder:mk1"] = new ItemOverride { Name = "Mortal Combat 1", Cover = "https://cdn2.steamgriddb.com/grid/wrong.png", Hero = "file:///C:/mine/hero.png" });

        service.SetArtworkMatch("folder:mk1", "steam:1971870", "Mortal Kombat 1\u0007");

        var stored = new ConfigStore(dir).Current.Overrides["folder:mk1"];
        Assert.Equal("steam:1971870", stored.ArtworkMatch);
        Assert.Equal("Mortal Kombat 1", stored.ArtworkMatchName);
        Assert.Null(stored.Cover); // the wrong game's picture goes
        Assert.Equal("file:///C:/mine/hero.png", stored.Hero); // an own picture stays
        Assert.Equal("Mortal Combat 1", stored.Name); // the rename is not touched
        Assert.Null(stored.StoreMatch); // nor the store match (age rating)
        await service.ScanAsync();
        Assert.Equal(new ArtworkMatchView("pinned", "steam:1971870", "Mortal Kombat 1"), service.GetArtworkMatch("folder:mk1"));

        Assert.Throws<ArgumentException>(() => service.SetArtworkMatch("folder:mk1", "javascript:alert(1)", null));
        Assert.Throws<ArgumentException>(() => service.SetArtworkMatch("steam:620", "sgdb:5", null));
        Assert.Throws<KeyNotFoundException>(() => service.SetArtworkMatch("folder:gone", "sgdb:5", null));

        service.SetArtworkMatch("folder:mk1", null, "ignored");
        var back = new ConfigStore(dir).Current.Overrides["folder:mk1"];
        Assert.Null(back.ArtworkMatch);
        Assert.Null(back.ArtworkMatchName);
        Assert.Equal("auto", service.GetArtworkMatch("folder:mk1").Mode);
    }

    [Fact]
    public async Task Online_picks_go_only_when_another_game_is_pinned_not_on_the_same_pin_or_on_unpinning()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-artsearch-").FullName;
        var config = new ConfigStore(dir);
        var resolver = new ArtworkResolver(new HttpClient(new Host()), Path.Combine(dir, "cache.json"), null) { Online = false };
        var service = new LibraryService(config, resolver, [new Provider(Game("folder:mk1", "Mortal Kombat 1"))]);
        await service.ScanAsync();
        const string Pick = "https://cdn2.steamgriddb.com/grid/mine.png";
        config.Update(c => c.Overrides["folder:mk1"] = new ItemOverride { Cover = Pick, ArtworkMatch = "sgdb:7" });

        service.SetArtworkMatch("folder:mk1", "sgdb:7", "Same"); // the same pin
        Assert.Equal(Pick, config.Current.Overrides["folder:mk1"].Cover);
        service.SetArtworkMatch("folder:mk1", null, null); // back to automatic
        Assert.Equal(Pick, config.Current.Overrides["folder:mk1"].Cover);
        service.SetArtworkMatch("folder:mk1", "sgdb:8", "Other"); // another game
        Assert.Null(config.Current.Overrides["folder:mk1"].Cover);
    }

    [Fact]
    public void A_pin_alone_keeps_the_override()
    {
        Assert.False(new ItemOverride { ArtworkMatch = "sgdb:1" }.IsEmpty);
        Assert.True(new ItemOverride().IsEmpty);
    }

    private sealed class Provider(params LibraryItem[] items) : ILibraryProvider
    {
        public string Name => "test";

        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<LibraryItem>>(items);
    }
}
