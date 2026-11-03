using System.Net;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Net;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// SDK 5: a plugin reaches the network only through the client the host makes for one of its declared services. What a manifest may declare,
/// which addresses a request may go to, what happens on a redirect (no undeclared host, no step down to http, no credential to another host),
/// that a switched-off service or Offline stops every hop, and how the weather, the news and the providers' keys move from the core into
/// their plugins (once, and a removed plugin leaves no key behind).
/// </summary>
public sealed class PluginNetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-pluginnet-" + Guid.NewGuid().ToString("N"));

    public PluginNetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static PluginServiceManifest Svc(string? id, params string?[] hosts) => new(id, new LocalizedText(new Dictionary<string, string> { ["de"] = "Dienst", ["en"] = "Service" }), hosts);

    private static PluginService Declared(params string[] hosts) => new("api", "Dienst", "Service", hosts);

    // =====================================================================
    // What a manifest may declare

    [Fact]
    public void Services_are_only_read_from_sdk_5_on()
    {
        Assert.Empty(PluginNetRules.Parse([Svc("api", "api.example.com")], sdk: 4));
        Assert.Single(PluginNetRules.Parse([Svc("api", "api.example.com")], sdk: 5));
        Assert.Empty(PluginNetRules.Parse(null, sdk: 5));
    }

    [Fact]
    public void Hosts_must_be_dns_names_and_bad_ones_are_dropped()
    {
        var parsed = PluginNetRules.Parse([Svc("api", "API.Example.com", "api.example.com", "127.0.0.1", "[::1]", "localhost", "*.example.com",
            "example.com:8080", "http://example.com", "1.2.3.4.5", "", null, "router.fritz.box")], sdk: 5);

        var svc = Assert.Single(parsed);
        Assert.Equal(["api.example.com", "router.fritz.box"], svc.Hosts);
        Assert.False(svc.AnyHost);
    }

    [Fact]
    public void Bad_or_repeated_ids_services_without_a_host_and_more_than_four_are_dropped()
    {
        var log = new List<string>();
        var parsed = PluginNetRules.Parse([Svc("a", "a.example.com"), Svc("a", "b.example.com"), Svc("../x", "c.example.com"), Svc(null, "d.example.com"),
            Svc("nohost", "127.0.0.1"), Svc("b", "*"), Svc("c", "c.example.com"), Svc("d", "d.example.com"), Svc("e", "e.example.com")], sdk: 5, log.Add);

        Assert.Equal(["a", "b", "c", "d"], parsed.Select(s => s.Id));
        Assert.True(parsed[1].AnyHost);
        Assert.NotEmpty(log);
    }

    [Fact]
    public void A_service_gets_a_name_in_both_languages_without_control_characters()
    {
        var only = PluginNetRules.Parse([new PluginServiceManifest("api", new LocalizedText(new Dictionary<string, string> { ["en"] = "Feeds\u0007" }), ["a.example.com"])], sdk: 5)[0];
        Assert.Equal("Feeds", only.NameEn);
        Assert.Equal("Feeds", only.NameDe);

        var none = PluginNetRules.Parse([new PluginServiceManifest("api", null, ["a.example.com"])], sdk: 5)[0];
        Assert.Equal("api", none.NameDe);
        Assert.Equal("weather/open-meteo", PluginService.Key("Weather", "open-meteo"));
    }

    // =====================================================================
    // Which address a request may go to

    [Theory]
    [InlineData("https://api.example.com/v1", true)]
    [InlineData("http://api.example.com/v1", false)]
    [InlineData("https://API.EXAMPLE.COM/v1", true)]
    [InlineData("https://api.example.com:8443/v1", false)]
    [InlineData("https://user:pw@api.example.com/v1", false)]
    [InlineData("https://other.example.com/v1", false)]
    [InlineData("https://api.example.com.evil.net/v1", false)]
    [InlineData("ftp://api.example.com/v1", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    public void Only_declared_hosts_on_their_default_port_over_https_are_allowed(string url, bool allowed) =>
        Assert.Equal(allowed, PluginNetRules.AllowsUri(Declared("api.example.com"), new Uri(url)));

    [Fact]
    public void Any_host_still_needs_http_and_no_userinfo()
    {
        var any = Declared(PluginNetServices.AnyHost);
        Assert.True(PluginNetRules.AllowsUri(any, new Uri("http://news.example.org/rss")));
        Assert.False(PluginNetRules.AllowsUri(any, new Uri("https://news.example.org:8443/rss")));
        Assert.False(PluginNetRules.AllowsUri(any, new Uri("https://a:b@news.example.org/rss")));
        Assert.False(PluginNetRules.AllowsUri(any, new Uri("ftp://news.example.org/rss")));
        Assert.False(PluginNetRules.AllowsUri(any, null));
    }

    // =====================================================================
    // The handler below the plugin's client

    /// <summary>Answers each address from a table and records what was sent.</summary>
    private sealed class FakeNet(Dictionary<string, Func<HttpResponseMessage>> answers) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(request);
            return Task.FromResult(answers.TryGetValue(request.RequestUri!.ToString(), out var answer) ? answer() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static Func<HttpResponseMessage> Redirect(string to) => () =>
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return r;
    };

    private static Func<HttpResponseMessage> Ok => () => new HttpResponseMessage(HttpStatusCode.OK);

    private static HttpClient Client(FakeNet net, PluginService service, Func<bool>? allowed = null) =>
        new(new PluginHttpHandler("test", service, allowed ?? (() => true), net));

    [Fact]
    public async Task A_request_to_an_undeclared_host_never_leaves()
    {
        var net = new FakeNet(new() { ["https://evil.example.net/"] = Ok });
        using var client = Client(net, Declared("api.example.com"));

        await Assert.ThrowsAsync<NetBlockedException>(() => client.GetAsync("https://evil.example.net/"));
        Assert.Empty(net.Sent);
    }

    [Fact]
    public async Task A_switched_off_service_or_offline_stops_the_request_before_it_is_sent()
    {
        var net = new FakeNet(new() { ["https://api.example.com/"] = Ok });
        using var client = Client(net, Declared("api.example.com"), () => false);

        await Assert.ThrowsAsync<NetBlockedException>(() => client.GetAsync("https://api.example.com/"));
        Assert.Empty(net.Sent);
    }

    [Fact]
    public async Task A_redirect_to_an_undeclared_host_is_refused()
    {
        var net = new FakeNet(new() { ["https://api.example.com/a"] = Redirect("https://evil.example.net/steal"), ["https://evil.example.net/steal"] = Ok });
        using var client = Client(net, Declared("api.example.com"));

        await Assert.ThrowsAsync<NetBlockedException>(() => client.GetAsync("https://api.example.com/a"));
        Assert.Single(net.Sent);
    }

    [Fact]
    public async Task The_switch_is_asked_again_on_every_hop()
    {
        var calls = 0;
        var net = new FakeNet(new() { ["https://api.example.com/a"] = Redirect("/b"), ["https://api.example.com/b"] = Ok });
        using var client = Client(net, Declared("api.example.com"), () => ++calls == 1);

        await Assert.ThrowsAsync<NetBlockedException>(() => client.GetAsync("https://api.example.com/a"));
        Assert.Single(net.Sent);
    }

    [Fact]
    public async Task A_redirect_from_https_to_http_is_not_followed()
    {
        var net = new FakeNet(new() { ["https://api.example.com/a"] = Redirect("http://api.example.com/b"), ["http://api.example.com/b"] = Ok });
        using var client = Client(net, Declared("api.example.com"));

        using var response = await client.GetAsync("https://api.example.com/a");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(net.Sent);
    }

    [Fact]
    public async Task Credentials_go_only_to_the_host_they_were_meant_for()
    {
        var net = new FakeNet(new()
        {
            ["https://a.example.com/x"] = Redirect("https://a.example.com/y"),
            ["https://a.example.com/y"] = Redirect("https://b.example.com/z"),
            ["https://b.example.com/z"] = Ok
        });
        using var client = Client(net, Declared("a.example.com", "b.example.com"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://a.example.com/x");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer sk-secret");
        request.Headers.TryAddWithoutValidation("x-api-key", "sk-secret");
        request.Headers.TryAddWithoutValidation("Cookie", "session=1");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, net.Sent.Count);
        Assert.True(net.Sent[1].Headers.Contains("Authorization"));
        var other = net.Sent[2];
        Assert.False(other.Headers.Contains("Authorization"));
        Assert.False(other.Headers.Contains("x-api-key"));
        Assert.False(other.Headers.Contains("Cookie"));
        Assert.True(other.Headers.Contains("Accept"));
    }

    [Fact]
    public async Task Redirects_end_after_five_hops_and_a_post_is_never_redirected()
    {
        var answers = new Dictionary<string, Func<HttpResponseMessage>>();
        for (var i = 0; i < 10; i++) answers[$"https://api.example.com/{i}"] = Redirect($"/{i + 1}");
        var net = new FakeNet(answers);
        using var client = Client(net, Declared("api.example.com"));

        using (var response = await client.GetAsync("https://api.example.com/0"))
        {
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }
        Assert.Equal(PluginHttpHandler.MaxRedirects + 1, net.Sent.Count);

        net.Sent.Clear();
        using var posted = await client.PostAsync("https://api.example.com/0", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Found, posted.StatusCode);
        Assert.Single(net.Sent);
    }

    [Fact]
    public void Using_the_hosts_client_is_no_network_finding_but_making_a_client_still_is()
    {
        // The SDK names HttpClient (IPluginContext.Http) but never makes one.
        Assert.DoesNotContain(PluginLint.Scan(File.ReadAllBytes(typeof(IPluginContext).Assembly.Location), "sdk.dll"), f => f.Capability == "network");
        // These tests make clients (new HttpClient(...)): that still needs a network capability.
        Assert.Contains(PluginLint.Scan(File.ReadAllBytes(typeof(PluginNetTests).Assembly.Location), "tests.dll"), f => f.Capability == "network" && f.Detail == "HttpClient..ctor");
    }

    // =====================================================================
    // The weather, the news and the keys move into their plugins

    private static string Protect(string plain, string? scope) => "dpapi:" + Convert.ToBase64String(Encoding.UTF8.GetBytes((scope ?? "core") + "|" + plain)).Replace("=", "-");

    private static string Unprotect(string stored, string? scope)
    {
        if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) return stored;
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(stored["dpapi:".Length..].Replace("-", "=")));
        var bar = text.IndexOf('|');
        return text[..bar] == (scope ?? "core") ? text[(bar + 1)..] : "";
    }

    private static PluginSettingSeeds.SecretMover Mover => (plugin, stored) => Protect(Unprotect(stored, null), plugin);

    [Fact]
    public void The_weather_place_becomes_the_plugins_first_setting_and_then_leaves_the_config()
    {
        var config = new AppConfig { WeatherLocation = new LegacyPlace("Bremen\u0007", "Bremen, Deutschland", 53.08, 8.81) };

        var seed = PluginSettingSeeds.For(config, "weather");

        using var place = JsonDocument.Parse(seed!["place"]!);
        Assert.Equal("Bremen", place.RootElement.GetProperty("name").GetString());
        Assert.Equal(53.08, place.RootElement.GetProperty("latitude").GetDouble());
        Assert.True(PluginSettingSeeds.ClearLegacy(config, "weather", seed));
        Assert.Null(config.WeatherLocation);
        Assert.Null(PluginSettingSeeds.For(config, "weather"));
    }

    [Theory]
    [InlineData(91, 8)]
    [InlineData(53, 181)]
    [InlineData(double.NaN, 8)]
    public void A_broken_place_is_not_handed_over(double lat, double lon) =>
        Assert.Null(PluginSettingSeeds.For(new AppConfig { WeatherLocation = new LegacyPlace("X", "", lat, lon) }, "weather"));

    [Fact]
    public void Only_the_users_own_feeds_move_and_the_old_default_sets_stay_behind()
    {
        static LegacyNewsFeed F(string url, string? name = null) => new() { Url = url, Name = name };

        Assert.Null(PluginSettingSeeds.For(new AppConfig { NewsFeeds = [F("https://static.winfuture.de/feeds/WinFuture-News-rss2.0.xml"), F("https://stadt-bremerhaven.de/feed/")] }, "news"));
        Assert.Null(PluginSettingSeeds.For(new AppConfig { NewsFeeds = [F("https://www.theverge.com/rss/games/index.xml"), F("https://www.pcgamer.com/rss/")] }, "news"));
        Assert.Null(PluginSettingSeeds.For(new AppConfig(), "news"));

        var config = new AppConfig { NewsFeeds = [F("https://example.org/rss", "Mine"), F("https://example.org/rss"), F("javascript:alert(1)"), F("file:///C:/x"), F("https://other.example/feed")] };
        var seed = PluginSettingSeeds.For(config, "news");

        using var feeds = JsonDocument.Parse(seed!["feeds"]!);
        Assert.Equal(["https://example.org/rss", "https://other.example/feed"], feeds.RootElement.EnumerateArray().Select(f => f.GetProperty("url").GetString()));
        Assert.Equal("Mine", feeds.RootElement[0].GetProperty("name").GetString());
        Assert.True(PluginSettingSeeds.ClearLegacy(config, "news", seed));
        Assert.Null(config.NewsFeeds);
    }

    [Fact]
    public void An_api_key_is_encrypted_again_for_its_provider_plugin_and_only_then_cleared()
    {
        var config = new AppConfig { AnthropicApiKey = Protect("sk-ant-key", null), OpenAiApiKey = Protect("sk-openai-key", null) };

        var seed = PluginSettingSeeds.For(config, "anthropic", Mover);

        Assert.Equal("sk-ant-key", Unprotect(seed!["apiKey"]!, "anthropic"));
        Assert.Equal("", Unprotect(seed["apiKey"]!, "openai"));
        Assert.DoesNotContain("sk-ant-key", seed["apiKey"]);
        Assert.True(PluginSettingSeeds.ClearLegacy(config, "anthropic", seed));
        Assert.Equal("", config.AnthropicApiKey);
        Assert.NotEqual("", config.OpenAiApiKey);

        // Without a working move the key stays in the config (a later start tries again) and nothing is handed over.
        Assert.Null(PluginSettingSeeds.For(config, "openai"));
        Assert.Null(PluginSettingSeeds.For(config, "openai", (_, _) => null));
        Assert.Null(PluginSettingSeeds.For(config, "openai", (_, _) => "sk-openai-key"));
        Assert.False(PluginSettingSeeds.ClearLegacy(config, "openai", new Dictionary<string, string?>()));
        Assert.NotEqual("", config.OpenAiApiKey);
    }

    [Fact]
    public void A_removed_plugin_leaves_no_secret_behind_but_keeps_its_other_settings()
    {
        var file = Path.Combine(_dir, "anthropic.json");
        PluginSetting[] declared = [new() { Key = "apiKey", Label = "Key", Kind = SettingKind.Secret }, new() { Key = "model", Label = "Model", Kind = SettingKind.Text }];
        var store = new SettingsStore(file, () => declared, protect: p => Protect(p, "anthropic"), unprotect: s => Unprotect(s, "anthropic"));
        store.Set("apiKey", "sk-ant-key");
        store.Set("model", "m1");
        Assert.Contains("dpapi:", File.ReadAllText(file));

        var again = new SettingsStore(file, () => declared, unprotect: s => Unprotect(s, "anthropic"));
        Assert.Equal(1, again.DropSecrets());

        var text = File.ReadAllText(file);
        Assert.DoesNotContain("dpapi:", text);
        Assert.Contains("m1", text);
        Assert.Equal(0, again.DropSecrets());
    }

    [Fact]
    public void An_unreadable_settings_file_of_a_removed_plugin_goes_as_a_whole()
    {
        var file = Path.Combine(_dir, "broken.json");
        File.WriteAllText(file, "{ not json dpapi:abc");

        new SettingsStore(file, () => []).DropSecrets();

        Assert.False(File.Exists(file));
    }
}
