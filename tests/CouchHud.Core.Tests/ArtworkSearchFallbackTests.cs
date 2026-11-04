using System.Net;
using System.Text;
using CouchHud.Core.Library;

namespace CouchHud.Core.Tests;

/// <summary>
/// "Search artwork" for "Mortal Kombat 1": the Steam store search wants every word, so an entry named "Mortal Kombat 1 Premium Edition" (the search
/// starts with the entry's name) found nothing although the game is on Steam (app 1971870). Shorter searches are tried; no network here, the store's
/// answer for "Mortal Kombat 1" was recorded (Fixtures/steam-search-mortal-kombat-1.json).
/// </summary>
public class ArtworkSearchFallbackTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var d in _dirs)
        {
            try { Directory.Delete(d, true); } catch (IOException) { }
        }
    }

    private static readonly string Recorded = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "steam-search-mortal-kombat-1.json"));

    /// <summary>Answers like the store: a recorded answer only when the term is exactly "Mortal Kombat 1" (every word must match), else nothing.</summary>
    private sealed class Store : HttpMessageHandler
    {
        public List<string> Terms { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var term = Uri.UnescapeDataString(request.RequestUri!.Query.Split("term=")[1].Split('&')[0]);
            lock (Terms) Terms.Add(term);
            var json = term.Equals("Mortal Kombat 1", StringComparison.OrdinalIgnoreCase) ? Recorded : """{ "total": 0, "items": [] }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private ArtworkResolver Resolver(HttpMessageHandler store)
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-artfb-").FullName;
        _dirs.Add(dir);
        return new ArtworkResolver(new HttpClient(store), Path.Combine(dir, "cache.json"), "");
    }

    /// <summary>Answers every search with the same loosely related games.</summary>
    private sealed class Related : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "items": [ { "type": "app", "id": 976310, "name": "Mortal Kombat 11" }, { "type": "app", "id": 307780, "name": "Mortal Kombat X" } ] }""", Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public async Task The_plain_name_finds_the_game_first_and_asks_once()
    {
        var store = new Store();
        var result = await Resolver(store).SearchAsync("Mortal Kombat 1");

        Assert.Equal("steam:1971870", result.Hits[0].Match);
        Assert.True(result.Hits[0].Best);
        Assert.NotNull(result.Hits[0].Thumb);
        Assert.Equal(["Mortal Kombat 1"], store.Terms);
    }

    [Theory]
    [InlineData("Mortal Kombat 1 Premium Edition")]
    [InlineData("Mortal Kombat™ 1: Premium Edition")]
    [InlineData("Mortal Kombat 1 (Premium Edition)")]
    [InlineData("MORTAL KOMBAT 1 Game of the Year Edition")]
    public async Task A_name_with_an_edition_still_finds_the_game_and_it_is_the_automatic_pick(string name)
    {
        var store = new Store();
        var result = await Resolver(store).SearchAsync(name);

        Assert.Equal("steam:1971870", result.Hits[0].Match);
        Assert.True(result.Hits[0].Best);
        Assert.Equal("Mortal Kombat 1", store.Terms[^1], ignoreCase: true);
        Assert.True(store.Terms.Count <= 4);
    }

    [Fact]
    public async Task A_name_with_an_unknown_extra_word_falls_back_by_dropping_words_and_stays_bounded()
    {
        var store = new Store();
        var result = await Resolver(store).SearchAsync("Mortal Kombat 1 Kollection Extra");

        Assert.Contains(result.Hits, h => h.Match == "steam:1971870");
        Assert.True(store.Terms.Count <= 4);
    }

    [Fact]
    public async Task Nothing_found_is_not_searched_without_end()
    {
        var store = new Store();
        var result = await Resolver(store).SearchAsync("Zzzz Qqqq Wwww Xxxx Yyyy Vvvv");

        Assert.Empty(result.Hits);
        Assert.True(store.Terms.Count <= 4);
    }

    [Theory]
    [InlineData("Mortal Kombat 1 Premium Edition", "Mortal Kombat 1")]
    [InlineData("Mortal Kombat™ 1: Premium Edition", "Mortal Kombat 1")]
    [InlineData("Hades II Deluxe Edition GOTY", "Hades II")]
    [InlineData("Assassin's Creed Collector's Edition", "Assassin's Creed")]
    public void Fallbacks_peel_signs_punctuation_and_editions(string query, string expectedLast)
    {
        var steps = NameMatch.SearchFallbacks(query);

        Assert.Contains(expectedLast, steps);
        Assert.True(steps.Count <= 3);
        Assert.DoesNotContain(query, steps);
    }

    [Fact]
    public void A_short_name_gets_no_fallback_below_two_words()
    {
        Assert.Empty(NameMatch.SearchFallbacks("Doom"));
        Assert.DoesNotContain("Mortal", NameMatch.SearchFallbacks("Mortal Kombat"));
    }

    [Fact]
    public async Task Offline_nothing_is_sent()
    {
        var store = new Store();
        var resolver = Resolver(store);
        resolver.Online = false;

        var result = await resolver.SearchAsync("Mortal Kombat 1 Premium Edition");

        Assert.Empty(result.Hits);
        Assert.Empty(store.Terms);
    }

    [Fact]
    public void The_fallbacks_come_in_this_exact_order()
    {
        Assert.Equal(["Mortal Kombat 1", "Mortal Kombat"], NameMatch.SearchFallbacks("Mortal Kombat 1 Premium Edition"));
    }

    [Fact]
    public async Task A_loosely_related_fallback_hit_is_shown_but_never_the_automatic_pick()
    {
        var result = await Resolver(new Related()).SearchAsync("Mortal Kombat 1 Premium Edition");

        Assert.NotEmpty(result.Hits);
        Assert.DoesNotContain(result.Hits, h => h.Best);
    }

    [Fact]
    public void A_single_word_is_searched_only_after_edition_words_and_never_a_bare_number()
    {
        Assert.Equal(["Doom"], NameMatch.SearchFallbacks("Doom Deluxe Edition"));
        Assert.Empty(NameMatch.SearchFallbacks("2 Deluxe Edition"));
        Assert.Empty(NameMatch.SearchFallbacks("II Deluxe Edition"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("II")]
    [InlineData("F1 24")]
    public void Short_names_have_no_fallback(string name) => Assert.Empty(NameMatch.SearchFallbacks(name));

    [Fact]
    public void An_edition_name_with_a_number_falls_back_to_the_game()
    {
        Assert.Equal(["F1 24"], NameMatch.SearchFallbacks("F1 24 Deluxe Edition"));
    }
}
