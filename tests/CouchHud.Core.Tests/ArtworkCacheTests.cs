using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CouchHud.Core.Library;
using CouchHud.Core.Net;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public class ArtworkCacheTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("couchhud-img-").FullName;

    /// <summary>A complete 1x1 png.</summary>
    internal static byte[] Png(int extra = 0)
    {
        var bytes = new List<byte>(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="));
        // Padding inside the stream isn't valid png, but the sniffer only looks at signature and end: use a text chunk instead.
        if (extra > 0)
        {
            var iend = bytes.GetRange(bytes.Count - 12, 12);
            bytes.RemoveRange(bytes.Count - 12, 12);
            bytes.AddRange(new byte[extra]);
            bytes.AddRange(iend);
        }
        return bytes.ToArray();
    }

    private static byte[] Jpeg() => JpegOfSize(1, 1);

    internal static byte[] JpegOfSize(int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
        0xFF, 0xC0, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
        0xFF, 0xD9
    ];

    [Fact]
    public void Complete_images_are_stored_truncated_ones_are_refused()
    {
        var cache = new ArtworkCache(TempDir());
        Assert.True(cache.Store("https://x/a.png", Png(), "image/png"));
        Assert.True(cache.Store("https://x/b.jpg", Jpeg(), "image/jpeg"));
        Assert.True(cache.Has("https://x/a.png"));

        var half = Png()[..(Png().Length - 5)];
        Assert.False(cache.Store("https://x/c.png", half, "image/png"));
        Assert.False(cache.Store("https://x/d.jpg", Jpeg()[..30], "image/jpeg"));
        Assert.False(cache.Has("https://x/c.png"));
    }

    [Fact]
    public void Only_bitmap_formats_with_matching_content_type_are_accepted()
    {
        var cache = new ArtworkCache(TempDir());
        var svg = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");
        Assert.False(cache.Store("https://x/s.svg", svg, "image/svg+xml"));
        Assert.False(cache.Store("https://x/h", Encoding.UTF8.GetBytes("<html>not an image at all</html>"), "image/png"));
        // The bytes decide, not the server's claim: a png is a png whatever the header said.
        Assert.True(cache.Store("https://x/p.png", Png(), "text/html"));
    }

    [Fact]
    public void Oversized_images_are_refused()
    {
        var cache = new ArtworkCache(TempDir());
        Assert.False(cache.Store("https://x/big.png", Png((int)ArtworkCache.MaxImageBytes), "image/png"));
    }

    [Fact]
    public void Writing_leaves_no_temp_files_and_old_leftovers_are_removed_on_start()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "abc.123.tmp"), "half");
        var cache = new ArtworkCache(dir);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

        cache.Store("https://x/a.png", Png(), "image/png");
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.Single(Directory.GetFiles(dir, "*.png"));

        // A new process finds what the old one stored.
        Assert.True(new ArtworkCache(dir).Has("https://x/a.png"));
    }

    [Theory]
    [InlineData("/../secret.png")]
    [InlineData("/..%2f..%2fsecret")]
    [InlineData("/C:/Windows/win.ini")]
    [InlineData("/")]
    [InlineData("/abc")]
    [InlineData("/0000000000000000000000000000000000000000000000000000000000000000.png")]
    [InlineData("/0000000000000000000000000000000000000000000000000000000000000000/../x")]
    public void The_virtual_host_only_resolves_exact_hashes_of_cached_images(string request)
    {
        var cache = new ArtworkCache(TempDir());
        cache.Store("https://x/a.png", Png(), "image/png");
        Assert.Null(cache.ResolveRequest(request));
        Assert.Null(cache.Read(request));
    }

    [Fact]
    public void Cached_images_are_served_with_the_type_found_in_their_bytes()
    {
        var cache = new ArtworkCache(TempDir());
        cache.Store("https://x/a.png", Png(), "image/png");
        var hash = ArtworkCache.HashOf("https://x/a.png");
        var served = cache.Read("/" + hash);
        Assert.NotNull(served);
        Assert.Equal("image/png", served!.Value.ContentType);
        Assert.EndsWith(hash, ArtworkCache.UrlFor("https://x/a.png"));
    }

    [Fact]
    public void A_file_damaged_afterwards_is_not_served_and_is_forgotten()
    {
        var dir = TempDir();
        var cache = new ArtworkCache(dir);
        cache.Store("https://x/a.png", Png(), "image/png");
        var path = cache.PathOf("https://x/a.png")!;
        File.WriteAllBytes(path, Png()[..20]);

        Assert.Null(cache.Read("/" + ArtworkCache.HashOf("https://x/a.png")));
        Assert.False(cache.Has("https://x/a.png"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Pruning_removes_the_least_recently_used_but_never_images_in_use()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var size = Png(1000).Length;
        var cache = new ArtworkCache(TempDir(), maxBytes: size * 4, utcNow: () => now);
        foreach (var n in new[] { "a", "b", "c", "d", "e" })
        {
            now = now.AddMinutes(1);
            Assert.True(cache.Store($"https://x/{n}.png", Png(1000), "image/png"));
        }
        // "a" is the oldest but was just used; "b" is the oldest after that but in use by an installed entry.
        now = now.AddMinutes(1);
        Assert.True(cache.Has("https://x/a.png"));

        var freed = cache.Prune(["https://x/b.png"]);

        Assert.True(freed > 0);
        Assert.True(cache.TotalBytes <= size * 4);
        Assert.True(cache.Has("https://x/a.png"));
        Assert.True(cache.Has("https://x/b.png"));
        Assert.False(cache.Has("https://x/c.png"));
        Assert.True(cache.Has("https://x/e.png"));
    }

    [Fact]
    public void Pruning_below_the_limit_does_nothing_and_in_use_images_survive_even_over_the_limit()
    {
        var cache = new ArtworkCache(TempDir(), maxBytes: 10);
        cache.Store("https://x/a.png", Png(), "image/png");
        Assert.Equal(0, cache.Prune(["https://x/a.png"]));
        Assert.True(cache.Has("https://x/a.png"));
    }

    [Fact]
    public void Failures_expire_and_clear_empties_everything()
    {
        var dir = TempDir();
        var cache = new ArtworkCache(dir);
        cache.Store("https://x/a.png", Png(), "image/png");
        cache.MarkFailed("https://x/gone.png");
        Assert.True(cache.RecentlyFailed("https://x/gone.png"));
        Assert.True(new ArtworkCache(dir).RecentlyFailed("https://x/gone.png"));

        cache.Clear();
        Assert.False(cache.Has("https://x/a.png"));
        Assert.False(cache.RecentlyFailed("https://x/gone.png"));
        Assert.Empty(Directory.GetFiles(dir));
    }

    // ----- Downloading through the resolver -----

    private sealed class ImageHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Func<string, HttpResponseMessage?>? Custom { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var response = Custom?.Invoke(url) ?? (url.Contains("missing")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png()) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } } });
            return Task.FromResult(response);
        }
    }

    private static ArtworkResolver Resolver(ImageHandler handler, ArtworkCache cache) =>
        new(new HttpClient(handler), Path.Combine(TempDir(), "art.json"), null, cache);

    [Fact]
    public async Task Images_are_downloaded_once_and_never_again()
    {
        var handler = new ImageHandler();
        var cache = new ArtworkCache(TempDir());
        var resolver = Resolver(handler, cache);
        string[][] slots = [["https://cdn.steamstatic.com/a.jpg"], ["https://cdn.steamstatic.com/b.jpg"]];

        Assert.Equal(2, await resolver.DownloadMissingAsync(slots));
        Assert.Equal(0, await resolver.DownloadMissingAsync(slots));
        // A restart finds them on disk too.
        Assert.Equal(0, await Resolver(handler, new ArtworkCache(cache.Root)).DownloadMissingAsync(slots));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_missing_image_is_remembered_and_the_next_candidate_is_tried()
    {
        var handler = new ImageHandler();
        var cache = new ArtworkCache(TempDir());
        var resolver = Resolver(handler, cache);
        string[][] slots = [["https://cdn.steamstatic.com/missing.jpg", "https://cdn.steamstatic.com/ok.jpg"]];

        Assert.Equal(1, await resolver.DownloadMissingAsync(slots));
        Assert.Equal(0, await resolver.DownloadMissingAsync(slots));
        Assert.Equal(2, handler.Requests.Count); // the 404 is not asked again
        Assert.True(cache.Has("https://cdn.steamstatic.com/ok.jpg"));
    }

    [Fact]
    public async Task Local_files_come_first_and_are_used_as_they_are()
    {
        var handler = new ImageHandler();
        var resolver = Resolver(handler, new ArtworkCache(TempDir()));
        Assert.Equal(0, await resolver.DownloadMissingAsync([["file:///C:/steam/library/1_library_600x900.jpg", "https://cdn.steamstatic.com/a.jpg"]]));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Html_and_oversized_answers_are_not_stored()
    {
        var handler = new ImageHandler
        {
            Custom = url => url.Contains("html")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>", Encoding.UTF8, "text/html") }
                : url.Contains("lying")
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>", Encoding.UTF8, "image/png") }
                    : null
        };
        var cache = new ArtworkCache(TempDir());
        var resolver = Resolver(handler, cache);

        Assert.Equal(0, await resolver.DownloadMissingAsync([["https://cdn.steamstatic.com/html"], ["https://cdn.steamstatic.com/lying"]]));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Nothing_is_downloaded_when_online_artwork_is_off_but_a_picked_image_is()
    {
        var handler = new ImageHandler();
        var cache = new ArtworkCache(TempDir());
        var resolver = Resolver(handler, cache);
        resolver.Online = false;

        Assert.Equal(0, await resolver.DownloadMissingAsync([["https://cdn.steamstatic.com/a.jpg"]]));
        Assert.Empty(handler.Requests);
        Assert.True(await resolver.CacheNowAsync("https://cdn.steamstatic.com/picked.jpg"));
        Assert.True(cache.Has("https://cdn.steamstatic.com/picked.jpg"));
    }

    [Fact]
    public async Task Background_work_waits_while_a_game_runs()
    {
        var handler = new ImageHandler();
        var resolver = Resolver(handler, new ArtworkCache(TempDir()));
        var gameRuns = true;
        var waits = 0;
        resolver.BackgroundAllowed = () => !gameRuns;
        resolver.BackgroundWait = _ =>
        {
            if (++waits == 3) gameRuns = false;
            return Task.CompletedTask;
        };

        await resolver.DownloadMissingAsync([["https://cdn.steamstatic.com/a.jpg"]]);

        Assert.Equal(3, waits);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Repeated_network_errors_end_the_pass()
    {
        var handler = new ImageHandler { Custom = _ => throw new HttpRequestException("offline") };
        var resolver = Resolver(handler, new ArtworkCache(TempDir()));
        string[][] slots = Enumerable.Range(0, 10).Select(i => new[] { $"https://cdn.steamstatic.com/{i}.jpg" }).ToArray();

        Assert.Equal(0, await resolver.DownloadMissingAsync(slots));
        Assert.Equal(3, handler.Requests.Count);
    }

    // ----- Fallback order -----

    private static LibraryItem App(string name, string? exe = null) => new()
    {
        Id = "custom:" + name,
        Source = "custom",
        Name = name,
        Type = ItemType.App,
        Launch = exe is null ? new LaunchTarget(LaunchKind.Url, "https://example.com") : new LaunchTarget(LaunchKind.Exe, exe)
    };

    private sealed class Routes(params (string Part, string Json)[] routes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var match = routes.FirstOrDefault(r => url.Contains(r.Part));
            return Task.FromResult(match.Json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(match.Json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Launcher_art_means_no_lookup_at_all()
    {
        var routes = new Routes();
        var game = App("Epic Thing") with
        {
            Type = ItemType.Game,
            Art = new Artwork { Cover = ["https://cdn1.epicgames.com/c.jpg"], Hero = ["https://cdn1.epicgames.com/h.jpg"] }
        };
        var resolver = new ArtworkResolver(new HttpClient(routes), Path.Combine(TempDir(), "a.json"), "key");

        var result = await resolver.ResolveAsync([game with { Art = game.Art with { Logo = ["https://cdn1.epicgames.com/l.png"] } }]);

        Assert.Null(result);
        Assert.Empty(routes.Requests);
    }

    [Fact]
    public async Task SteamGridDb_comes_before_the_Steam_store_and_the_exe_icon_comes_last()
    {
        var routes = new Routes(
            ("autocomplete/Tool", """{ "data": [ { "id": 7 } ] }"""),
            ("grids/game/7", """{ "data": [ { "url": "https://cdn.steamstatic.com/grid.png" } ] }"""));
        var cache = new ArtworkCache(TempDir());
        var exe = Path.Combine(TempDir(), "tool.exe");
        File.WriteAllText(exe, "x");
        var resolver = new ArtworkResolver(new HttpClient(routes), Path.Combine(TempDir(), "a.json"), "key", cache)
        {
            ExeIcons = _ => Png()
        };

        var result = await resolver.ResolveAsync([App("Tool", exe), App("Plain", exe)]);

        Assert.NotNull(result);
        Assert.Equal(["https://cdn.steamstatic.com/grid.png"], result[0].Art.Cover);
        // The icon is only the last resort: "Tool" has a cover, "Plain" (nothing found) gets the icon.
        Assert.Empty(result[0].Art.Icon);
        Assert.Single(result[1].Art.Icon);
        Assert.Empty(result[1].Art.Cover);
        Assert.DoesNotContain(routes.Requests, r => r.Contains("storesearch"));

        // And from disk afterwards, without the icon renderer.
        var again = new ArtworkResolver(new HttpClient(new Routes()), Path.Combine(TempDir(), "b.json"), null, cache);
        Assert.Single(again.ApplyCached([App("Plain", exe)])[0].Art.Icon);
    }

    [Fact]
    public async Task A_game_with_no_source_keeps_empty_art_for_the_generated_banner()
    {
        var routes = new Routes(("storesearch", """{ "items": [] }"""));
        var resolver = new ArtworkResolver(new HttpClient(routes), Path.Combine(TempDir(), "a.json"), null, new ArtworkCache(TempDir()));
        var game = App("Unknown Indie") with { Type = ItemType.Game };

        Assert.Null(await resolver.ResolveAsync([game]));
        // The miss is remembered: the next pass doesn't ask again.
        Assert.Null(await resolver.ResolveAsync([game]));
        Assert.Single(routes.Requests);
    }

    [Fact]
    public async Task Switching_online_artwork_off_stops_all_requests()
    {
        var routes = new Routes(("storesearch", """{ "items": [ { "id": 1, "name": "Hades", "type": "app" } ] }"""));
        var resolver = new ArtworkResolver(new HttpClient(routes), Path.Combine(TempDir(), "a.json"), null) { Online = false };
        Assert.Null(await resolver.ResolveAsync([App("Hades") with { Type = ItemType.Game }]));
        Assert.Empty(routes.Requests);
    }

    [Fact]
    public async Task The_same_name_is_searched_once_for_art_and_store_info()
    {
        var routes = new Routes(("storesearch", """{ "items": [ { "id": 1145360, "name": "Hades", "type": "app" } ] }"""));
        var http = new HttpClient(routes);
        var file = Path.Combine(TempDir(), "ids.json");
        var ids = new SteamAppIds(http, file);
        var resolver = new ArtworkResolver(http, Path.Combine(TempDir(), "a.json"), null, null, ids);

        await resolver.ResolveAsync([App("Hades") with { Type = ItemType.Game }]);
        Assert.Equal("1145360", await ids.FindAsync("HADES", default));
        // Even a new process (or the store info service) finds the answer on disk.
        Assert.Equal("1145360", await new SteamAppIds(http, file).FindAsync("Hades", default));
        Assert.Single(routes.Requests, r => r.Contains("storesearch"));
    }

    // ----- Names -----

    [Theory]
    [InlineData("Cyberpunk 2077: Game of the Year Edition", "Cyberpunk 2077")]
    [InlineData("Resident Evil II", "Resident Evil 2")]
    [InlineData("The Witcher 3: Wild Hunt - GOTY", "Witcher 3 Wild Hunt")]
    [InlineData("DOOM™ (2016)", "Doom")]
    [InlineData("Pokémon Quest", "Pokemon Quest")]
    [InlineData("Borderlands 3 Super Deluxe Edition", "Borderlands 3 Super")]
    [InlineData("Ratchet & Clank", "Ratchet and Clank")]
    [InlineData("Divinity: Original Sin 2 - Definitive Edition", "Divinity Original Sin 2")]
    public void Names_that_differ_only_by_edition_year_or_numerals_match(string a, string b) =>
        Assert.True(NameMatch.Same(a, b), $"{NameMatch.Canonical(a)} / {NameMatch.Canonical(b)}");

    [Theory]
    [InlineData("Resident Evil 2", "Resident Evil 3")]
    [InlineData("Doom", "Doom Eternal")]
    [InlineData("Fallout", "Fallout 4")]
    [InlineData("Civilization VI", "Civilization V")]
    [InlineData("Final Fantasy", "Final Fantasy VII")]
    [InlineData("Hades", "Hades II")]
    [InlineData("Dark Souls", "Dark Souls Remastered")]
    [InlineData("X", "")]
    public void Different_games_never_match(string a, string b) => Assert.False(NameMatch.Same(a, b));

    [Fact]
    public async Task The_store_search_skips_dlc_and_accepts_an_edition_variant()
    {
        var routes = new Routes(("storesearch", """{ "items": [ { "id": 5, "name": "Hades", "type": "dlc" }, { "id": 6, "name": "Hades: Definitive Edition", "type": "app" } ] }"""));
        Assert.Equal("6", await SteamStore.FindAppIdAsync(new HttpClient(routes), "Hades", default));
    }

    // ----- Launcher art -----

    [Fact]
    public void Epic_catalog_cache_gives_box_art_without_any_request()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "catcache.bin");
        const string json = """
            [ { "id": "abc", "title": "T", "keyImages": [
                { "type": "DieselGameBoxTall", "url": "https://cdn1.epicgames.com/tall.jpg" },
                { "type": "DieselGameBox", "url": "https://cdn1.epicgames.com/wide.jpg" },
                { "type": "DieselGameBoxLogo", "url": "https://cdn1.epicgames.com/logo.png" },
                { "type": "Screenshot", "url": "http://insecure/x.jpg" } ] },
              { "id": "nothing", "keyImages": [ { "type": "Thumbnail", "url": "http://insecure/x.jpg" } ] } ]
            """;
        File.WriteAllText(file, Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));

        var catalog = LauncherArtwork.ReadEpicCatalog(file);

        Assert.Equal(["https://cdn1.epicgames.com/tall.jpg"], catalog["abc"].Cover);
        Assert.Equal(["https://cdn1.epicgames.com/wide.jpg"], catalog["abc"].Hero);
        Assert.Equal(["https://cdn1.epicgames.com/logo.png"], catalog["abc"].Logo);
        Assert.False(catalog.ContainsKey("nothing"));
        Assert.Empty(LauncherArtwork.ReadEpicCatalog(Path.Combine(dir, "missing.bin")));
        File.WriteAllText(file, "%%% not base64 %%%");
        Assert.Empty(LauncherArtwork.ReadEpicCatalog(file));
    }

    [Fact]
    public async Task Epic_items_pick_up_their_catalog_art()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "x.item"),
            """{ "DisplayName": "Game", "AppName": "App1", "CatalogItemId": "abc", "CatalogNamespace": "ns", "InstallLocation": "C:\\G", "AppCategories": ["games"] }""");
        var catalog = Path.Combine(dir, "catcache.bin");
        File.WriteAllText(catalog, Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """[ { "id": "abc", "keyImages": [ { "type": "DieselGameBoxTall", "url": "https://cdn1.epicgames.com/tall.jpg" } ] } ]""")));

        var items = await new EpicScanner(dir, catalog).GetItemsAsync(default);

        Assert.Equal(["https://cdn1.epicgames.com/tall.jpg"], items.Single().Art.Cover);
    }

    [Fact]
    public void Package_logos_come_from_the_manifest_and_stay_inside_the_package()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "Assets"));
        File.WriteAllBytes(Path.Combine(dir, "Assets", "Wide.scale-100.png"), Png());
        File.WriteAllBytes(Path.Combine(dir, "Assets", "Wide.scale-200.png"), Png(500));
        File.WriteAllBytes(Path.Combine(dir, "Assets", "Square.png"), Png());
        File.WriteAllText(Path.Combine(dir, "AppxManifest.xml"), """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Applications><Application><uap:VisualElements Wide310x150Logo="Assets\Wide.png" Square150x150Logo="Assets\Square.png" Square310x310Logo="..\..\outside.png" /></Application></Applications>
            </Package>
            """);

        var art = LauncherArtwork.FromPackage(dir);

        Assert.EndsWith("Wide.scale-200.png", art.Hero.Single());
        Assert.EndsWith("Square.png", art.Cover.Single()); // the 310 logo points outside the package and is ignored
        Assert.EndsWith("Square.png", art.Icon.Single());
        Assert.Equal(Artwork.Empty.Cover, LauncherArtwork.FromPackage(Path.Combine(dir, "nothing")).Cover);
    }

    // ----- Polite requests -----

    private sealed class FakeNet
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = [];
        public List<(DateTimeOffset At, HttpRequestMessage Request)> Calls { get; } = [];
        public Queue<Func<HttpResponseMessage>> Answers { get; } = new();
        public Func<HttpResponseMessage> Default { get; set; } = () => new HttpResponseMessage(HttpStatusCode.OK);

        public HttpClient Client(Func<string, HostPolicy>? policy = null) =>
            new(new PoliteHandler(new Inner(this), "9.9.9", policy, () => Now, (d, _) =>
            {
                Delays.Add(d);
                Now += d;
                return Task.CompletedTask;
            }));

        private sealed class Inner(FakeNet net) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                net.Calls.Add((net.Now, request));
                return Task.FromResult(net.Answers.Count > 0 ? net.Answers.Dequeue()() : net.Default());
            }
        }
    }

    private static HttpResponseMessage Status(HttpStatusCode code, TimeSpan? retryAfter = null)
    {
        var r = new HttpResponseMessage(code);
        if (retryAfter is { } d) r.Headers.RetryAfter = new RetryConditionHeaderValue(d);
        return r;
    }

    [Fact]
    public async Task Requests_carry_the_couchhud_user_agent_and_keep_a_minimum_gap()
    {
        var net = new FakeNet();
        var client = net.Client(_ => new HostPolicy(1, TimeSpan.FromSeconds(2)));

        for (var i = 0; i < 3; i++) (await client.GetAsync("https://store.example/a")).Dispose();

        Assert.StartsWith("CouchHUD/9.9.9", net.Calls[0].Request.Headers.UserAgent.ToString());
        Assert.Equal(TimeSpan.FromSeconds(2), net.Calls[1].At - net.Calls[0].At);
        Assert.Equal(TimeSpan.FromSeconds(4), net.Calls[2].At - net.Calls[0].At);
    }

    [Fact]
    public async Task Each_host_has_its_own_gap()
    {
        var net = new FakeNet();
        var client = net.Client(_ => new HostPolicy(1, TimeSpan.FromSeconds(5)));
        (await client.GetAsync("https://one.example/a")).Dispose();
        (await client.GetAsync("https://two.example/a")).Dispose();
        Assert.Equal(net.Calls[0].At, net.Calls[1].At);
    }

    [Fact]
    public async Task Retry_after_is_honored_and_the_request_then_succeeds()
    {
        var net = new FakeNet();
        net.Answers.Enqueue(() => Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(10)));
        var client = net.Client(_ => new HostPolicy(1, TimeSpan.Zero));

        using var response = await client.GetAsync("https://api.example/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, net.Calls.Count);
        Assert.True(net.Calls[1].At - net.Calls[0].At >= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Without_retry_after_the_wait_grows_exponentially()
    {
        var net = new FakeNet();
        net.Answers.Enqueue(() => Status(HttpStatusCode.ServiceUnavailable));
        net.Answers.Enqueue(() => Status(HttpStatusCode.ServiceUnavailable));
        var client = net.Client(_ => new HostPolicy(1, TimeSpan.Zero));

        using var response = await client.GetAsync("https://api.example/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), net.Calls[1].At - net.Calls[0].At);
        Assert.Equal(TimeSpan.FromSeconds(4), net.Calls[2].At - net.Calls[1].At);
    }

    [Fact]
    public async Task A_long_retry_after_fails_fast_and_pauses_the_host()
    {
        var net = new FakeNet();
        net.Default = () => Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(10));
        var client = net.Client(_ => new HostPolicy(1, TimeSpan.Zero));

        using var first = await client.GetAsync("https://api.example/x");
        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Single(net.Calls);

        // Paused: no request leaves the machine.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.example/y"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Single(net.Calls);

        net.Now += TimeSpan.FromMinutes(11);
        net.Default = () => new HttpResponseMessage(HttpStatusCode.OK);
        using var later = await client.GetAsync("https://api.example/y");
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    [Fact]
    public async Task The_circuit_opens_after_repeated_errors_and_closes_again_later()
    {
        var net = new FakeNet();
        net.Default = () => Status(HttpStatusCode.InternalServerError);
        var client = net.Client(_ => new HostPolicy(1, TimeSpan.Zero));

        for (var i = 0; i < 5; i++) (await client.GetAsync("https://cdn.example/a")).Dispose();
        var calls = net.Calls.Count;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://cdn.example/a"));
        Assert.Equal(calls, net.Calls.Count);
        // A different host is not affected.
        (await client.GetAsync("https://other.example/a")).Dispose();

        net.Now += TimeSpan.FromMinutes(6);
        net.Default = () => new HttpResponseMessage(HttpStatusCode.OK);
        using var ok = await client.GetAsync("https://cdn.example/a");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Not_found_is_not_a_failure_of_the_host()
    {
        var net = new FakeNet();
        net.Default = () => Status(HttpStatusCode.NotFound);
        var client = net.Client(_ => new HostPolicy(1, TimeSpan.Zero));
        for (var i = 0; i < 20; i++) (await client.GetAsync("https://cdn.example/a")).Dispose();
        Assert.Equal(20, net.Calls.Count);
    }

    [Fact]
    public void Default_limits_are_gentle_for_the_store_and_the_api()
    {
        Assert.Equal(1, PoliteHandler.DefaultPolicy("store.steampowered.com").MaxParallel);
        Assert.True(PoliteHandler.DefaultPolicy("store.steampowered.com").MinInterval >= TimeSpan.FromSeconds(1));
        Assert.Equal(1, PoliteHandler.DefaultPolicy("www.steamgriddb.com").MaxParallel);
        Assert.True(PoliteHandler.DefaultPolicy("cdn.example").MaxParallel <= 2);
    }
}
