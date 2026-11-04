using System.Net;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Integrations.Tv;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// TV control: the link a TV plugin gets (against a fake TV on loopback), the provider registry, the hosted session, the controller with a
/// small test provider, the address and input rules, Wake-on-LAN and SSDP. The protocols themselves (webOS, Tizen) are tested in the
/// plugins' repositories.
/// </summary>
public class TvTests
{
    private const string Plugin = "webostv";

    private static TvLinkOptions LinkOptions() => new()
    {
        AllowLoopback = true,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        HttpTimeout = TimeSpan.FromSeconds(3)
    };

    private static TvTimeouts Timeouts(double pairSeconds = 10) => new()
    {
        Pair = TimeSpan.FromSeconds(pairSeconds),
        Connect = TimeSpan.FromSeconds(5),
        Call = TimeSpan.FromMilliseconds(1500)
    };

    private static TestTvProvider Provider(FakeWebOsTv? secure, FakeWebOsTv? legacy = null, int promptMs = 3000) =>
        new(secure?.Port ?? FakeWebOsTv.FreePort(), legacy?.Port ?? FakeWebOsTv.FreePort(), promptMs);

    private static TvProviderRegistry Registry(ITvProvider provider, string plugin = Plugin)
    {
        var registry = new TvProviderRegistry();
        Assert.Null(registry.Register(plugin, provider, PluginOrigin.FirstParty));
        return registry;
    }

    private static TvController Controller(ITvProvider provider, ITvStore store, Action<TvStatus> emit, Func<long>? now = null,
        Func<byte[], IPAddress, CancellationToken, Task<bool>>? wake = null, double pairSeconds = 10) =>
        new(Registry(provider), store, emit, now, wake, null, LinkOptions(), Timeouts(pairSeconds));

    private sealed class MemoryStore : ITvStore
    {
        public TvPairing? Stored;
        public int Saves;
        public TvPairing? Load() => Stored;

        public void Save(TvPairing? pairing)
        {
            Stored = pairing;
            Saves++;
        }

        public readonly List<string> Seen = [];
        public IReadOnlyCollection<string> SecureSeen() => Seen.ToList();
        public void RememberSecure(IEnumerable<string> ids) => Seen.AddRange(ids.Where(i => !Seen.Contains(i)));
    }

    private static TvPairing Stored(string address, bool secure, string key, string? pin, string? mac = null, string plugin = Plugin, string provider = TvProviderRegistry.LgWebOs) =>
        new(provider, address, secure, key, pin, mac, null, null, null, plugin);

    // ---- pairing through the controller -------------------------------------------------------------------------------------

    [Fact]
    public async Task Pairing_over_tls_shows_prompt_and_pins_the_certificate_the_link_saw()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var store = new MemoryStore();
        var statuses = new List<TvStatus>();
        await using var c = Controller(Provider(tv), store, s => { lock (statuses) statuses.Add(s); });

