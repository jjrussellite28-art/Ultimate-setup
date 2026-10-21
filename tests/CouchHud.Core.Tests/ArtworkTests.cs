using System.Net;
using System.Text;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public class ArtworkTests
{
    private static string TempCache() =>
        Path.Combine(Directory.CreateTempSubdirectory("couchhud-art-").FullName, "cache.json");

    private static LibraryItem Item(string name, ItemType type = ItemType.Game) => new()
    {
        Id = $"test:{name}",
        Source = "test",
        Name = name,
        Type = type,
        Launch = new LaunchTarget(LaunchKind.Url, "https://example.com")
    };

    [Theory]
    [InlineData("DOOM™ Eternal: Deluxe", "doom eternal deluxe")]
    [InlineData("Ratchet & Clank", "ratchet and clank")]
    public void Normalize_ignores_trademarks_and_punctuation(string input, string expected) =>
        Assert.Equal(expected, ArtworkResolver.Normalize(input));

    [Fact]
    public async Task Steam_store_fallback_only_accepts_exact_matches()
    {
        var handler = new FakeHandler(
            ("storesearch/?term=Hades", """{ "items": [ { "id": 1145360, "name": "Hades" } ] }"""),
            ("storesearch/?term=Fortnite", """{ "items": [ { "id": 1, "name": "Fortnite Soundtrack" } ] }"""));
        var resolver = new ArtworkResolver(new HttpClient(handler), TempCache(), null);

        var result = await resolver.ResolveAsync([Item("Hades"), Item("Fortnite"), Item("YouTube", ItemType.App)]);

        Assert.NotNull(result);
        Assert.EndsWith("1145360/library_600x900.jpg", result[0].Art.Cover[0]);
        Assert.Empty(result[1].Art.Cover);
        // Apps aren't looked up without a SteamGridDB key.
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.ToString().Contains("YouTube"));
    }

    [Fact]
    public async Task SteamGridDb_is_used_first_when_a_key_is_set_and_results_are_cached()
    {
        var handler = new FakeHandler(
            ("autocomplete/Kodi", """{ "data": [ { "id": 42 } ] }"""),
            ("grids/game/42?dimensions=920x430", """{ "data": [ { "url": "https://cdn/grid.png" } ] }"""),
            ("heroes/game/42", """{ "data": [ { "url": "https://cdn/hero.png" } ] }"""),
            ("logos/game/42", """{ "data": [ { "url": "https://cdn/logo.png" } ] }"""));
        var cache = TempCache();
        var kodi = Item("Kodi", ItemType.App);

        var result = await new ArtworkResolver(new HttpClient(handler), cache, "k").ResolveAsync([kodi]);

        Assert.Equal(new[] { "https://cdn/grid.png" }, result![0].Art.Cover);
        Assert.Equal(new[] { "https://cdn/hero.png" }, result[0].Art.Hero);
        Assert.Equal(new[] { "https://cdn/logo.png" }, result[0].Art.Logo);
        Assert.Equal("Bearer k", handler.Requests[0].Headers.Authorization!.ToString());

        var offline = new ArtworkResolver(new HttpClient(new FakeHandler()), cache, "k");
        Assert.Equal(new[] { "https://cdn/grid.png" }, offline.ApplyCached([kodi])[0].Art.Cover);
    }

    [Fact]
    public async Task Items_with_art_are_left_alone_and_network_errors_are_not_cached()
    {
        var cache = TempCache();
        var withArt = Item("X") with { Art = new Artwork { Cover = ["a.jpg"] } };
        var resolver = new ArtworkResolver(new HttpClient(new FakeHandler(failAll: true)), cache, null);

        Assert.Null(await resolver.ResolveAsync([withArt, Item("Y")]));
        Assert.Equal("{}", File.ReadAllText(cache));
    }

    [Fact]
    public async Task Own_artwork_wins_and_only_missing_slots_are_filled()
    {
        var handler = new FakeHandler(
            ("autocomplete/Kodi", """{ "data": [ { "id": 42 } ] }"""),
            ("grids/game/42", """{ "data": [ { "url": "https://cdn/grid.png" } ] }"""),
            ("logos/game/42", """{ "data": [ { "url": "https://cdn/logo.png" } ] }"""));
        var mine = Item("Kodi", ItemType.App) with { Art = new Artwork { Cover = ["file:///C:/my/kodi.png"] } };

        var result = await new ArtworkResolver(new HttpClient(handler), TempCache(), "k").ResolveAsync([mine]);

        Assert.Equal(new[] { "file:///C:/my/kodi.png" }, result![0].Art.Cover);
        Assert.Equal(new[] { "https://cdn/logo.png" }, result[0].Art.Logo);
    }

    [Fact]
    public async Task Setting_a_key_retries_earlier_misses()
    {
        var cache = TempCache();
        // Without a key, an app is never looked up; a game miss gets cached.
        var steamMiss = new FakeHandler(("storesearch", """{ "items": [] }"""));
        var resolver = new ArtworkResolver(new HttpClient(steamMiss), cache, null);
        await resolver.ResolveAsync([Item("Obscure Game")]);
        Assert.Contains("obscure game", File.ReadAllText(cache));

        resolver.ApiKey = "new-key";
        await resolver.ResolveAsync([]);
        Assert.DoesNotContain("obscure game", File.ReadAllText(cache));
    }

    private sealed class FakeHandler(params (string UrlPart, string Json)[] routes) : HttpMessageHandler
    {
        private readonly bool _failAll;

        public FakeHandler(bool failAll) : this() => _failAll = failAll;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (_failAll) throw new HttpRequestException("offline");
            var url = request.RequestUri!.ToString();
            var match = routes.FirstOrDefault(r => url.Contains(r.UrlPart));
            var response = match.Json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(match.Json, Encoding.UTF8, "application/json") };
            return Task.FromResult(response);
        }
    }
}
