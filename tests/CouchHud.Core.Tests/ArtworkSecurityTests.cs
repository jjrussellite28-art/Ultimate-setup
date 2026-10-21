using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CouchHud.Core.Library;
using CouchHud.Core.Net;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>Where artwork may come from, what may be stored, and what a hostile or broken source can not do.</summary>
public class ArtworkSecurityTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("couchhud-sec-").FullName;

    private static byte[] Png() => ArtworkCacheTests.Png();

    // ----- addresses -----

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.178.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:192.168.0.1")]
    public void Private_loopback_and_reserved_addresses_are_not_public(string address) =>
        Assert.False(SafeNet.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("104.18.20.5")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_are_allowed(string address) => Assert.True(SafeNet.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task The_safe_handler_refuses_to_connect_to_private_addresses_and_follows_no_redirects()
    {
        using var handler = SafeNet.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        // Nothing listens there, and the answer must come from the check, before any connection attempt.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:9/x"));
        Assert.Contains("public", ex.Message);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://169.254.169.254/latest/meta-data"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://[::1]:9/x"));
    }

    // ----- hosts -----

    [Theory]
    [InlineData("https://cdn.steamstatic.com/a.jpg", true)]
    [InlineData("https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/1/library_600x900.jpg", true)]
    [InlineData("https://cdn2.steamgriddb.com/grid/x.png", true)]
    [InlineData("https://cdn1.epicgames.com/x/y.jpg", true)]
    [InlineData("https://cdn2.unrealengine.com/x.jpg", true)]
    [InlineData("https://steamcdn-a.akamaihd.net/x.jpg", true)]
    [InlineData("http://cdn.steamstatic.com/a.jpg", false)]
    [InlineData("https://cdn.steamstatic.com:8443/a.jpg", false)]
    [InlineData("https://user:pw@cdn.steamstatic.com/a.jpg", false)]
    [InlineData("https://evilsteamstatic.com/a.jpg", false)]
    [InlineData("https://steamstatic.com.evil.example/a.jpg", false)]
    [InlineData("https://169.254.169.254/latest", false)]
    [InlineData("https://localhost/a.png", false)]
    [InlineData("https://router.fritz.box/a.png", false)]
    [InlineData("file:///C:/a.png", false)]
    [InlineData("https://art.couchhud/abc", false)]
    [InlineData("not a url", false)]
    public void Only_the_hosts_of_the_known_sources_may_be_downloaded(string url, bool allowed)
    {
        Assert.Equal(allowed, ArtworkSources.IsAllowed(url));
        // The UI is sent to the cache for exactly those, and loads everything else as before.
        Assert.Equal(allowed, ArtworkSources.CacheUrl(url) is not null);
        if (allowed) Assert.Equal(ArtworkCache.UrlFor(url), ArtworkSources.CacheUrl(url));
    }

    // ----- downloads -----

    private sealed class Script : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Func<Uri, CancellationToken, Task<HttpResponseMessage>> Answer { get; set; } = (_, _) => Task.FromResult(Ok());

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Answer(request.RequestUri!, ct);
        }
    }

    private static HttpResponseMessage Ok(byte[]? body = null, string type = "image/png") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body ?? Png()) { Headers = { ContentType = new MediaTypeHeaderValue(type) } } };

    private static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

    private static ArtworkResolver Resolver(Script script, ArtworkCache cache) =>
        new(new HttpClient(script), Path.Combine(TempDir(), "art.json"), null, cache);

    [Fact]
    public async Task A_host_outside_the_allowlist_is_never_asked()
    {
        var script = new Script();
        var cache = new ArtworkCache(TempDir());
        var resolver = Resolver(script, cache);

        Assert.Equal(0, await resolver.DownloadMissingAsync([["https://169.254.169.254/latest/x.png"], ["https://intranet.example/x.png"]]));
        Assert.False(await resolver.CacheNowAsync("https://router.local/logo.png"));
        Assert.Empty(script.Requests);
    }

    [Fact]
    public async Task A_redirect_into_the_private_network_is_not_followed()
    {
        var script = new Script { Answer = (_, _) => Task.FromResult(Redirect("https://169.254.169.254/latest/meta-data")) };
        var cache = new ArtworkCache(TempDir());

        Assert.Equal(0, await Resolver(script, cache).DownloadMissingAsync([["https://cdn.steamstatic.com/a.png"]]));

        Assert.Single(script.Requests);
        Assert.True(cache.RecentlyFailed("https://cdn.steamstatic.com/a.png"));
    }

    [Theory]
    [InlineData("http://cdn.steamstatic.com/b.png")]
    [InlineData("https://evil.example/b.png")]
    [InlineData("https://cdn.steamstatic.com:8443/b.png")]
    public async Task A_redirect_to_a_plain_or_foreign_address_is_refused(string target)
    {
        var script = new Script { Answer = (_, _) => Task.FromResult(Redirect(target)) };
        Assert.Equal(0, await Resolver(script, new ArtworkCache(TempDir())).DownloadMissingAsync([["https://cdn.steamstatic.com/a.png"]]));
        Assert.Single(script.Requests);
    }

    [Fact]
    public async Task A_redirect_to_another_allowed_host_is_followed_a_few_times_only()
    {
        var script = new Script
        {
            Answer = (uri, _) => Task.FromResult(uri.AbsolutePath == "/final.png" ? Ok() : Redirect(uri.AbsolutePath == "/a.png" ? "https://cdn2.steamgriddb.com/final.png" : "https://cdn.steamstatic.com/a.png"))
        };
        var cache = new ArtworkCache(TempDir());
        Assert.Equal(1, await Resolver(script, cache).DownloadMissingAsync([["https://cdn.steamstatic.com/a.png"]]));
        Assert.True(cache.Has("https://cdn.steamstatic.com/a.png"));

        // An endless chain stops after three hops.
        var loop = new Script { Answer = (_, _) => Task.FromResult(Redirect("https://cdn.steamstatic.com/loop.png")) };
        Assert.Equal(0, await Resolver(loop, new ArtworkCache(TempDir())).DownloadMissingAsync([["https://cdn.steamstatic.com/loop.png"]]));
        Assert.Equal(4, loop.Requests.Count);
    }

    [Fact]
    public async Task A_download_that_hangs_ends_at_the_time_limit_and_is_tried_again_later()
    {
        var script = new Script { Answer = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Ok(); } };
        var cache = new ArtworkCache(TempDir());
        var resolver = Resolver(script, cache);
        resolver.DownloadTimeout = TimeSpan.FromMilliseconds(100);

        var started = DateTime.UtcNow;
        Assert.Equal(0, await resolver.DownloadMissingAsync([["https://cdn.steamstatic.com/slow.png"]]));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.False(cache.RecentlyFailed("https://cdn.steamstatic.com/slow.png")); // not a verdict
    }

    [Fact]
    public async Task A_stream_that_stalls_after_the_headers_is_cut_off_too()
    {
        var script = new Script
        {
            Answer = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) })
        };
        var resolver = Resolver(script, new ArtworkCache(TempDir()));
        resolver.DownloadTimeout = TimeSpan.FromMilliseconds(100);
        Assert.Equal(0, await resolver.DownloadMissingAsync([["https://cdn.steamstatic.com/stall.png"]]));
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Garbage_is_a_lasting_miss_but_a_disk_problem_is_not()
    {
        var script = new Script { Answer = (_, _) => Task.FromResult(Ok(Encoding.UTF8.GetBytes("<html>not an image</html>"))) };
        var cache = new ArtworkCache(TempDir());
        await Resolver(script, cache).DownloadMissingAsync([["https://cdn.steamstatic.com/junk.png"]]);
        Assert.True(cache.RecentlyFailed("https://cdn.steamstatic.com/junk.png"));

        // The disk goes away: the image is fine, so nothing is remembered against it.
        var brokenDisk = new ArtworkCache(TempDir());
        Directory.Delete(brokenDisk.Root, recursive: true);
        Assert.Equal(ArtworkCache.StoreResult.Error, brokenDisk.TryStore("https://cdn.steamstatic.com/ok.png", Png()));
        Assert.Equal(0, await Resolver(new Script(), brokenDisk).DownloadMissingAsync([["https://cdn.steamstatic.com/ok.png"]]));
        Assert.False(brokenDisk.RecentlyFailed("https://cdn.steamstatic.com/ok.png"));
    }

    // ----- image dimensions -----

    private static byte[] PngOfSize(uint width, uint height)
    {
        var data = Png();
        for (var i = 0; i < 4; i++)
        {
            data[16 + i] = (byte)(width >> (24 - 8 * i));
            data[20 + i] = (byte)(height >> (24 - 8 * i));
        }
        return data;
    }

    private static byte[] WebpExtended(int width, int height)
    {
        var data = new byte[30];
        "RIFF"u8.CopyTo(data);
        BitConverter.GetBytes(22u).CopyTo(data, 4);
        "WEBPVP8X"u8.CopyTo(data.AsSpan(8));
        data[16] = 10;
        data[24] = (byte)(width - 1); data[25] = (byte)((width - 1) >> 8); data[26] = (byte)((width - 1) >> 16);
        data[27] = (byte)(height - 1); data[28] = (byte)((height - 1) >> 8); data[29] = (byte)((height - 1) >> 16);
        return data;
    }

    [Theory]
    [InlineData(600, 900, true)]
    [InlineData(8192, 8192, true)]
    [InlineData(8193, 100, false)]
    [InlineData(100, 20000, false)]
    [InlineData(0, 100, false)]
    [InlineData(65000, 65000, false)]
    public void Images_with_absurd_dimensions_are_refused_in_every_format(int width, int height, bool accepted)
    {
        var cache = new ArtworkCache(TempDir());
        Assert.Equal(accepted, cache.Store("png", PngOfSize((uint)width, (uint)height)));
        Assert.Equal(accepted, cache.Store("jpg", ArtworkCacheTests.JpegOfSize(width, height)));
        if (width > 0 && height > 0) Assert.Equal(accepted, cache.Store("webp", WebpExtended(width, height)));
    }

    [Fact]
    public void A_header_that_cannot_be_read_is_refused()
    {
        var cache = new ArtworkCache(TempDir());
        var noSize = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xD9 };
        Assert.Equal(ArtworkCache.StoreResult.Rejected, cache.TryStore("x", noSize));
    }

    // ----- cache details -----

    [Fact]
    public void Failures_are_written_in_batches_by_the_injected_clock_and_expire_with_it()
    {
        var now = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var dir = TempDir();
        var cache = new ArtworkCache(dir, utcNow: () => now);

        cache.MarkFailed("a");
        now = now.AddSeconds(1);
        cache.MarkFailed("b");
        Assert.True(new ArtworkCache(dir, utcNow: () => now).RecentlyFailed("a"));
        Assert.False(new ArtworkCache(dir, utcNow: () => now).RecentlyFailed("b")); // not written yet

        cache.Flush();
        Assert.True(new ArtworkCache(dir, utcNow: () => now).RecentlyFailed("b"));

        now = now.AddSeconds(10);
        cache.MarkFailed("c"); // the gap has passed: written at once
        Assert.True(new ArtworkCache(dir, utcNow: () => now).RecentlyFailed("c"));

        now = now.AddDays(8);
        Assert.False(cache.RecentlyFailed("a"));
    }

    [Fact]
    public void A_folder_given_with_a_trailing_separator_works()
    {
        var dir = TempDir();
        var cache = new ArtworkCache(dir + Path.DirectorySeparatorChar);
        Assert.True(cache.Store("https://x/a.png", Png()));
        Assert.NotNull(cache.Read("/" + ArtworkCache.HashOf("https://x/a.png")));
        Assert.False(cache.Root.EndsWith(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void A_replaced_file_is_not_forgotten_by_a_reader_that_saw_the_old_one()
    {
        var cache = new ArtworkCache(TempDir());
        cache.Store("https://x/a.png", Png());
        var path = cache.PathOf("https://x/a.png")!;
        var hash = ArtworkCache.HashOf("https://x/a.png");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);

        // A damaged file is dropped; a good download of the same image afterwards is stored and served again.
        Assert.Null(cache.Read("/" + hash));
        Assert.True(cache.Store("https://x/a.png", Png()));
        Assert.NotNull(cache.Read("/" + hash));
    }

    // ----- polite requests -----

    [Fact]
    public async Task A_cancelled_request_gives_its_slot_back()
    {
        var calls = 0;
        var inner = new Script();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var client = new HttpClient(new PoliteHandler(inner, "1.0", _ => new HostPolicy(1, TimeSpan.FromSeconds(5)), () => now, (d, _) =>
        {
            // The second request is cancelled while it waits for its turn.
            if (++calls == 1) throw new OperationCanceledException();
            now += d;
            return Task.CompletedTask;
        }));

        (await client.GetAsync("https://api.example/1")).Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://api.example/2"));
        // With a slot leaked, this would wait forever (one slot per host).
        var third = await client.GetAsync("https://api.example/3").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    [Fact]
    public async Task An_absurd_retry_after_is_capped_at_one_hour()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var inner = new Script
        {
            Answer = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(int.MaxValue)) }
            })
        };
        var client = new HttpClient(new PoliteHandler(inner, "1.0", _ => new HostPolicy(1, TimeSpan.Zero), () => now, (_, _) => Task.CompletedTask));

        (await client.GetAsync("https://api.example/x")).Dispose();
        now += TimeSpan.FromMinutes(59);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.example/x"));
        now += TimeSpan.FromMinutes(2);
        inner.Answer = (_, _) => Task.FromResult(Ok());
        (await client.GetAsync("https://api.example/x")).Dispose();
        Assert.Equal(2, inner.Requests.Count);
    }

    // ----- launcher files -----

    [Fact]
    public void Broken_epic_catalogs_give_no_art_and_no_crash()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "catcache.bin");
        foreach (var content in new[]
        {
            "", "%%%", "AAAA", Convert.ToBase64String(Encoding.UTF8.GetBytes("[{\"id\":\"a\",\"keyImages\":[{\"type\":")), // truncated
            Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"not\":\"an array\"}")),
            Convert.ToBase64String(Encoding.UTF8.GetBytes("[1, 2, null, \"x\", {\"id\": 5, \"keyImages\": 7}]")),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('[', 5000))), // deeper than the parser goes
            Convert.ToBase64String([0xFF, 0xFE, 0x00, 0xD8])
        })
        {
            File.WriteAllText(file, content);
            Assert.Empty(LauncherArtwork.ReadEpicCatalog(file));
        }
    }

    [Fact]
    public void An_oversized_catalog_is_not_read_and_a_changed_one_is_read_again()
    {
        var dir = TempDir();
        var big = Path.Combine(dir, "big.bin");
        using (var f = new FileStream(big, FileMode.Create)) f.SetLength(LauncherArtwork.MaxCatalogBytes + 1);
        Assert.Empty(LauncherArtwork.ReadEpicCatalog(big));
        File.Delete(big);

        string Catalog(string id) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $$"""[ { "id": "{{id}}", "keyImages": [ { "type": "DieselGameBoxTall", "url": "https://cdn1.epicgames.com/{{id}}.jpg" } ] } ]"""));
        var file = Path.Combine(dir, "catcache.bin");
        File.WriteAllText(file, Catalog("one"));
        Assert.Contains("one", LauncherArtwork.ReadEpicCatalog(file).Keys);
        // Same file again: the same answer (from memory), and the caller can't damage it.
        LauncherArtwork.ReadEpicCatalog(file).Clear();
        Assert.Contains("one", LauncherArtwork.ReadEpicCatalog(file).Keys);

        File.WriteAllText(file, Catalog("two"));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
        var again = LauncherArtwork.ReadEpicCatalog(file);
        Assert.Contains("two", again.Keys);
        Assert.DoesNotContain("one", again.Keys);
    }

    [Fact]
    public void Package_manifests_with_a_dtd_or_bad_paths_are_ignored()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "AppxManifest.xml"),
            """<?xml version="1.0"?><!DOCTYPE p [<!ENTITY e SYSTEM "file:///C:/Windows/win.ini">]><Package><VisualElements Square150x150Logo="&e;"/></Package>""");
        Assert.Empty(LauncherArtwork.FromPackage(dir).Icon);

        var bad = TempDir();
        File.WriteAllText(Path.Combine(bad, "AppxManifest.xml"),
            "<Package><VisualElements Square150x150Logo=\"Assets\\a<b>|\u0000.png\" Wide310x150Logo=\"C:\\Windows\\win.png\" StoreLogo=\"\\\\server\\share\\x.png\"/></Package>");
        var art = LauncherArtwork.FromPackage(bad);
        Assert.Empty(art.Cover);
        Assert.Empty(art.Hero);
    }

    // ----- exe icons -----

    private static LibraryItem ExeApp(string exe) => new()
    {
        Id = "custom:tool",
        Source = "custom",
        Name = "Tool",
        Type = ItemType.App,
        Launch = new LaunchTarget(LaunchKind.Exe, exe)
    };

    private static ArtworkResolver IconResolver(ArtworkCache cache, Func<string, byte[]?> icons) =>
        new(new HttpClient(new Script()), Path.Combine(TempDir(), "a.json"), null, cache) { ExeIcons = icons, Online = false };

    [Fact]
    public async Task An_updated_exe_gets_a_fresh_icon_and_a_missing_one_is_not_held_against_it()
    {
        var cache = new ArtworkCache(TempDir());
        var exe = Path.Combine(TempDir(), "tool.exe");
        File.WriteAllText(exe, "v1");
        var rendered = 0;
        byte[]? Icons(string _) { rendered++; return Png(); }

        Assert.NotNull(await IconResolver(cache, Icons).ResolveAsync([ExeApp(exe)]));
        Assert.Equal(1, rendered);
        await IconResolver(cache, Icons).ResolveAsync([ExeApp(exe)]);
        Assert.Equal(1, rendered); // the second pass found it on disk (ApplyCached)

        File.WriteAllText(exe, "v2, bigger");
        File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddHours(1));
        Assert.NotNull(await IconResolver(cache, Icons).ResolveAsync([ExeApp(exe)]));
        Assert.Equal(2, rendered);

        // Not there (unplugged drive): nothing rendered, nothing remembered.
        var gone = Path.Combine(TempDir(), "gone.exe");
        Assert.Null(await IconResolver(cache, Icons).ResolveAsync([ExeApp(gone)]));
        Assert.Equal(2, rendered);
        File.WriteAllText(gone, "now it is here");
        Assert.NotNull(await IconResolver(cache, Icons).ResolveAsync([ExeApp(gone)]));
        Assert.Equal(3, rendered);
    }

    [Fact]
    public async Task An_exe_without_icon_is_not_asked_again_and_icons_wait_for_the_game_to_end()
    {
        var cache = new ArtworkCache(TempDir());
        var exe = Path.Combine(TempDir(), "plain.exe");
        File.WriteAllText(exe, "x");
        var rendered = 0;
        var resolver = IconResolver(cache, _ => { rendered++; return null; });
        await resolver.ResolveAsync([ExeApp(exe)]);
        await IconResolver(cache, _ => { rendered++; return null; }).ResolveAsync([ExeApp(exe)]);
        Assert.Equal(1, rendered);

        var gameRuns = true;
        var waits = 0;
        var waiting = IconResolver(cache, _ => Png());
        waiting.BackgroundAllowed = () => !gameRuns;
        waiting.BackgroundWait = _ => { waits++; gameRuns = false; return Task.CompletedTask; };
        var exe2 = Path.Combine(TempDir(), "other.exe");
        File.WriteAllText(exe2, "y");
        Assert.NotNull(await waiting.ResolveAsync([ExeApp(exe2)]));
        Assert.Equal(1, waits);
    }

    // ----- picker -----

    [Fact]
    public async Task The_picker_marks_the_current_image_by_its_original_address()
    {
        var resolver = new ArtworkResolver(new HttpClient(new Script()), Path.Combine(TempDir(), "a.json"), null) { Online = false };
        var item = ExeApp(@"C:\x.exe") with
        {
            Art = new Artwork { Cover = ["https://cdn2.steamgriddb.com/cover.png"], Hero = ["file:///C:/mine/hero.png"] }
        };

        var choices = await resolver.GetChoicesAsync(item);

        Assert.Equal("https://cdn2.steamgriddb.com/cover.png", Assert.Single(choices.Cover, c => c.Current).Url);
        Assert.Equal("file:///C:/mine/hero.png", Assert.Single(choices.Hero, c => c.Current).Url);
        Assert.DoesNotContain(choices.Logo, c => c.Current);
    }
}
