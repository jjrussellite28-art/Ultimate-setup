using System.Net;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public class ArtworkPickerTests
{
    private static LibraryItem Item(string id, string name, Artwork? art = null, ItemType type = ItemType.Game) => new()
    {
        Id = id,
        Source = "test",
        Name = name,
        Type = type,
        Launch = new LaunchTarget(LaunchKind.Url, "https://example.com"),
        Art = art ?? Artwork.Empty
    };

    private static ItemOverride O(string? cover = null, string? hero = null, string? logo = null) => new() { Cover = cover, Hero = hero, Logo = logo };

    [Fact]
    public void Apply_puts_the_pick_first_and_keeps_the_rest_as_fallback()
    {
        var art = new Artwork { Cover = ["https://a/1.jpg", "https://a/2.jpg"], Hero = ["https://a/h.jpg"] };

        var result = ArtworkOverrides.Apply(art, O(cover: "https://a/2.jpg", logo: "https://my/logo.png"));

        Assert.Equal(new[] { "https://a/2.jpg", "https://a/1.jpg" }, result.Cover);
        Assert.Equal(new[] { "https://a/h.jpg" }, result.Hero);
        Assert.Equal(new[] { "https://my/logo.png" }, result.Logo);
    }

    [Theory]
    [InlineData("ftp://x/y.png")]
    [InlineData("http://insecure/y.png")]
    [InlineData(@"\\server\share\c.png")]
    [InlineData("//server/share/x.png")]
    [InlineData(@"\/server/x.png")]
    [InlineData(@"\\?\C:\x.png")]
    [InlineData(@"\\.\C:\x.png")]
    [InlineData("C:/Windows/notes.txt")]
    public void Normalize_refuses_unsafe_sources(string value) =>
        Assert.ThrowsAny<Exception>(() => ArtworkOverrides.Normalize(value));

    [Fact]
    public void Normalize_turns_local_images_into_file_urls_and_checks_they_exist()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-pick-").FullName;
        var png = Path.Combine(dir, "cover.png");
        File.WriteAllBytes(png, [1, 2, 3]);

        Assert.StartsWith("file:///", ArtworkOverrides.Normalize(png));
        Assert.Equal("https://x/y.png", ArtworkOverrides.Normalize(" https://x/y.png "));
        Assert.Throws<FileNotFoundException>(() => ArtworkOverrides.Normalize(Path.Combine(dir, "missing.png")));
    }

    [Fact]
    public void ParseSteamGridDb_reads_urls_and_thumbs_and_skips_non_https()
    {
        using var doc = JsonDocument.Parse("""
            { "data": [
              { "url": "https://cdn/a.png", "thumb": "https://cdn/a_t.png" },
              { "url": "https://cdn/b.png" },
              { "url": "http://cdn/c.png" },
              { "nourl": 1 } ] }
            """);

        var list = ArtworkResolver.ParseSteamGridDb(doc.RootElement);

        Assert.Equal(2, list.Count);
        Assert.Equal("https://cdn/a_t.png", list[0].Thumb);
        Assert.Equal("https://cdn/b.png", list[1].Thumb);
    }

    [Fact]
    public async Task Choices_without_a_key_offer_steam_variants_and_the_current_art()
    {
        var cache = Path.Combine(Directory.CreateTempSubdirectory("couchhud-pick-").FullName, "cache.json");
        var resolver = new ArtworkResolver(new HttpClient(new Fake()), cache, null);
        var item = Item("steam:620", "Portal 2", new Artwork { Cover = ["file:///C:/steam/cover.jpg"] });

        var choices = await resolver.GetChoicesAsync(item);

        Assert.False(choices.HasKey);
        Assert.Equal("Aktuell", choices.Cover[0].Source);
        Assert.Contains(choices.Cover, c => c.Url.EndsWith("620/header.jpg"));
        Assert.Single(choices.Logo);
    }

    [Fact]
    public async Task Choices_with_a_key_list_every_steamgriddb_hit()
    {
        var cache = Path.Combine(Directory.CreateTempSubdirectory("couchhud-pick-").FullName, "cache.json");
        var fake = new Fake(
            ("grids/game/7", """{ "data": [ { "url": "https://g/1.png" }, { "url": "https://g/2.png" } ] }"""),
            ("heroes/game/7", """{ "data": [ { "url": "https://g/h.png" } ] }"""),
            ("autocomplete/Kodi", """{ "data": [ { "id": 7 } ] }"""));
        var resolver = new ArtworkResolver(new HttpClient(fake), cache, "key");

        var choices = await resolver.GetChoicesAsync(Item("custom:kodi", "Kodi", type: ItemType.App));

        Assert.True(choices.HasKey);
        Assert.Equal(2, choices.Cover.Count);
        Assert.Single(choices.Hero);
        Assert.Empty(choices.Logo);
        Assert.Null(choices.Note);
    }

    [Fact]
    public async Task Choices_report_a_rejected_key()
    {
        var cache = Path.Combine(Directory.CreateTempSubdirectory("couchhud-pick-").FullName, "cache.json");
        var resolver = new ArtworkResolver(new HttpClient(new Fake(unauthorized: true)), cache, "bad");

        var choices = await resolver.GetChoicesAsync(Item("custom:kodi", "Kodi", type: ItemType.App));

        Assert.Contains("Key", choices.Note);
    }

    [Fact]
    public async Task The_pick_survives_a_rescan_and_can_be_reset()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-pick-").FullName;
        var config = new ConfigStore(dir);
        var cache = Path.Combine(dir, "cache.json");
        var provider = new Provider(Item("steam:1", "Hades", new Artwork { Cover = ["https://auto/c.jpg"] }));
        var service = new LibraryService(config, new ArtworkResolver(new HttpClient(new Fake()), cache, null), [provider]);
        await service.ScanAsync();

        var picked = service.SetArtwork("steam:1", "cover", "https://my/c.png");
        Assert.Equal(new[] { "https://my/c.png", "https://auto/c.jpg" }, picked.Art.Cover);

        var rescanned = await service.ScanAsync();
        Assert.Equal("https://my/c.png", rescanned[0].Art.Cover[0]);
        Assert.Equal("https://my/c.png", new ConfigStore(dir).Current.Overrides["steam:1"].Cover);

        var reset = service.SetArtwork("steam:1", "cover", null);
        Assert.Equal(new[] { "https://auto/c.jpg" }, reset.Art.Cover);
        Assert.Empty(new ConfigStore(dir).Current.Overrides);
    }

    [Fact]
    public async Task Picking_the_automatic_image_and_resetting_keeps_the_image()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-pick-").FullName;
        var config = new ConfigStore(dir);
        var provider = new Provider(Item("steam:1", "Hades", new Artwork { Cover = ["https://auto/c.jpg"] }));
        var service = new LibraryService(config, new ArtworkResolver(new HttpClient(new Fake()), Path.Combine(dir, "cache.json"), null), [provider]);
        await service.ScanAsync();

        service.SetArtwork("steam:1", "cover", "https://auto/c.jpg");
        var reset = service.SetArtwork("steam:1", "cover", null);

        Assert.Equal(new[] { "https://auto/c.jpg" }, reset.Art.Cover);
        Assert.Empty(new ConfigStore(dir).Current.Overrides);
    }

    [Fact]
    public async Task A_pick_stays_on_top_of_what_the_lookup_finds_and_reset_brings_the_automatic_image_back()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-pick-").FullName;
        var config = new ConfigStore(dir);
        var fake = new Fake(("storesearch", """{ "items": [ { "id": 5, "name": "Hades" } ] }"""));
        var service = new LibraryService(config, new ArtworkResolver(new HttpClient(fake), Path.Combine(dir, "cache.json"), null),
            [new Provider(Item("epic:hades", "Hades"))]);
        await service.ScanAsync();
        service.SetArtwork("epic:hades", "cover", "https://my/c.png");

        await service.ScanAsync();
        await Task.Delay(400);

        var item = service.Find("epic:hades")!;
        Assert.Equal("https://my/c.png", item.Art.Cover[0]);
        Assert.Contains(item.Art.Cover, c => c.EndsWith("5/library_600x900.jpg"));
        Assert.NotEmpty(item.Art.Hero);
        Assert.EndsWith("5/library_600x900.jpg", service.SetArtwork("epic:hades", "cover", null).Art.Cover[0]);
    }

    private sealed class Provider(params LibraryItem[] items) : ILibraryProvider
    {
        public string Name => "test";

        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<LibraryItem>>(items);
    }

    private sealed class Fake(params (string UrlPart, string Json)[] routes) : HttpMessageHandler
    {
        private readonly bool _unauthorized;

        public Fake(bool unauthorized) : this() => _unauthorized = unauthorized;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (_unauthorized) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var url = request.RequestUri!.ToString();
            var match = routes.FirstOrDefault(r => url.Contains(r.UrlPart));
            var empty = url.Contains("storesearch") ? """{ "items": [] }""" : null;
            var json = match.Json ?? empty;
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}