        var status = await c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs);
        Assert.True(status.Paired);
        Assert.True(status.Secure);
        lock (statuses) Assert.Contains(statuses, s => s.Pairing == "prompt");
        var p = store.Stored!;
        Assert.Equal(FakeWebOsTv.IssuedKey, p.ClientKey);
        Assert.Equal(TvText.SpkiPin(tv.Certificate!), p.CertPin);
        Assert.Equal("A8:23:FE:01:02:03", p.Mac);
        // The model came with a NUL from the TV: the core cleans what the plugin passes on.
        Assert.Equal("OLED55TEST", p.Model);
        Assert.Equal("com.webos.app.hdmi2", p.PcApp);
        Assert.Equal("127.0.0.1", p.Address);
        Assert.Equal(Plugin, p.Plugin);
        Assert.Equal(TvProviderRegistry.LgWebOs, p.Provider);
    }

    [Fact]
    public async Task Declined_prompt_is_denied()
    {
        await using var tv = new FakeWebOsTv(tls: true) { PromptAnswer = "deny" };
        var store = new MemoryStore();
        await using var c = Controller(Provider(tv), store, _ => { });
        Assert.Equal(TvError.Denied, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs))).Code);
        Assert.Null(store.Stored);
    }

    [Fact]
    public async Task A_plugin_that_never_finishes_pairing_is_stopped_by_the_core()
    {
        await using var tv = new FakeWebOsTv(tls: true) { PromptAnswer = "none" };
        var provider = Provider(tv, promptMs: 60_000);
        // The plugin ignores the token on top of that.
        provider.PairOverride = (_, _) => new TaskCompletionSource<TvPairResult>().Task;
        await using var c = Controller(provider, new MemoryStore(), _ => { }, pairSeconds: 0.5);
        var started = Environment.TickCount64;
        Assert.Equal(TvError.Timeout, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs))).Code);
        Assert.True(Environment.TickCount64 - started < 30_000);
        Assert.Equal("idle", c.Status().Pairing);
    }

    [Fact]
    public async Task Old_tv_without_tls_port_is_paired_over_plain_only_when_allowed_and_marked()
    {
        await using var legacy = new FakeWebOsTv(tls: false);
        var store = new MemoryStore();
        await using var c = Controller(Provider(null, legacy), store, _ => { });
        Assert.Equal(TvError.PlainOnly, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs))).Code);
        // The link refused before connecting: the old TV saw nothing.
        Assert.Equal(0, legacy.Connections);

        var status = await c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs, allowPlain: true);
        Assert.False(status.Secure);
        Assert.Null(store.Stored!.CertPin);
    }

    [Fact]
    public async Task Without_the_plugin_nothing_is_sent_and_the_status_says_which_plugin()
    {
        var store = new MemoryStore();
        await using var c = new TvController(new TvProviderRegistry(), store, _ => { }, null, null, null, LinkOptions(), Timeouts());
        Assert.Equal(TvError.NoProvider, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs))).Code);
        Assert.Equal("noProvider", c.Status().Error);
        Assert.Empty(c.Status().Providers!);

        await using var tv = new FakeWebOsTv(tls: true);
        store.Stored = Stored("127.0.0.1", true, FakeWebOsTv.IssuedKey, TvText.SpkiPin(tv.Certificate!));
        await using var c2 = new TvController(new TvProviderRegistry(), store, _ => { }, null, null, null, LinkOptions(), Timeouts());
        var status = await c2.RefreshAsync(user: true);
        Assert.True(status.Paired);
        Assert.True(status.ProviderMissing);
        Assert.Equal("webostv", status.InstallPlugin);
        Assert.Equal("noProvider", status.Error);
        Assert.Equal(0, tv.Connections);
    }

    [Fact]
    public async Task Pairing_from_before_the_plugins_migrates_to_the_official_plugin()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        // As 0.6 stored it: no plugin.
        var store = new MemoryStore { Stored = Stored("127.0.0.1", true, FakeWebOsTv.IssuedKey, TvText.SpkiPin(tv.Certificate!), plugin: "") };
        var provider = Provider(tv);
        var registry = new TvProviderRegistry();
        await using var c = new TvController(registry, store, _ => { }, null, null, null, LinkOptions(), Timeouts());
        var status = c.Status();
        Assert.True(status.Paired);
        Assert.True(status.Migrated);
        Assert.True(status.ProviderMissing);
        Assert.Equal("webostv", status.InstallPlugin);

        // The plugin arrives: the stored key works again without a new prompt.
        Assert.Null(registry.Register("webostv", provider, PluginOrigin.FirstParty));
        status = await c.RefreshAsync(user: true);
        Assert.False(status.ProviderMissing);
        Assert.Equal("on", status.Power);
        Assert.Equal([FakeWebOsTv.IssuedKey], provider.ConnectKeys);
        Assert.Equal(0, provider.Pairs);

        // A pairing of a kind nobody knows, without a plugin, is nobody's.
        var unknown = new MemoryStore { Stored = Stored("127.0.0.1", true, FakeWebOsTv.IssuedKey, "pin", plugin: "", provider: "acme-tv") };
        await using var c2 = new TvController(registry, unknown, _ => { }, null, null, null, LinkOptions(), Timeouts());
        Assert.False(c2.Paired);
    }

    [Fact]
    public void Config_migration_keeps_the_pairing_and_names_the_plugin()
    {
        var config = new TvConfig { Provider = TvProviderRegistry.LgWebOs, Address = "192.168.1.20", ClientKey = "enc", CertPin = "pin" };
        Assert.True(config.FromCore);
        var p = config.ToPairing(_ => "secret-key-123456")!;
        Assert.Equal("webostv", p.Plugin);
        config.SetPairing(p, s => "enc");
        Assert.False(config.FromCore);
        Assert.Equal("webostv", config.Plugin);
        Assert.Equal("", TvConfig.PluginOf("acme-tv", null));
        Assert.Equal("samsungtv", TvConfig.PluginOf(TvProviderRegistry.SamsungTizen, ""));
    }

    [Fact]
    public async Task The_key_goes_only_to_the_plugin_that_paired()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        // Paired through "acme"; "evil" registered the same kind of TV while acme is not there.
        var store = new MemoryStore { Stored = Stored("127.0.0.1", true, FakeWebOsTv.IssuedKey, TvText.SpkiPin(tv.Certificate!), plugin: "acme", provider: "acme-tv") };
        var impostor = new TestTvProvider(tv.Port, FakeWebOsTv.FreePort()) { Id = "acme-tv" };
        var registry = new TvProviderRegistry();
        Assert.Null(registry.Register("evil", impostor, PluginOrigin.Local));
        await using var c = new TvController(registry, store, _ => { }, null, null, null, LinkOptions(), Timeouts());
        var status = await c.RefreshAsync(user: true);
        Assert.Equal("noProvider", status.Error);
        Assert.True(status.ProviderMissing);
        Assert.Empty(impostor.ConnectKeys);
        Assert.Equal(0, tv.Connections);
    }

    [Fact]
    public async Task A_key_that_came_over_no_connection_is_no_pairing()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var provider = Provider(tv);
        provider.PairOverride = (_, _) => Task.FromResult(new TvPairResult("made-up-key-0123456789"));
        var store = new MemoryStore();
        await using var c = Controller(provider, store, _ => { });
        Assert.Equal(TvError.BadAnswer, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs))).Code);
        Assert.Null(store.Stored);
    }

    [Fact]
    public async Task A_key_over_a_key_free_port_only_is_no_pairing()
    {
        await using var tv = new FakeWebOsTv(tls: false);
        var provider = new TestTvProvider(FakeWebOsTv.FreePort(), FakeWebOsTv.FreePort()) { Ports = [new(tv.Port, false, KeyFree: true)] };
        provider.PairOverride = async (link, ct) =>
        {
            await link.SendAsync(new TvHttpRequest(tv.Port, "/api/v2/"), ct);
            return new TvPairResult("http-key-0123456789");
        };
        var store = new MemoryStore();
        await using var c = Controller(provider, store, _ => { });
        Assert.Equal(TvError.BadAnswer, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs, allowPlain: true))).Code);
        Assert.Null(store.Stored);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("key with spaces 123")]
    [InlineData("key-\u00e4-0123456789")]
    public async Task Bad_keys_from_a_plugin_are_no_pairing(string key)
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var provider = Provider(tv);
        provider.PairOverride = async (link, ct) =>
        {
            await using var ch = await link.OpenAsync(tv.Port, "/", ct);
            return new TvPairResult(key);
        };
        var store = new MemoryStore();
        await using var c = Controller(provider, store, _ => { });
        Assert.Equal(TvError.BadAnswer, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs))).Code);
        Assert.Null(store.Stored);
    }

    [Fact]
    public async Task A_refusal_of_the_link_counts_even_when_the_plugin_swallows_it()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var provider = Provider(tv);
        provider.BeforePair = async (link, ct) =>
        {
            try { await link.OpenAsync(80, "/", ct); }
            catch (TvException) { }
        };
        var store = new MemoryStore();
        await using var c = Controller(provider, store, _ => { });
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs))).Code);
        Assert.Null(store.Stored);
    }

    [Fact]
    public async Task Public_and_loopback_addresses_are_refused_before_any_connection()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var provider = Provider(tv);
        await using var c = Controller(provider, new MemoryStore(), _ => { });
        Assert.Equal(TvError.NotLocal, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Parse("8.8.8.8"), TvProviderRegistry.LgWebOs))).Code);
        Assert.Equal(0, provider.Pairs);

        // Without the tests' loopback switch the fake TV itself is out of reach.
        Assert.Equal(TvError.NotLocal, Assert.Throws<TvException>(() => TvLink.ForPairing(IPAddress.Loopback, provider.Ports, false)).Code);
        Assert.Equal(TvError.NotLocal, Assert.Throws<TvException>(() => TvLink.ForPairing(IPAddress.Parse("1.1.1.1"), provider.Ports, false, LinkOptions())).Code);
        Assert.Equal(TvError.NotLocal, Assert.Throws<TvException>(() => TvLink.ForSession(IPAddress.Parse("100.64.0.1"), provider.Ports, true, "pin", "key-0123456789", LinkOptions())).Code);

        var store = new MemoryStore { Stored = Stored("1.1.1.1", true, "k-0123456789", "pin") };
        await using var c2 = Controller(provider, store, _ => { });
        Assert.Equal("notLocal", (await c2.RefreshAsync(user: true)).Error);
        Assert.Empty(provider.ConnectKeys);
    }

    // ---- the link ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Link_pins_the_first_certificate_at_pairing_and_requires_it_in_a_session()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        TvPort[] ports = [new(tv.Port, true)];
        await using (var pairing = TvLink.ForPairing(IPAddress.Loopback, ports, false, LinkOptions()))
        {
            await using var ch = await pairing.OpenAsync(tv.Port, "/", CancellationToken.None);
            Assert.True(ch.Open);
            Assert.Equal(TvText.SpkiPin(tv.Certificate!), pairing.Pin);
            Assert.True(pairing.Secure);
            Assert.Null(pairing.Fault);
        }

        await using (var ok = TvLink.ForSession(IPAddress.Loopback, ports, true, TvText.SpkiPin(tv.Certificate!), FakeWebOsTv.IssuedKey, LinkOptions()))
        {
            await using var ch = await ok.OpenAsync(tv.Port, "/", CancellationToken.None);
            Assert.True(ch.Open);
        }

        await using var other = TvLink.ForSession(IPAddress.Loopback, ports, true, Convert.ToBase64String(new byte[32]), FakeWebOsTv.IssuedKey, LinkOptions());
        Assert.Equal(TvError.CertChanged, (await Assert.ThrowsAsync<TvException>(() => other.OpenAsync(tv.Port, "/", CancellationToken.None))).Code);
        Assert.Equal(TvError.CertChanged, other.Fault);
        // The handshake failed on CouchHUD's side: the TV never saw a request.
        Assert.Equal(2, tv.Upgrades.Count);

        // A TLS pairing without a pin is refused before anything.
        Assert.Equal(TvError.CertChanged, Assert.Throws<TvException>(() => TvLink.ForSession(IPAddress.Loopback, ports, true, null, FakeWebOsTv.IssuedKey, LinkOptions())).Code);
    }

    [Fact]
    public async Task Link_reaches_only_declared_ports_and_plain_only_when_allowed()
    {
        await using var tls = new FakeWebOsTv(tls: true);
        await using var plain = new FakeWebOsTv(tls: false);
        await using var undeclared = new FakeWebOsTv(tls: false);
        TvPort[] ports = [new(tls.Port, true), new(plain.Port, false)];

        await using var pairing = TvLink.ForPairing(IPAddress.Loopback, ports, false, LinkOptions());
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => pairing.OpenAsync(undeclared.Port, "/", CancellationToken.None))).Code);
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => pairing.SendAsync(new TvHttpRequest(undeclared.Port, "/"), CancellationToken.None))).Code);
        Assert.Equal(TvError.PlainOnly, (await Assert.ThrowsAsync<TvException>(() => pairing.OpenAsync(plain.Port, "/", CancellationToken.None))).Code);

        // A TLS pairing's session never uses the plain port.
        await using var session = TvLink.ForSession(IPAddress.Loopback, ports, true, TvText.SpkiPin(tls.Certificate!), FakeWebOsTv.IssuedKey, LinkOptions());
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => session.OpenAsync(plain.Port, "/", CancellationToken.None))).Code);

        Assert.Equal(0, undeclared.Connections);
        Assert.Equal(0, plain.Connections);

        // Port lists that do not pass.
        Assert.Null(TvLink.CheckPorts([]));
        Assert.Null(TvLink.CheckPorts([new(0, true)]));
        Assert.Null(TvLink.CheckPorts([new(70000, true)]));
        Assert.Null(TvLink.CheckPorts([new(3001, true), new(3001, false)]));
        Assert.Null(TvLink.CheckPorts(Enumerable.Range(1, TvPort.MaxPorts + 1).Select(p => new TvPort(p, true)).ToList()));
        Assert.False(TvLink.CheckPorts([new(8002, true, KeyFree: true)])![8002].KeyFree);
    }

    [Theory]
    [InlineData("")]
    [InlineData("api")]
    [InlineData("//8.8.8.8/x")]
    [InlineData("/a b")]
    [InlineData("/a\\b")]
    [InlineData("/a\r\nHost: evil")]
    [InlineData("/\u00e4")]
    public async Task Link_refuses_odd_paths(string path)
    {
        await using var tv = new FakeWebOsTv(tls: false);
        await using var link = TvLink.ForPairing(IPAddress.Loopback, [new(tv.Port, false, KeyFree: true)], false, LinkOptions());
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => link.SendAsync(new TvHttpRequest(tv.Port, path), CancellationToken.None))).Code);
        Assert.Equal(0, tv.Connections);
    }

    [Fact]
    public async Task Link_http_is_bounded_and_follows_no_redirect()
    {
        await using var tv = new FakeWebOsTv(tls: false);
        await using var link = TvLink.ForPairing(IPAddress.Loopback, [new(tv.Port, false, KeyFree: true)], false, LinkOptions());

        var ok = await link.SendAsync(new TvHttpRequest(tv.Port, "/api/v2/"), CancellationToken.None);
        Assert.Equal(200, ok.Status);
        Assert.Equal("{}", ok.Body);
        // Key-free HTTP is not a connection of the pairing.
        Assert.False(link.UsedPlain);
        Assert.False(link.UsedTls);

        tv.HttpAnswer = "HTTP/1.1 302 Found\r\nLocation: http://8.8.8.8/steal\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        var redirect = await link.SendAsync(new TvHttpRequest(tv.Port, "/api/v2/"), CancellationToken.None);
        Assert.Equal(302, redirect.Status);
        Assert.Equal(2, tv.HttpRequests.Count);

        var big = new string('a', 70 * 1024);
        tv.HttpAnswer = $"HTTP/1.1 200 OK\r\nContent-Length: {big.Length}\r\nConnection: close\r\n\r\n{big}";
        Assert.Equal(TvError.BadAnswer, (await Assert.ThrowsAsync<TvException>(() => link.SendAsync(new TvHttpRequest(tv.Port, "/x"), CancellationToken.None))).Code);
        tv.HttpAnswer = $"HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n{big}";
        Assert.Equal(TvError.BadAnswer, (await Assert.ThrowsAsync<TvException>(() => link.SendAsync(new TvHttpRequest(tv.Port, "/x"), CancellationToken.None))).Code);

        // Methods, bodies and headers a TV API needs, nothing else.
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => link.SendAsync(new TvHttpRequest(tv.Port, "/x") { Method = "PUT" }, CancellationToken.None))).Code);
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => link.SendAsync(new TvHttpRequest(tv.Port, "/x") { Body = "x" }, CancellationToken.None))).Code);
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => link.SendAsync(new TvHttpRequest(tv.Port, "/x") { Method = "POST", Body = new string('b', 17 * 1024) }, CancellationToken.None))).Code);
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => link.SendAsync(new TvHttpRequest(tv.Port, "/x") { Method = "POST", Body = "x", SoapAction = "a\r\nX-Evil: 1" }, CancellationToken.None))).Code);
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => link.SendAsync(new TvHttpRequest(tv.Port, "/x") { Method = "POST", Body = "x", ContentType = "text/xml\n" }, CancellationToken.None))).Code);
        Assert.Equal(4, tv.HttpRequests.Count);

        tv.HttpAnswer = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
        var post = await link.SendAsync(new TvHttpRequest(tv.Port, "/upnp/control/RenderingControl1") { Method = "POST", Body = "<x/>", ContentType = "text/xml", SoapAction = "\"urn:x#GetVolume\"" }, CancellationToken.None);
        Assert.Equal("ok", post.Body);
        Assert.Contains(tv.HttpRequests, r => r.StartsWith("POST /upnp/control/RenderingControl1", StringComparison.Ordinal) && r.Contains("SOAPACTION: \"urn:x#GetVolume\"") && r.EndsWith("<x/>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Key_free_ports_take_no_web_socket_and_never_the_key()
    {
        await using var tv = new FakeWebOsTv(tls: false);
        const string key = "secret/key+0123456789";
        await using var link = TvLink.ForSession(IPAddress.Loopback, [new(tv.Port, false, KeyFree: true)], true, "pin", key, LinkOptions());
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => link.OpenAsync(tv.Port, "/", CancellationToken.None))).Code);
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => link.SendAsync(new TvHttpRequest(tv.Port, "/x?token=" + key), CancellationToken.None))).Code);
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => link.SendAsync(new TvHttpRequest(tv.Port, "/x?token=" + Uri.EscapeDataString(key)), CancellationToken.None))).Code);
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => link.SendAsync(new TvHttpRequest(tv.Port, "/x") { Method = "POST", Body = "k=" + key }, CancellationToken.None))).Code);
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => link.SendAsync(new TvHttpRequest(tv.Port, "/x") { Method = "POST", Body = "x", SoapAction = key }, CancellationToken.None))).Code);
        Assert.Equal(0, tv.Connections);
        Assert.Equal(TvError.Blocked, link.Fault);
    }

    [Fact]
    public async Task Link_limits_channels_rate_and_sizes_and_closes_everything_on_dispose()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        TvPort[] ports = [new(tv.Port, true)];
        var options = LinkOptions() with { MaxChannels = 2, MaxOpens = 4, MaxSendBytes = 1024 };
        var link = TvLink.ForPairing(IPAddress.Loopback, ports, false, options);
        var a = await link.OpenAsync(tv.Port, "/", CancellationToken.None);
        var b = await link.OpenAsync(tv.Port, "/", CancellationToken.None);
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => link.OpenAsync(tv.Port, "/", CancellationToken.None))).Code);
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => a.SendAsync(new string('x', 2048), CancellationToken.None))).Code);

        await b.DisposeAsync();
        var c = await link.OpenAsync(tv.Port, "/", CancellationToken.None);
        await c.DisposeAsync();
        // Five opens within the window: the fifth is refused.
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => link.OpenAsync(tv.Port, "/", CancellationToken.None))).Code);

        await link.DisposeAsync();
        Assert.False(a.Open);
        Assert.Equal(TvError.Unreachable, (await Assert.ThrowsAsync<TvException>(() => link.OpenAsync(tv.Port, "/", CancellationToken.None))).Code);
        Assert.Equal(TvError.Unreachable, (await Assert.ThrowsAsync<TvException>(() => a.SendAsync("{}", CancellationToken.None))).Code);
    }

    // ---- commands through the hosted session ---------------------------------------------------------------------------------

    private static async Task<HostedTvSession> SessionAsync(FakeWebOsTv tv, TestTvProvider? provider = null)
    {
        provider ??= Provider(tv);
        var link = TvLink.ForSession(IPAddress.Loopback, provider.Ports, true, TvText.SpkiPin(tv.Certificate!), FakeWebOsTv.IssuedKey, LinkOptions());
        var inner = await provider.ConnectAsync(link, FakeWebOsTv.IssuedKey, CancellationToken.None);
        return new HostedTvSession(inner, link, TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task Commands_reach_the_tv_and_answers_are_checked_by_the_core()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        await using var s = await SessionAsync(tv);

        await s.VolumeStepAsync(true, CancellationToken.None);
        Assert.Equal(new TvVolume(13, false), await s.GetVolumeAsync(CancellationToken.None));
        await s.SetMuteAsync(true, CancellationToken.None);
        // Clamped by the core before the plugin sees it.
        await s.SetVolumeAsync(150, CancellationToken.None);
        Assert.Equal(new TvVolume(100, true), await s.GetVolumeAsync(CancellationToken.None));

        var inputs = await s.GetInputsAsync(CancellationToken.None);
        Assert.Equal(["HDMI_1", "HDMI_2", "HDMI_3"], inputs.Select(i => i.Id));
        Assert.Equal("PlayStation 5", inputs[0].Label);
        // Shown as text by the UI; the host only strips control characters.
        Assert.Equal("<b>PC</b>", inputs[1].Label);
        Assert.Equal("HDMI_3", inputs[2].Label);
        Assert.Equal("HDMI_2", await s.GetActiveInputAsync(inputs, CancellationToken.None));

        await s.SwitchInputAsync("HDMI_1", CancellationToken.None);
        Assert.Equal("HDMI_1", tv.SwitchedTo);
        Assert.Throws<ArgumentException>(() => { _ = s.SwitchInputAsync("x\"; y", CancellationToken.None); });
        Assert.Equal("HDMI_1", tv.SwitchedTo);

        Assert.Equal("com.webos.app.hdmi2", await s.GetForegroundAppAsync(CancellationToken.None));
        await s.TurnOffAsync(CancellationToken.None);
        Assert.True(tv.TurnedOff);
    }

    [Fact]
    public async Task Refused_command_and_silent_tv_give_codes()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        await using var s = await SessionAsync(tv);
        tv.Refuse.Add("ssap://tv/switchInput");
        Assert.Equal(TvError.Refused, (await Assert.ThrowsAsync<TvException>(() => s.SwitchInputAsync("HDMI_1", CancellationToken.None))).Code);
        tv.Silent.Add("ssap://audio/getVolume");
        Assert.Equal(TvError.Timeout, (await Assert.ThrowsAsync<TvException>(() => s.GetVolumeAsync(CancellationToken.None))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_or_binary_answer_drops_the_connection(bool binary)
    {
        await using var tv = new FakeWebOsTv(tls: true);
        await using var s = await SessionAsync(tv);
        if (binary) tv.Binary = "ssap://tv/getExternalInputList";
        else tv.Oversize = "ssap://tv/getExternalInputList";
        var ex = await Assert.ThrowsAsync<TvException>(() => s.GetInputsAsync(CancellationToken.None));
        Assert.Contains(ex.Code, new[] { TvError.BadAnswer, TvError.Unreachable, TvError.Timeout });
        Assert.False(s.Open);
    }

    [Fact]
    public async Task Forgotten_key_means_unpaired_not_a_new_prompt()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        tv.KnownKeys.Clear();
        var store = new MemoryStore { Stored = Stored("127.0.0.1", true, "stale-key-0123456", TvText.SpkiPin(tv.Certificate!)) };
        await using var c = Controller(Provider(tv), store, _ => { });
        Assert.Equal("unpaired", (await c.RefreshAsync(user: true)).Error);
    }

    // ---- a plugin's session inside the core's bounds ---------------------------------------------------------------------------

    private sealed class ScriptedSession : ITvSession
    {
        public Func<CancellationToken, Task<TvVolume>> Volume { get; set; } = _ => Task.FromResult(new TvVolume(10, false));
        public IReadOnlyList<TvInput>? Inputs { get; set; }
        public string? App { get; set; }
        public string? Active { get; set; }
        public Exception? Throw { get; set; }
        public List<string> Switched { get; } = [];
        public bool Disposed;
        public bool Open => true;
        public Task TurnOffAsync(CancellationToken ct) => Throw is null ? Task.CompletedTask : Task.FromException(Throw);
        public Task<TvVolume> GetVolumeAsync(CancellationToken ct) => Volume(ct);
        public Task VolumeStepAsync(bool up, CancellationToken ct) => Task.CompletedTask;
        public Task SetVolumeAsync(int volume, CancellationToken ct) => Task.CompletedTask;
        public Task SetMuteAsync(bool mute, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<TvInput>> GetInputsAsync(CancellationToken ct) => Task.FromResult(Inputs!);

        public Task SwitchInputAsync(string inputId, CancellationToken ct)
        {
            Switched.Add(inputId);
            return Task.CompletedTask;
        }

        public Task<string?> GetForegroundAppAsync(CancellationToken ct) => Task.FromResult(App);
        public Task<string?> GetActiveInputAsync(IReadOnlyList<TvInput> inputs, CancellationToken ct) => Task.FromResult(Active);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static TvLink IdleLink(int port = 3001) =>
        TvLink.ForSession(IPAddress.Loopback, [new(port, true)], true, "pin", "key-0123456789", LinkOptions());

    [Fact]
    public async Task Hosted_session_cleans_whatever_the_plugin_answers()
    {
        var inner = new ScriptedSession
        {
            Volume = _ => Task.FromResult(new TvVolume(-5, true)),
            Inputs = [.. Enumerable.Range(0, 50).Select(i => new TvInput($"IN_{i}", "x", true)), new("IN_1", "dup", true)],
            App = "../../evil app",
            Active = "NOT_AN_INPUT"
        };
        await using var s = new HostedTvSession(inner, IdleLink(), TimeSpan.FromSeconds(1));
        Assert.Equal(new TvVolume(0, true), await s.GetVolumeAsync(CancellationToken.None));
        var inputs = await s.GetInputsAsync(CancellationToken.None);
        Assert.Equal(TvInput.MaxInputs, inputs.Count);
        Assert.Equal(inputs.Count, inputs.Select(i => i.Id).Distinct().Count());
        Assert.Null(await s.GetForegroundAppAsync(CancellationToken.None));
        Assert.Null(await s.GetActiveInputAsync(inputs, CancellationToken.None));

        inner.Inputs = [new("ok", "\u202eevil\u0000", true), new("bad id", "x", true), null!, new("HDMI_1", null!, false)];
        Assert.Equal([new TvInput("ok", "evil", true), new TvInput("HDMI_1", "HDMI_1", false)], await s.GetInputsAsync(CancellationToken.None));

        Assert.Throws<ArgumentException>(() => { _ = s.SwitchInputAsync("HDMI 1; rm", CancellationToken.None); });
        Assert.Empty(inner.Switched);
    }

    [Fact]
    public async Task Hosted_session_bounds_time_and_maps_what_the_plugin_throws()
    {
        var inner = new ScriptedSession { Volume = _ => new TaskCompletionSource<TvVolume>().Task };
        await using var s = new HostedTvSession(inner, IdleLink(), TimeSpan.FromMilliseconds(300));
        var started = Environment.TickCount64;
        Assert.Equal(TvError.Timeout, (await Assert.ThrowsAsync<TvException>(() => s.GetVolumeAsync(CancellationToken.None))).Code);
        Assert.True(Environment.TickCount64 - started < 30_000);

        inner.Throw = new InvalidOperationException("plugin bug with a path C:\\Users\\x");
        Assert.Equal(TvError.BadAnswer, (await Assert.ThrowsAsync<TvException>(() => s.TurnOffAsync(CancellationToken.None))).Code);
        // Codes that only the core may give are not taken from a plugin.
        inner.Throw = new TvException(TvError.NotLocal);
        Assert.Equal(TvError.BadAnswer, (await Assert.ThrowsAsync<TvException>(() => s.TurnOffAsync(CancellationToken.None))).Code);
        inner.Throw = new TvException(TvError.Refused);
        Assert.Equal(TvError.Refused, (await Assert.ThrowsAsync<TvException>(() => s.TurnOffAsync(CancellationToken.None))).Code);
        // Turning on is not something every plugin can.
        Assert.Equal(TvError.Refused, (await Assert.ThrowsAsync<TvException>(() => s.TurnOnAsync(CancellationToken.None))).Code);

        await s.DisposeAsync();
        Assert.True(inner.Disposed);
    }

    // ---- review follow-ups ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("192.168.1.10", false)]
    [InlineData("192.168.1.11", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("10.1.2.4", true)]
    public void This_pcs_own_address_is_no_tv(string address, bool allowed)
    {
        IReadOnlyList<(IPAddress, IPAddress)> Subnets() => [(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0")), (IPAddress.Parse("10.1.2.3"), IPAddress.Parse("255.0.0.0"))];
        Assert.Equal(allowed, TvAddressPolicy.IsAllowed(IPAddress.Parse(address), false, Subnets));
        Assert.Equal(allowed, TvAddressPolicy.ParseManual(address, false, Subnets) is not null);
        // The mapped form is the same address.
        Assert.Equal(allowed, TvAddressPolicy.IsAllowed(IPAddress.Parse(address).MapToIPv6(), false, Subnets));
        // The range alone (for choosing where discovery is sent from) does not look at this PC.
        Assert.True(TvAddressPolicy.InLocalRange(IPAddress.Parse(address)));
    }

    [Fact]
    public void Key_guard_finds_the_key_in_its_disguises()
    {
        const string key = "secret/key+0123456789";
        var bytes = Encoding.UTF8.GetBytes(key);
        var forms = new List<string>
        {
            key, "x=" + key + "&y", key.ToUpperInvariant(), Uri.EscapeDataString(key), Uri.EscapeDataString(key).ToLowerInvariant(),
            Uri.EscapeDataString(Uri.EscapeDataString(key)), "%73ecret/key+0123456789", @"\u0073ecret/key+0123456789", @"\u0053ECRET/key+0123456789",
            "&#115;ecret/key+0123456789", "&#x73;ecret/key+0123456789", Uri.EscapeDataString("&#115;ecret/key+0123456789"),
            Convert.ToHexString(bytes), Convert.ToHexString(bytes).ToLowerInvariant(), "token=" + Convert.ToHexString(bytes) + "!"
        };
        foreach (var pad in new[] { "", "a", "ab", "abc" })
        {
            var shifted = Convert.ToBase64String(Encoding.UTF8.GetBytes(pad + key + "zz"));
            forms.Add(shifted);
            forms.Add(shifted.Replace('+', '-').Replace('/', '_').TrimEnd('='));
            forms.Add(Uri.EscapeDataString(shifted));
        }
        foreach (var form in forms) Assert.True(KeyGuard.Carries(form, key), form);
        foreach (var innocent in new[] { "", "/api/volume?level=10", "secret/key", "other-key-0123456789", "<x/>", "text/xml; charset=utf-8", "%zz", "&#99999999;", @"\ud800" })
            Assert.False(KeyGuard.Carries(innocent, key), innocent);
    }

    [Fact]
    public void Key_guard_does_not_trip_on_a_short_number_inside_a_longer_one()
    {
        Assert.True(KeyGuard.Carries("pin=12345678", "12345678"));
        Assert.True(KeyGuard.Carries("12345678", "12345678"));
        Assert.True(KeyGuard.Carries("a/12345678/b", "12345678"));
        Assert.False(KeyGuard.Carries("/id/9912345678", "12345678"));
        Assert.False(KeyGuard.Carries("/ts/123456789", "12345678"));
        Assert.False(KeyGuard.Carries("<id>212345678x</id>", "12345678"));
        // From 12 characters on it counts anywhere.
        Assert.True(KeyGuard.Carries("/id/99123456789012", "123456789012"));
    }

    [Fact]
    public async Task Plain_ports_refuse_the_key_in_every_part_the_plugin_may_set()
    {
        await using var tv = new FakeWebOsTv(tls: false);
        const string key = "secret/key+0123456789";
        await using var link = TvLink.ForSession(IPAddress.Loopback, [new(tv.Port, false, KeyFree: true)], true, "pin", key, LinkOptions());
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));
        async Task<TvError> Code(TvHttpRequest r) => (await Assert.ThrowsAsync<TvException>(() => link.SendAsync(r, CancellationToken.None))).Code;
        Assert.Equal(TvError.Blocked, await Code(new TvHttpRequest(tv.Port, "/x") { Method = "POST", Body = "x", ContentType = "text/xml; k=" + key }));
        Assert.Equal(TvError.Blocked, await Code(new TvHttpRequest(tv.Port, "/x") { Method = "POST", Body = "x", ContentType = b64 }));
        Assert.Equal(TvError.Blocked, await Code(new TvHttpRequest(tv.Port, "/x?t=" + Uri.EscapeDataString(Uri.EscapeDataString(key)))));
        Assert.Equal(TvError.Blocked, await Code(new TvHttpRequest(tv.Port, "/x?t=" + Convert.ToHexString(Encoding.UTF8.GetBytes(key)))));
        Assert.Equal(TvError.Blocked, await Code(new TvHttpRequest(tv.Port, "/x") { Method = "POST", Body = "{\"k\":\"\\u0073ecret/key+0123456789\"}" }));
        Assert.Equal(TvError.Blocked, await Code(new TvHttpRequest(tv.Port, "/x") { Method = "POST", Body = "<k>&#115;ecret/key+0123456789</k>" }));
        Assert.Equal(TvError.Blocked, await Code(new TvHttpRequest(tv.Port, "/x") { Method = "POST", Body = "x", SoapAction = b64 }));
        Assert.Equal(0, tv.Connections);
    }

    [Fact]
    public async Task A_tls_failure_forbids_the_plain_port_for_the_rest_of_the_link()
    {
        // Something answers on the TLS port and drops the connection (a fall back being forced); the plain port must stay shut.
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var tlsPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    client.Client.LingerState = new System.Net.Sockets.LingerOption(true, 0);
                }
                catch { return; }
            }
        });
        await using var plain = new FakeWebOsTv(tls: false);
        TvPort[] ports = [new(tlsPort, true), new(plain.Port, false)];
        await using var link = TvLink.ForPairing(IPAddress.Loopback, ports, true, LinkOptions());
        await Assert.ThrowsAnyAsync<TvException>(() => link.OpenAsync(tlsPort, "/", CancellationToken.None));
        Assert.Equal(TvError.Blocked, (await Assert.ThrowsAsync<TvException>(() => link.OpenAsync(plain.Port, "/", CancellationToken.None))).Code);
        Assert.Equal(0, plain.Connections);
        Assert.Equal(TvError.Blocked, link.Fault);
    }

    [Fact]
    public async Task A_closed_tls_port_alone_does_not_forbid_the_plain_one()
    {
        await using var plain = new FakeWebOsTv(tls: false);
        TvPort[] ports = [new(FakeWebOsTv.FreePort(), true), new(plain.Port, false)];
        await using var link = TvLink.ForPairing(IPAddress.Loopback, ports, true, LinkOptions());
        Assert.Equal(TvError.Unreachable, (await Assert.ThrowsAsync<TvException>(() => link.OpenAsync(ports[0].Port, "/", CancellationToken.None))).Code);
        await using var channel = await link.OpenAsync(plain.Port, "/", CancellationToken.None);
        Assert.True(channel.Open);
    }

    [Fact]
    public async Task A_session_with_a_pin_checks_it_on_tls_ports_even_when_plain_is_allowed()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        await using var link = TvLink.ForSession(IPAddress.Loopback, [new(tv.Port, true)], false, Convert.ToBase64String(new byte[32]), "key-0123456789", LinkOptions());
        Assert.Equal(TvError.CertChanged, (await Assert.ThrowsAsync<TvException>(() => link.OpenAsync(tv.Port, "/", CancellationToken.None))).Code);
    }

    private sealed class ShiftingProvider(TestTvProvider inner) : ITvProvider
    {
        public string Id { get; set; } = inner.Id;
        public string Name => inner.Name;
        public IReadOnlyList<TvPort> Ports => inner.Ports;
        public Task<TvPairResult> PairAsync(ITvLink link, Action onPrompt, CancellationToken ct) => inner.PairAsync(link, onPrompt, ct);
        public Task<ITvSession> ConnectAsync(ITvLink link, string key, CancellationToken ct) => inner.ConnectAsync(link, key, ct);
    }

    [Fact]
    public async Task The_stored_provider_id_is_the_one_registered_not_what_the_plugin_says_later()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var inner = Provider(tv);
        var shifting = new ShiftingProvider(inner);
        inner.BeforePair = (_, _) =>
        {
            shifting.Id = "something-else";
            return Task.CompletedTask;
        };
        var store = new MemoryStore();
        await using var c = Controller(shifting, store, _ => { });
        await c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs);
        Assert.Equal(TvProviderRegistry.LgWebOs, store.Stored!.Provider);
    }

    [Fact]
    public void Reserved_kinds_need_a_first_party_plugin_whatever_its_folder_is_called()
    {
        var registry = new TvProviderRegistry();
        foreach (var origin in new[] { PluginOrigin.Local, PluginOrigin.ThirdParty })
        {
            Assert.Equal("reserved id", registry.Register("webostv", new TestTvProvider(3001, 3000), origin));
            Assert.Equal("reserved id", registry.Register("samsungtv", new TestTvProvider(8002, 8001) { Id = TvProviderRegistry.SamsungTizen }, origin));
        }
        Assert.Empty(registry.Available());
        Assert.Null(registry.Register("webostv", new TestTvProvider(3001, 3000), PluginOrigin.FirstParty));
        // Other ids are open to everyone.
        Assert.Null(registry.Register("acme", new TestTvProvider(1, 2) { Id = "acme-tv" }, PluginOrigin.ThirdParty));
    }

    // ---- registry ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Registry_keeps_reserved_ids_for_their_plugins_and_limits_the_rest()
    {
        var registry = new TvProviderRegistry();
        var lg = new TestTvProvider(3001, 3000);
        Assert.Equal("reserved id", registry.Register("evil", lg, PluginOrigin.Local));
        Assert.Equal("reserved id", registry.Register("evil", new TestTvProvider(8002, 8001) { Id = TvProviderRegistry.SamsungTizen }, PluginOrigin.Local));
        Assert.Null(registry.Register("webostv", lg, PluginOrigin.FirstParty));
        Assert.Equal("webostv", registry.PluginOf(TvProviderRegistry.LgWebOs));

        Assert.Null(registry.Register("acme", new TestTvProvider(1, 2) { Id = "acme-tv", Name = "Acme\u0000 TV" }, PluginOrigin.Local));
        Assert.Contains(registry.Available(), p => p.Id == "acme-tv" && p.Name == "Acme TV" && p.Plugin == "acme");
        Assert.Equal("id taken", registry.Register("other", new TestTvProvider(1, 2) { Id = "acme-tv" }, PluginOrigin.Local));
        Assert.Null(registry.Register("acme", new TestTvProvider(1, 2) { Id = "acme-tv2" }, PluginOrigin.Local));
        Assert.Equal("too many", registry.Register("acme", new TestTvProvider(1, 2) { Id = "acme-tv3" }, PluginOrigin.Local));
        // Same id again from the same plugin replaces it.
        Assert.Null(registry.Register("acme", new TestTvProvider(1, 2) { Id = "acme-tv" }, PluginOrigin.Local));

        Assert.Equal("id", registry.Register("x", new TestTvProvider(1, 2) { Id = "Bad Id" }, PluginOrigin.Local));
        Assert.Equal("id", registry.Register("x", new TestTvProvider(1, 2) { Id = "" }, PluginOrigin.Local));
        Assert.Equal("ports", registry.Register("x", new TestTvProvider(1, 2) { Id = "x-tv", Ports = [] }, PluginOrigin.Local));
        Assert.Equal("ports", registry.Register("x", new TestTvProvider(1, 2) { Id = "x-tv", Ports = [new(0, true)] }, PluginOrigin.Local));
        Assert.Equal("provider threw", registry.Register("x", new ThrowingProvider(), PluginOrigin.Local));

        Assert.Equal(["acme-tv", "acme-tv2", TvProviderRegistry.LgWebOs], registry.Available().Select(p => p.Id).Order().ToArray());
        Assert.Null(registry.Find(TvProviderRegistry.LgWebOs, "acme"));
        Assert.NotNull(registry.Find(TvProviderRegistry.LgWebOs, "webostv"));
    }

    [Fact]
    public void Registry_answers_only_for_running_plugins()
    {
        var running = true;
        var registry = new TvProviderRegistry { IsRunning = id => running && id == "webostv" };
        var changes = 0;
        registry.Changed += () => changes++;
        Assert.Null(registry.Register("webostv", new TestTvProvider(3001, 3000), PluginOrigin.FirstParty));
        Assert.Equal(1, changes);
        Assert.NotNull(registry.Find(TvProviderRegistry.LgWebOs, "webostv"));
        running = false;
        Assert.Null(registry.Find(TvProviderRegistry.LgWebOs, "webostv"));
        Assert.Null(registry.PluginOf(TvProviderRegistry.LgWebOs));
        Assert.Empty(registry.Available());
    }

    private sealed class ThrowingProvider : ITvProvider
    {
        public string Id => throw new InvalidOperationException();
        public string Name => "x";
        public IReadOnlyList<TvPort> Ports => [];
        public Task<TvPairResult> PairAsync(ITvLink link, Action onPrompt, CancellationToken ct) => throw new NotSupportedException();
        public Task<ITvSession> ConnectAsync(ITvLink link, string key, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class SessionProvider(ITvSession session) : ITvProvider
    {
        public string Id => TvProviderRegistry.LgWebOs;
        public string Name => "Scripted";
        public IReadOnlyList<TvPort> Ports => [new(3001, true)];
        public Task<TvPairResult> PairAsync(ITvLink link, Action onPrompt, CancellationToken ct) => throw new NotSupportedException();
        public Task<ITvSession> ConnectAsync(ITvLink link, string key, CancellationToken ct) => Task.FromResult(session);
    }

    private sealed class ThrowingDisposeSession : ITvSession
    {
        public bool Disposed;
        public bool Open => !Disposed;
        public Task TurnOffAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<TvVolume> GetVolumeAsync(CancellationToken ct) => Task.FromResult(new TvVolume(10, false));
        public Task VolumeStepAsync(bool up, CancellationToken ct) => Task.CompletedTask;
        public Task SetVolumeAsync(int volume, CancellationToken ct) => Task.CompletedTask;
        public Task SetMuteAsync(bool mute, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<TvInput>> GetInputsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TvInput>>([]);
        public Task SwitchInputAsync(string inputId, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetForegroundAppAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            throw new InvalidOperationException("plugin dispose failed");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_running_session_ends_when_its_plugin_is_forgotten(bool disposeThrows)
    {
        ITvSession session = disposeThrows ? new ThrowingDisposeSession() : new ScriptedSession();
        var registry = Registry(new SessionProvider(session));
        var store = new MemoryStore { Stored = Stored("127.0.0.1", true, FakeWebOsTv.IssuedKey, "pin") };
        await using var c = new TvController(registry, store, _ => { }, null, null, null, LinkOptions(), Timeouts());
        await c.RefreshAsync(user: true);
        bool Gone() => session is ScriptedSession s ? s.Disposed : ((ThrowingDisposeSession)session).Disposed;
        Assert.False(Gone());

        // Another plugin leaving touches nothing.
        registry.Forget("someotherplugin");
        await Task.Delay(200);
        Assert.False(Gone());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        registry.Forget(Plugin);
        while (!Gone() && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
        Assert.True(Gone());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
    }

    // ---- addresses -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.3.4", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("169.254.10.10", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fe80::1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.1.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:192.168.1.2", true)]
    public void Only_local_network_addresses_are_tv_addresses(string address, bool allowed)
    {
        Assert.Equal(allowed, TvAddressPolicy.IsAllowed(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20")]
    [InlineData(" 10.0.0.7 ", "10.0.0.7")]
    [InlineData("fd00::5", "fd00::5")]
    [InlineData("lgtv.local", null)]
    [InlineData("192.168.1", null)]
    [InlineData("3232235796", null)]
    [InlineData("0xC0.0xA8.1.20", null)]
    [InlineData("192.168.001.020", null)]
    [InlineData("8.8.8.8", null)]
    [InlineData("", null)]
    public void Manual_address_is_a_local_ip_literal(string text, string? expected)
    {
        Assert.Equal(expected, TvAddressPolicy.ParseManual(text)?.ToString());
    }

    // ---- Wake-on-LAN ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a8:23:fe:01:02:03", "A8:23:FE:01:02:03")]
    [InlineData("A8-23-FE-01-02-03", "A8:23:FE:01:02:03")]
    [InlineData("A8:23-FE:01:02:03", null)]
    [InlineData("00:00:00:00:00:00", null)]
    [InlineData("FF:FF:FF:FF:FF:FF", null)]
    [InlineData("01:00:5E:00:00:01", null)]
    [InlineData("A823FE010203", null)]
    [InlineData("zz:23:fe:01:02:03", null)]
    public void Mac_addresses_are_strict(string text, string? expected)
    {
        var mac = WakeOnLan.ParseMac(text);
        Assert.Equal(expected, mac is null ? null : WakeOnLan.Format(mac));
    }

    [Fact]
    public void Magic_packet_is_six_ff_and_sixteen_macs()
    {
        var mac = WakeOnLan.ParseMac("A8:23:FE:01:02:03")!;
        var packet = WakeOnLan.MagicPacket(mac);
        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++) Assert.Equal(mac, packet.Skip(6 + i * 6).Take(6));
    }

    [Fact]
    public void Broadcast_only_in_the_tvs_subnet()
    {
        var mask = IPAddress.Parse("255.255.255.0");
        Assert.Equal(IPAddress.Parse("192.168.1.255"), WakeOnLan.SubnetBroadcast(IPAddress.Parse("192.168.1.10"), mask, IPAddress.Parse("192.168.1.20")));
        Assert.Null(WakeOnLan.SubnetBroadcast(IPAddress.Parse("192.168.2.10"), mask, IPAddress.Parse("192.168.1.20")));
        Assert.Null(WakeOnLan.SubnetBroadcast(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.255"), IPAddress.Parse("192.168.1.10")));
    }

    [Fact]
    public void Throttle_allows_one_per_interval()
    {
        long now = 0;
        var t = new Throttle(TimeSpan.FromSeconds(10), () => now);
        Assert.True(t.TryTake());
        now = 9_000;
        Assert.False(t.TryTake());
        now = 10_000;
        Assert.True(t.TryTake());
    }

    // ---- SSDP ----------------------------------------------------------------------------------------------------------------

    private static byte[] Answer(string st, string extra = "") => Encoding.UTF8.GetBytes(
        $"HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLOCATION: http://8.8.8.8:1234/evil.xml\r\nST: {st}\r\n{extra}\r\n");

    [Fact]
    public void Ssdp_answer_gives_the_senders_address_and_never_the_location()
    {
        var device = SsdpDiscovery.ParseAnswer(Answer(SsdpDiscovery.SearchTarget, "DLNADeviceName.lge.com: %5BLG%5D%20webOS%20TV%0A\r\n"), IPAddress.Parse("192.168.1.20"));
        Assert.NotNull(device);
        Assert.Equal("192.168.1.20", device!.Address);
        Assert.Equal("[LG] webOS TV", device.Name);
        Assert.Equal(TvProviderRegistry.LgWebOs, device.Provider);
    }

    [Fact]
    public void Ssdp_answers_from_outside_wrong_target_or_too_large_are_dropped()
    {
        Assert.Null(SsdpDiscovery.ParseAnswer(Answer(SsdpDiscovery.SearchTarget), IPAddress.Parse("8.8.8.8")));
        Assert.Null(SsdpDiscovery.ParseAnswer(Answer("upnp:rootdevice"), IPAddress.Parse("192.168.1.20")));
        Assert.Null(SsdpDiscovery.ParseAnswer(Encoding.UTF8.GetBytes("NOTIFY * HTTP/1.1\r\nNT: " + SsdpDiscovery.SearchTarget + "\r\n\r\n"), IPAddress.Parse("192.168.1.20")));
        Assert.Null(SsdpDiscovery.ParseAnswer(new byte[SsdpDiscovery.MaxAnswerBytes + 1], IPAddress.Parse("192.168.1.20")));
        Assert.Contains("ST: urn:lge-com:service:webos-second-screen:1", Encoding.ASCII.GetString(SsdpDiscovery.SearchMessage()));
    }

    [Fact]
    public void Ssdp_knows_the_kinds_of_tv_of_the_official_plugins()
    {
        var samsung = SsdpDiscovery.ParseAnswer(Answer("urn:samsung.com:device:RemoteControlReceiver:1"), IPAddress.Parse("192.168.1.30"));
        Assert.NotNull(samsung);
        Assert.Equal(TvProviderRegistry.SamsungTizen, samsung!.Provider);
        Assert.Equal("192.168.1.30", samsung.Address);
        Assert.Contains("ST: urn:samsung.com:device:RemoteControlReceiver:1", Encoding.ASCII.GetString(SsdpDiscovery.SearchMessage("urn:samsung.com:device:RemoteControlReceiver:1")));
    }

    // ---- policy and config ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Locked_couch_keeps_volume_and_power_but_not_inputs_or_pairing()
    {
        foreach (var m in new[] { "tvStatus", "tvVolume", "tvVolumeStep", "tvSetVolume", "tvMute", "tvPower" }) Assert.True(TvPolicy.Allowed(m, locked: true), m);
        foreach (var m in new[] { "tvSwitchInput", "tvInputs", "tvPair", "tvForget", "tvDiscover", "tvSetMac", "tvCancelPair" })
        {
            Assert.False(TvPolicy.Allowed(m, locked: true), m);
            Assert.True(TvPolicy.Allowed(m, locked: false), m);
        }
        Assert.False(TvPolicy.Allowed("tvLaunchApp", locked: false));
    }

    [Fact]
    public void Config_keeps_the_key_only_protected()
    {
        var config = new TvConfig();
        var pairing = new TvPairing(TvProviderRegistry.LgWebOs, "192.168.1.20", true, "secret-key-123456", "pin", "A8:23:FE:01:02:03", null, "OLED", "com.webos.app.hdmi1", "webostv");
        config.SetPairing(pairing, s => "dpapi:" + new string(s.Reverse().ToArray()));
        Assert.DoesNotContain("secret-key-123456", JsonSerializer.Serialize(config));
        Assert.Equal(pairing, config.ToPairing(s => new string(s["dpapi:".Length..].Reverse().ToArray())));
        // A key that cannot be decrypted (other Windows user) is no pairing.
        Assert.Null(config.ToPairing(_ => ""));
        config.SetPairing(null, s => s);
        Assert.Null(config.ToPairing(s => s));
        Assert.Equal("", config.ClientKey);
    }

    // ---- controller ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Controller_pairs_controls_and_never_shows_the_key()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var store = new MemoryStore();
        var statuses = new List<TvStatus>();
        await using var c = Controller(Provider(tv), store, s => { lock (statuses) statuses.Add(s); });

        var status = await c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs);
        Assert.True(status.Paired);
        Assert.Equal(FakeWebOsTv.IssuedKey, store.Stored!.ClientKey);
        lock (statuses) Assert.Contains(statuses, s => s.Pairing == "prompt");

        status = await c.VolumeStepAsync(up: false);
        Assert.Equal(11, status.Volume);
        status = await c.MuteAsync(null);
        Assert.True(status.Muted);
        status = await c.RefreshAsync(user: true);
        Assert.Equal("on", status.Power);
        Assert.Equal("HDMI_2", status.Input);

        await Assert.ThrowsAsync<ArgumentException>(() => c.SwitchInputAsync("HDMI_9"));
        status = await c.SwitchInputAsync("HDMI_1");
        Assert.Equal("HDMI_1", tv.SwitchedTo);

        lock (statuses) Assert.DoesNotContain(statuses, s => JsonSerializer.Serialize(s).Contains(FakeWebOsTv.IssuedKey));

        status = await c.ForgetAsync();
        Assert.False(status.Paired);
        Assert.Null(store.Stored);
    }

    [Fact]
    public async Task Controller_wakes_only_with_a_mac_and_rate_limited()
    {
        var store = new MemoryStore
        {
            Stored = new TvPairing(TvProviderRegistry.LgWebOs, "192.168.1.20", true, "key-0123456789", "pin", null, null, null)
        };
        var wakes = new List<(byte[] Mac, IPAddress To)>();
        long now = 1_000_000;
        await using var c = Controller(Provider(null), store, _ => { }, () => now, (mac, to, _) =>
        {
            wakes.Add((mac, to));
            return Task.FromResult(true);
        });

        Assert.Equal(TvError.NoWake, (await Assert.ThrowsAsync<TvException>(() => c.PowerAsync(true))).Code);
        c.SetMac("a8:23:fe:01:02:03");
        Assert.Equal("A8:23:FE:01:02:03", store.Stored!.Mac);
        Assert.Throws<ArgumentException>(() => c.SetMac("ff:ff:ff:ff:ff:ff"));

        var status = await c.PowerAsync(true);
        Assert.Equal("waking", status.Power);
        await c.PowerAsync(true);
        Assert.Single(wakes);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), wakes[0].To);
        now += 11_000;
        await c.PowerAsync(true);
        Assert.Equal(2, wakes.Count);

        // Following CouchHUD: off by default.
        await c.FollowWakeAsync(new TvFollow(false, false));
        Assert.Equal(2, wakes.Count);
    }

    [Fact]
    public async Task Tv_off_with_pc_only_while_the_pc_input_is_in_front()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var store = new MemoryStore();
        await using var c = Controller(Provider(tv), store, _ => { });
        await c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs);
        Assert.Equal("com.webos.app.hdmi2", store.Stored!.PcApp);

        await c.FollowSleepAsync(new TvFollow(false, false));
        Assert.False(tv.TurnedOff);

        tv.ForegroundApp = "netflix";
        await c.FollowSleepAsync(new TvFollow(false, true));
        Assert.False(tv.TurnedOff);

        tv.ForegroundApp = "com.webos.app.hdmi2";
        await c.FollowSleepAsync(new TvFollow(false, true));
        Assert.True(tv.TurnedOff);
    }

    [Fact]
    public async Task Unreachable_tv_is_off_and_automatic_reads_back_off()
    {
        var store = new MemoryStore
        {
            Stored = new TvPairing(TvProviderRegistry.LgWebOs, "127.0.0.1", false, "key-0123456789", null, null, null, null)
        };
        long now = 5_000_000;
        await using var c = Controller(Provider(null), store, _ => { }, () => now);
        var status = await c.RefreshAsync(user: true);
        Assert.Equal("off", status.Power);
        // A port nobody listens on usually refuses at once; on a busy machine the connect may run into the timeout instead.
        Assert.Contains(status.Error, new[] { "unreachable", "timeout" });

        // Within the backoff an automatic read does not connect again.
        await using var tv = new FakeWebOsTv(tls: false);
        var status2 = await c.RefreshAsync(user: false);
        Assert.Equal("off", status2.Power);
    }

    [Fact]
    public async Task Cert_change_keeps_its_code_in_the_status()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var store = new MemoryStore
        {
            Stored = new TvPairing(TvProviderRegistry.LgWebOs, "127.0.0.1", true, FakeWebOsTv.IssuedKey, Convert.ToBase64String(new byte[32]), null, null, null)
        };
        await using var c = Controller(Provider(tv), store, _ => { });
        var status = await c.RefreshAsync(user: true);
        Assert.Equal("certChanged", status.Error);
        Assert.Equal(TvError.CertChanged, (await Assert.ThrowsAsync<TvException>(() => c.VolumeStepAsync(true))).Code);
    }

    // ---- review fixes: downgrade, cert replaced, busy, retry spacing, network/broadcast, host gates -----------------------------

    [Fact]
    public async Task Controller_offers_plain_only_on_request_and_never_for_a_tv_that_had_tls()
    {
        await using var legacy = new FakeWebOsTv(tls: false);
        var store = new MemoryStore();
        await using var c = Controller(Provider(null, legacy), store, _ => { });

        Assert.Equal(TvError.PlainOnly, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs))).Code);
        Assert.Equal("plainOnly", c.Status().Error);
        Assert.Null(store.Stored);

        // This address was paired over TLS once (also after "Remove TV"): no plain pairing, nothing sent.
        store.Seen.Add("127.0.0.1");
        Assert.Equal(TvError.Downgrade, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs, allowPlain: true))).Code);
        Assert.Equal(0, legacy.Connections);

        // Same TV under another address, known by its MAC from the ARP cache: refused before connecting.
        store.Seen.Clear();
        store.Seen.Add("A8:23:FE:01:02:03");
        Assert.Equal(TvError.Downgrade, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs, _ => "a8-23-fe-01-02-03", allowPlain: true))).Code);
        Assert.Equal(0, legacy.Connections);

        // ... and when only the TV itself tells the MAC: the plain pairing is not kept.
        Assert.Equal(TvError.Downgrade, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs, allowPlain: true))).Code);
        Assert.Null(store.Stored);

        // A TV that never had TLS: fine after the confirmation.
        store.Seen.Clear();
        var status = await c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs, allowPlain: true);
        Assert.True(status.Paired);
        Assert.False(status.Secure);
        Assert.Empty(store.Seen);
    }

    [Fact]
    public async Task Tls_pairing_is_remembered_and_blocks_a_later_plain_one()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var store = new MemoryStore();
        await using var c = Controller(Provider(tv), store, _ => { });
        await c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs);
        Assert.Contains("127.0.0.1", store.Seen);
        Assert.Contains("A8:23:FE:01:02:03", store.Seen);
        var forgotten = await c.ForgetAsync();
        Assert.Contains("127.0.0.1", store.Seen);
        // The page learns only that there was an encrypted pairing, not where.
        Assert.True(forgotten.SecureBefore);
        Assert.DoesNotContain("127.0.0.1", JsonSerializer.Serialize(forgotten));
        Assert.Equal(TvError.Downgrade, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs, allowPlain: true))).Code);
    }

    [Fact]
    public async Task Repairing_with_another_certificate_is_flagged()
    {
        var store = new MemoryStore();
        await using (var first = new FakeWebOsTv(tls: true))
        {
            await using var c1 = Controller(Provider(first), store, _ => { });
            Assert.False((await c1.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs)).CertReplaced);
        }
        await using var other = new FakeWebOsTv(tls: true);
        await using var c = Controller(Provider(other), store, _ => { });
        Assert.Equal("certChanged", (await c.RefreshAsync(user: true)).Error);
        var status = await c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs);
        Assert.True(status.CertReplaced);
        Assert.Equal(TvText.SpkiPin(other.Certificate!), store.Stored!.CertPin);
        // Same certificate again: no flag.
        Assert.False((await c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs)).CertReplaced);
    }

    [Fact]
    public async Task A_second_pairing_while_one_runs_is_busy()
    {
        await using var tv = new FakeWebOsTv(tls: true) { PromptAnswer = "none" };
        var prompt = new TaskCompletionSource();
        await using var c = Controller(Provider(tv, promptMs: 5000), new MemoryStore(), s => { if (s.Pairing == "prompt") prompt.TrySetResult(); });
        var first = c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs);
        await prompt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TvError.Busy, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, TvProviderRegistry.LgWebOs))).Code);
        c.CancelPair();
        Assert.Equal(TvError.Denied, (await Assert.ThrowsAsync<TvException>(() => first)).Code);
    }

    [Fact]
    public async Task After_a_failed_connection_no_new_one_for_two_seconds_whoever_asks()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var store = new MemoryStore
        {
            Stored = new TvPairing(TvProviderRegistry.LgWebOs, "127.0.0.1", true, FakeWebOsTv.IssuedKey, Convert.ToBase64String(new byte[32]), null, null, null)
        };
        long now = 7_000_000;
        await using var c = Controller(Provider(tv), store, _ => { }, () => now);
        Assert.Equal("certChanged", (await c.RefreshAsync(user: true)).Error);
        var tries = tv.Connections;
        // Locked reads (tvVolume), steps, a press: none of them connects again within the spacing.
        await c.RefreshAsync(user: true);
        Assert.Equal(TvError.CertChanged, (await Assert.ThrowsAsync<TvException>(() => c.VolumeStepAsync(true))).Code);
        Assert.Equal(tries, tv.Connections);
        now += 2_100;
        await c.RefreshAsync(user: true);
        Assert.Equal(tries + 1, tv.Connections);
    }

    [Theory]
    [InlineData("192.168.1.255", "192.168.1.10", "255.255.255.0", false)]
    [InlineData("192.168.1.0", "192.168.1.10", "255.255.255.0", false)]
    [InlineData("192.168.1.20", "192.168.1.10", "255.255.255.0", true)]
    [InlineData("192.168.0.255", "192.168.1.10", "255.255.254.0", true)]
    [InlineData("192.168.1.255", "192.168.1.10", "255.255.254.0", false)]
    [InlineData("10.0.0.255", "10.0.0.2", "255.255.0.0", true)]
    [InlineData("10.0.255.255", "10.0.0.2", "255.255.0.0", false)]
    [InlineData("192.168.5.255", "192.168.1.10", "255.255.255.0", false)]
    [InlineData("192.168.5.0", "192.168.1.10", "255.255.255.0", false)]
    [InlineData("192.168.5.7", "192.168.1.10", "255.255.255.0", true)]
    public void Network_and_broadcast_addresses_are_no_tv(string address, string local, string mask, bool allowed)
    {
        IReadOnlyList<(IPAddress, IPAddress)> Subnets() => [(IPAddress.Parse(local), IPAddress.Parse(mask))];
        Assert.Equal(allowed, TvAddressPolicy.IsAllowed(IPAddress.Parse(address), false, Subnets));
    }

    [Fact]
    public void Host_gate_refuses_locked_calls_itself()
    {
        Assert.Throws<CouchHud.Core.Guard.LockedException>(() => TvPolicy.Admit("tvSwitchInput", locked: true, "locked"));
        Assert.Throws<CouchHud.Core.Guard.LockedException>(() => TvPolicy.Admit("tvPair", locked: true, "locked"));
        Assert.Throws<CouchHud.Core.Guard.LockedException>(() => TvPolicy.Admit("tvInputs", locked: true, "locked"));
        TvPolicy.Admit("tvVolumeStep", locked: true, "locked");
        TvPolicy.Admit("tvSwitchInput", locked: false, "locked");
        Assert.Throws<InvalidOperationException>(() => TvPolicy.Admit("tvExec", locked: false, "locked"));
    }

    [Fact]
    public void Input_switch_checks_the_id_before_the_release_and_binds_the_release_to_it()
    {
        var released = new List<string>();
        Assert.Throws<ArgumentException>(() => TvPolicy.AdmitInputSwitch("HDMI 1; rm", released.Add));
        Assert.Throws<ArgumentException>(() => TvPolicy.AdmitInputSwitch(null, released.Add));
        Assert.Empty(released);
        Assert.Equal("HDMI_2", TvPolicy.AdmitInputSwitch("HDMI_2", released.Add));
        Assert.Equal(["HDMI_2"], released);
        // A refused release (no parents' code) stops the switch.
        Assert.Throws<InvalidOperationException>(() => TvPolicy.AdmitInputSwitch("HDMI_3", _ => throw new InvalidOperationException("code")));
    }
}