public class CollectionTests
{
    [Fact]
    public void Create_trims_and_rejects_empty_and_duplicate_names()
    {
        var all = new List<ItemCollection>();

        var c = Collections.Create(all, "  Couch Koop ");

        Assert.Equal("Couch Koop", c.Name);
        Assert.False(string.IsNullOrEmpty(c.Id));
        Assert.Throws<ArgumentException>(() => Collections.Create(all, "   "));
        Assert.Throws<ArgumentException>(() => Collections.Create(all, "couch koop"));
    }

    [Fact]
    public void Names_are_limited_in_length()
    {
        var all = new List<ItemCollection>();

        Assert.Equal(Collections.MaxNameLength, Collections.Create(all, new string('x', 100)).Name.Length);
    }

    [Fact]
    public void Rename_may_keep_its_own_name_but_not_take_another()
    {
        var all = new List<ItemCollection>();
        var a = Collections.Create(all, "A");
        Collections.Create(all, "B");

        Collections.Rename(all, a.Id, "a");
        Assert.Equal("a", a.Name);
        Assert.Throws<ArgumentException>(() => Collections.Rename(all, a.Id, "B"));
    }

    [Fact]
    public void Members_are_added_once_and_removed()
    {
        var all = new List<ItemCollection>();
        var c = Collections.Create(all, "Kinder");

        Collections.SetMember(all, c.Id, "steam:1", true);
        Collections.SetMember(all, c.Id, "steam:1", true);
        Collections.SetMember(all, c.Id, "steam:2", true);
        Collections.SetMember(all, c.Id, "steam:1", false);

        Assert.Equal(new[] { "steam:2" }, c.Items);
    }

    [Fact]
    public void Delete_and_forget_clean_up()
    {
        var all = new List<ItemCollection>();
        var a = Collections.Create(all, "A");
        var b = Collections.Create(all, "B");
        Collections.SetMember(all, a.Id, "custom:x", true);
        Collections.SetMember(all, b.Id, "custom:x", true);

        Collections.Forget(all, "custom:x");
        Collections.Delete(all, a.Id);

        Assert.Empty(b.Items);
        Assert.Single(all);
        Assert.Throws<KeyNotFoundException>(() => Collections.Delete(all, "nope"));
    }

    [Fact]
    public void Collections_survive_a_restart()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-col-").FullName;
        var store = new ConfigStore(dir);
        store.Update(c =>
        {
            var col = Collections.Create(c.Collections, "Koop");
            Collections.SetMember(c.Collections, col.Id, "steam:9", true);
            Collections.SetOnHome(c.Collections, col.Id, true);
        });

        var loaded = new ConfigStore(dir).Current.Collections.Single();

        Assert.Equal("Koop", loaded.Name);
        Assert.Equal(new[] { "steam:9" }, loaded.Items);
        Assert.True(loaded.OnHome);
    }
}
