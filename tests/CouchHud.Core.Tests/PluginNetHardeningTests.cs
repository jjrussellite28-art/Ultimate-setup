using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CouchHud.Core.Net;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// The follow-up to SDK 5 networking: declared services and hosts are part of the signed statement, the trust list and the "rights changed"
/// comparison; third-party plugins with services may not open connections of their own; ports and https for hosts; the address check for IPv6 forms
/// that carry an IPv4 address; the response cap; credentials on redirects; the settings seed.
/// </summary>
public sealed class PluginNetHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-nethard-" + Guid.NewGuid().ToString("N"));

    public PluginNetHardeningTests() => Directory.CreateDirectory(_dir);

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

    private static PackageService Pkg(string id, params string[] hosts) => new(id, hosts);

    private static JsonObject StatementJson(JsonNode? services = null)
    {
        var o = new JsonObject
        {
            ["id"] = "demo", ["folder"] = "Demo", ["version"] = "1.0.0", ["sha256"] = new string('a', 64), ["sdk"] = 5,
            ["capabilities"] = new JsonArray("network:internet"), ["publisherKey"] = new string('b', 64), ["license"] = "MIT"
        };
        if (services is not null) o["services"] = services;
        return o;
    }

    private static PackageStatement Parse(JsonObject o) => PackageStatements.Parse(Encoding.UTF8.GetBytes(o.ToJsonString()));

    private static JsonArray One(string id, params string[] hosts) => new(new JsonObject { ["id"] = id, ["hosts"] = new JsonArray(hosts.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()) });

    private static string RepoRoot()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CouchHud.slnx"))) root = Path.GetDirectoryName(root)!;
        return root;
    }

    // =====================================================================
    // M1: services in the statement, the trust list and the comparison

    [Fact]
    public void A_statement_carries_its_services_and_an_old_one_without_them_declares_none()
    {
        Assert.Empty(Parse(StatementJson()).Services);
        var s = Parse(StatementJson(One("api", "API.example.com", "api.example.com", "*")));
        var svc = Assert.Single(s.Services);
        Assert.Equal("api", svc.Id);
        Assert.Equal(["api.example.com", "*"], svc.Hosts);
    }

    [Theory]
    [InlineData("""[{"id":"api","hosts":["127.0.0.1"]}]""")]
    [InlineData("""[{"id":"api","hosts":["*.example.com"]}]""")]
    [InlineData("""[{"id":"api","hosts":[]}]""")]
    [InlineData("""[{"id":"api"}]""")]
    [InlineData("""[{"id":"../x","hosts":["a.example.com"]}]""")]
    [InlineData("""[{"id":"a","hosts":["a.example.com"]},{"id":"a","hosts":["b.example.com"]}]""")]
    [InlineData("""[{"id":"a","hosts":["a.example.com"],"price":1}]""")]
    [InlineData("""[{"id":"a","hosts":["a.example.com"]},{"id":"b","hosts":["a.example.com"]},{"id":"c","hosts":["a.example.com"]},{"id":"d","hosts":["a.example.com"]},{"id":"e","hosts":["a.example.com"]}]""")]
    [InlineData("""{"id":"a"}""")]
    public void A_bad_services_field_makes_the_statement_invalid(string json) =>
        Assert.Throws<CatalogException>(() => Parse(StatementJson(JsonNode.Parse(json))));

    [Fact]
    public void The_trust_list_records_the_services_and_a_list_without_them_stays_as_it_was()
    {
        var key = new byte[32];
        var trust = new PluginTrust(_dir, key);
        var hash = new string('c', 64);
        trust.Record("Old", hash, new PluginTrustEntry { Hash = "", Origin = PluginOrigin.ThirdParty, PluginId = "old", Capabilities = ["network:internet"] });
        // Nothing about services is written when there are none, so a list from before keeps its signature.
        Assert.DoesNotContain("Services", File.ReadAllText(Path.Combine(_dir, "plugin-trust.json")));
        Assert.Null(new PluginTrust(_dir, key).Entry("Old")!.Services);

        trust.Record("New", hash, new PluginTrustEntry { Hash = "", Origin = PluginOrigin.ThirdParty, PluginId = "new", Services = [Pkg("api", "a.example.com")] });
        var reread = new PluginTrust(_dir, key);
        Assert.False(reread.IsFaulty);
        var back = reread.Entry("New")!;
        Assert.Equal("api", Assert.Single(back.Services!).Id);
        Assert.Equal(["a.example.com"], back.Services![0].Hosts);
    }

    [Fact]
    public void The_trust_entry_made_from_a_verified_statement_has_the_services()
    {
        var verified = new VerifiedStatement(Parse(StatementJson(One("api", "a.example.com"))), PluginOrigin.ThirdParty, "ff");
        Assert.Equal("api", Assert.Single(verified.ToTrustEntry().Services!).Id);
        Assert.Null(new VerifiedStatement(Parse(StatementJson()), PluginOrigin.ThirdParty, "ff").ToTrustEntry().Services);
    }

    [Fact]
    public void Widened_lists_a_new_service_a_new_host_and_a_new_star_but_not_what_was_approved()
    {
        PackageService[] have = [Pkg("api", "a.example.com", "b.example.com")];
        Assert.Empty(PluginNetRules.Widened(have, [Pkg("api", "b.example.com")]));
        Assert.Empty(PluginNetRules.Widened(have, []));
        Assert.Empty(PluginNetRules.Widened(null, []));
        Assert.Equal(["api: c.example.com"], PluginNetRules.Widened(have, [Pkg("api", "a.example.com", "c.example.com")]));
        Assert.Equal(["api: *"], PluginNetRules.Widened(have, [Pkg("api", "*")]));
        Assert.Equal(["other: a.example.com"], PluginNetRules.Widened(have, [Pkg("other", "a.example.com")]));
        // Nothing recorded counts as none approved.
        Assert.Equal(["api: a.example.com"], PluginNetRules.Widened(null, [Pkg("api", "a.example.com")]));
        // "*" already approved covers every host of that service, but not another service.
        Assert.Empty(PluginNetRules.Widened([Pkg("feeds", "*")], [Pkg("feeds", "x.example.com", "*")]));
        Assert.NotEmpty(PluginNetRules.Widened([Pkg("feeds", "*")], [Pkg("other", "x.example.com")]));
    }

    [Fact]
    public void An_update_that_widens_the_hosts_is_handled_like_an_update_with_a_new_capability()
    {
        var installed = new PluginTrustEntry { Hash = "", Origin = PluginOrigin.ThirdParty, Capabilities = ["network:internet"], Services = [Pkg("api", "a.example.com")] };
        Assert.False(CapabilityCompare.AddsRights(installed, Parse(StatementJson(One("api", "a.example.com")))));
        Assert.True(CapabilityCompare.AddsRights(installed, Parse(StatementJson(One("api", "*")))));
        Assert.True(CapabilityCompare.AddsRights(installed with { Services = null }, Parse(StatementJson(One("api", "a.example.com")))));
        Assert.False(CapabilityCompare.AddsRights(installed with { Services = null }, Parse(StatementJson())));
    }

    [Fact]
    public void The_automatic_update_asks_for_permission_when_the_services_widen()
    {
        static AutoUpdateCandidate Plugin(IReadOnlyList<PackageService>? have, IReadOnlyList<PackageService>? offered) => new(CatalogKind.Plugin, false, false, true, PluginOrigin.FirstParty, PluginOrigin.FirstParty,
            "1.0.0", "1.0.0", "1.1.0", "0.6.0", SdkVersion.Current, new SemVer(0, 7, 0, ""), ["network:internet"], ["network:internet"],
            InstalledServices: have, OfferedServices: offered);

        Assert.Equal(AutoUpdateVerdict.Apply, AutoUpdateRules.Decide(Plugin([Pkg("api", "a.example.com")], [Pkg("api", "a.example.com")])));
        Assert.Equal(AutoUpdateVerdict.NeedsPermission, AutoUpdateRules.Decide(Plugin([Pkg("api", "a.example.com")], [Pkg("api", "a.example.com", "b.example.com")])));
        Assert.Equal(AutoUpdateVerdict.NeedsPermission, AutoUpdateRules.Decide(Plugin(null, [Pkg("api", "*")])));
    }

    private static PluginManifest Manifest(params PluginServiceManifest[] services) =>
        new(null, null, null, null, null, Sdk: 5, Id: "demo", Capabilities: ["network:internet"], Services: services);

    [Fact]
    public void The_plugin_json_in_the_package_may_not_declare_more_services_than_the_statement()
    {
        var statement = Parse(StatementJson(One("api", "a.example.com")));
        Assert.Null(AutoUpdateRules.ManifestProblem(Manifest(new PluginServiceManifest("api", null, ["a.example.com"])), statement));
        Assert.NotNull(AutoUpdateRules.ManifestProblem(Manifest(new PluginServiceManifest("api", null, ["a.example.com", "evil.example.net"])), statement));
        Assert.NotNull(AutoUpdateRules.ManifestProblem(Manifest(new PluginServiceManifest("api", null, ["*"])), statement));
        Assert.NotNull(AutoUpdateRules.ManifestProblem(Manifest(new PluginServiceManifest("more", null, ["a.example.com"])), statement));
    }

    [Fact]
    public void A_third_party_plugin_json_that_changed_after_the_approval_gets_no_more_hosts_than_were_recorded()
    {
        var manifest = Manifest(new PluginServiceManifest("api", null, ["a.example.com", "evil.example.net"]));
        var approved = new PluginTrustEntry { Hash = "", Origin = PluginOrigin.ThirdParty, Services = [Pkg("api", "a.example.com")] };

        var got = PluginManager.DeclaredServices("Demo", PluginOrigin.ThirdParty, manifest, approved);
        Assert.Equal(["a.example.com"], Assert.Single(got).Hosts);
        Assert.Empty(PluginManager.DeclaredServices("Demo", PluginOrigin.ThirdParty, manifest, approved with { Services = null }));
        Assert.Empty(PluginManager.DeclaredServices("Demo", PluginOrigin.ThirdParty, manifest, null));
        // CouchHUD's own plugins are as their signed package says.
        Assert.Equal(2, PluginManager.DeclaredServices("Demo", PluginOrigin.FirstParty, manifest, null).Single().Hosts.Count);
    }

    [Fact]
    public void The_install_gate_uses_the_services_in_the_comparison_and_the_approval_shows_the_hosts()
    {
        var root = RepoRoot();
        var catalog = File.ReadAllText(Path.Combine(root, "src", "CouchHud.Core", "Plugins", "CatalogService.cs"));
        Assert.Contains("AddsRights(before, statement.Statement)", catalog);
        Assert.Contains("declaresServices: statement?.Statement.Services.Count > 0", catalog);
        Assert.Contains("AddsRights(", File.ReadAllText(Path.Combine(root, "src", "CouchHud.Shell", "Services", "CatalogManager.cs")));
        Assert.Contains("e.services", File.ReadAllText(Path.Combine(root, "ui", "views", "plugins.js")));
    }

    // =====================================================================
    // M2 / L4: no connection of its own for third-party plugins with services

    private static readonly IReadOnlyList<LintFinding> Findings = PluginLint.Scan(File.ReadAllBytes(typeof(PluginNetHardeningTests).Assembly.Location), "Tests.dll");

    [Theory]
    [InlineData("HttpClient..ctor")]
    [InlineData("System.Net.Http.HttpClientHandler")]
    [InlineData("System.Net.Http.SocketsHttpHandler")]
    [InlineData("System.Net.Sockets.Socket")]
    [InlineData("System.Net.Sockets.TcpClient")]
    [InlineData("System.Net.WebSockets.ClientWebSocket")]
    [InlineData("System.Net.WebClient")]
    [InlineData("System.Net.WebRequest")]
    [InlineData("System.Net.Dns")]
    [InlineData("System.Net.NetworkInformation.Ping")]
    [InlineData("System.Net.Mail.SmtpClient")]
    public void A_connection_of_its_own_is_found_as_network_and_refused_for_a_third_party_that_declares_services(string detail)
    {
        Assert.Contains(Findings, f => f.Capability == "network" && f.Detail == detail);
        var found = Findings.Where(f => f.Detail == detail).ToList();
        string[] caps = [PluginCapabilities.NetworkInternet];

        // Third party, SDK 5, services declared: refused, whatever it declared.
        Assert.Contains(PluginLint.Evaluate(found, PluginOrigin.ThirdParty, caps, 5, declaresServices: true), f => f.Capability == PluginLint.RawNetwork);
        // Without services nothing changes: the network capability covers it.
        Assert.Empty(PluginLint.Evaluate(found, PluginOrigin.ThirdParty, caps, 5, declaresServices: false));
        Assert.Empty(PluginLint.Evaluate(found, PluginOrigin.ThirdParty, caps, 4, declaresServices: true));
        // CouchHUD's own plugins and local ones are not touched by it.
        Assert.Empty(PluginLint.Evaluate(found, PluginOrigin.FirstParty, caps, 5, declaresServices: true));
        Assert.Empty(PluginLint.Evaluate(found, PluginOrigin.Local, caps, 5, declaresServices: true));
        // And the old rule stays: without a network capability it is undeclared.
        Assert.NotEmpty(PluginLint.Evaluate(found, PluginOrigin.ThirdParty, [], 5, declaresServices: false));
    }

    [Fact]
    public void WinHttpHandler_is_a_network_type_and_a_listener_is_no_connection_of_its_own()
    {
        var rules = typeof(PluginLint).GetField("TypeRules", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var text = string.Join("\n", ((System.Collections.IEnumerable)rules).Cast<object>().Select(r => r.ToString()));
        Assert.Contains("WinHttpHandler", text);
        Assert.Contains("HttpListener", text);
    }

    [Fact]
    public void Using_the_hosts_client_is_not_a_raw_connection()
    {
        Assert.DoesNotContain(PluginLint.Scan(File.ReadAllBytes(typeof(IPluginContext).Assembly.Location), "sdk.dll"), f => f.Capability == PluginLint.RawNetwork);
    }

    // =====================================================================
    // L3 / L10: ports and https

    [Theory]
    [InlineData("https://news.example.org/rss", true)]
    [InlineData("http://news.example.org/rss", true)]
    [InlineData("http://news.example.org:80/rss", true)]
    [InlineData("https://news.example.org:443/rss", true)]
    [InlineData("https://news.example.org:8443/rss", false)]
    [InlineData("http://news.example.org:8080/rss", false)]
    [InlineData("http://news.example.org:22/rss", false)]
    public void The_any_host_service_takes_only_ports_80_and_443(string url, bool allowed) =>
        Assert.Equal(allowed, PluginNetRules.AllowsUri(new PluginService("feeds", "F", "F", ["*"]), new Uri(url)));

    [Fact]
    public void A_named_host_is_reached_over_https_only()
    {
        var service = new PluginService("api", "A", "A", ["api.example.com"]);
        Assert.True(PluginNetRules.AllowsUri(service, new Uri("https://api.example.com/x")));
        Assert.False(PluginNetRules.AllowsUri(service, new Uri("http://api.example.com/x")));
    }

    // =====================================================================
    // L5 / L6: addresses

    [Theory]
    [InlineData("64:ff9b::7f00:1")]       // NAT64 of 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")]    // NAT64 of 169.254.169.254
    [InlineData("64:ff9b::c0a8:101")]     // NAT64 of 192.168.1.1
    [InlineData("64:ff9b::a00:1")]        // NAT64 of 10.0.0.1
    [InlineData("64:ff9b:1::1")]          // NAT64 local use
    [InlineData("2002:7f00:1::")]         // 6to4 of 127.0.0.1
    [InlineData("2002:c0a8:101::1")]      // 6to4 of 192.168.1.1
    [InlineData("2002:a9fe:a9fe::1")]     // 6to4 of 169.254.169.254
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")] // Teredo, client 192.0.2.45 (documentation)
    [InlineData("2001:0:808:808:0:0:80ff:fe9a")]         // Teredo, public server, client 127.0.1.101
    [InlineData("::7f00:1")]              // IPv4-compatible 127.0.0.1
    [InlineData("::c0a8:101")]            // IPv4-compatible 192.168.1.1
    [InlineData("::a9fe:a9fe")]           // IPv4-compatible 169.254.169.254
    [InlineData("2001:db8::1")]
    [InlineData("100::1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.0.2.255")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("192.88.99.1")]
    [InlineData("240.0.0.1")]
    [InlineData("250.1.2.3")]
    [InlineData("255.255.255.255")]
    [InlineData("::ffff:192.0.2.1")]
    public void Addresses_that_are_not_public_also_when_wrapped_in_an_IPv6_address(string address) =>
        Assert.False(SafeNet.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("64:ff9b::808:808")]      // NAT64 of 8.8.8.8
    [InlineData("2002:808:808::1")]       // 6to4 of 8.8.8.8
    [InlineData("::808:808")]             // IPv4-compatible 8.8.8.8
    [InlineData("2606:4700::1111")]
    [InlineData("198.51.101.1")]
    [InlineData("192.0.3.1")]
    [InlineData("203.0.114.1")]
    public void Public_addresses_stay_public_also_when_wrapped(string address) => Assert.True(SafeNet.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void A_teredo_address_is_public_only_when_server_and_client_are()
    {
        // Server 8.8.8.8 (bytes 4..7), client 8.8.4.4 stored inverted in the last four bytes.
        Assert.True(SafeNet.IsPublic(IPAddress.Parse("2001:0:808:808:0:0:f7f7:fbfb")));
        // Client 192.168.1.1 inverted.
        Assert.False(SafeNet.IsPublic(IPAddress.Parse("2001:0:808:808:0:0:3f57:fefe")));
        // Server 10.0.0.1.
        Assert.False(SafeNet.IsPublic(IPAddress.Parse("2001:0:a00:1:0:0:f7f7:fbfb")));
    }

    [Theory]
    [InlineData("http://[64:ff9b::7f00:1]:9/x")]
    [InlineData("http://[64:ff9b::a9fe:a9fe]:9/latest/meta-data")]
    [InlineData("http://[2002:c0a8:101::1]:9/x")]
    [InlineData("http://[::7f00:1]:9/x")]
    [InlineData("http://[::ffff:10.0.0.1]:9/x")]
    [InlineData("http://192.0.2.1:9/x")]
    public async Task The_safe_handler_refuses_to_connect_to_wrapped_private_addresses(string url)
    {
        using var handler = SafeNet.CreateHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));
        Assert.Contains("public", ex.Message);
    }

    [Fact]
    public void The_safe_handler_has_a_connect_timeout_and_a_connection_lifetime()
    {
        using var handler = SafeNet.CreateHandler();
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionLifetime);
    }

    // =====================================================================
    // L7: the response cap

    private sealed class Canned(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer());
    }

    private sealed class Endless : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'a', offset, count);
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpClient Limited(long max, Func<HttpResponseMessage> answer) => new(new PluginResponseLimitHandler(max, new Canned(answer)));

    [Fact]
    public async Task An_answer_that_announces_too_much_is_refused_and_one_within_the_limit_is_read()
    {
        using var big = Limited(1000, () => new HttpResponseMessage { Content = new ByteArrayContent(new byte[2000]) });
        await Assert.ThrowsAsync<HttpRequestException>(() => big.GetStringAsync("https://a.example.com/"));

        using var small = Limited(1000, () => new HttpResponseMessage { Content = new ByteArrayContent(new byte[1000]) });
        Assert.Equal(1000, (await small.GetByteArrayAsync("https://a.example.com/")).Length);
    }

    [Fact]
    public async Task An_endless_stream_is_cut_off_when_read_as_a_stream_or_buffered()
    {
        using var client = Limited(10_000, () => new HttpResponseMessage { Content = new StreamContent(new Endless()) });

        using (var response = await client.GetAsync("https://a.example.com/", HttpCompletionOption.ResponseHeadersRead))
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            var total = 0;
            var buffer = new byte[4096];
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
            {
                while (true) total += await stream.ReadAsync(buffer);
            });
            Assert.True(total <= 10_000 + 4096);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetStringAsync("https://a.example.com/"));
    }

    [Fact]
    public void The_cap_leaves_room_for_the_news_plugins_4_MB_and_is_set_on_the_client()
    {
        Assert.True(PluginManager.MaxResponseBytes >= 8L * 1024 * 1024);
        Assert.True(PluginManager.MaxResponseBytes > 4 * 1024 * 1024);
        Assert.Contains("MaxResponseContentBufferSize = MaxResponseBytes", File.ReadAllText(Path.Combine(RepoRoot(), "src", "CouchHud.Core", "Plugins", "PluginSlots.cs")));
    }

    // =====================================================================
    // Redirects: what is sent where

    private sealed class Table(Dictionary<string, Func<HttpResponseMessage>> answers) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(request);
            return Task.FromResult(answers.TryGetValue(request.RequestUri!.ToString(), out var a) ? a() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static Func<HttpResponseMessage> Redirect(string to) => () =>
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to, UriKind.Absolute);
        return r;
    };

    [Fact]
    public async Task Proxy_authorization_and_google_api_keys_do_not_follow_a_redirect_to_another_host()
    {
        var net = new Table(new() { ["https://a.example.com/x"] = Redirect("https://b.example.com/y"), ["https://b.example.com/y"] = () => new HttpResponseMessage(HttpStatusCode.OK) });
        using var client = new HttpClient(new PluginHttpHandler("t", new PluginService("api", "A", "A", ["a.example.com", "b.example.com"]), () => true, net));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://a.example.com/x");
        request.Headers.TryAddWithoutValidation("Proxy-Authorization", "Basic abc");
        request.Headers.TryAddWithoutValidation("x-goog-api-key", "AIza-secret");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer t");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(2, net.Sent.Count);
        Assert.True(net.Sent[0].Headers.Contains("Proxy-Authorization"));
        var other = net.Sent[1];
        Assert.False(other.Headers.Contains("Proxy-Authorization"));
        Assert.False(other.Headers.Contains("x-goog-api-key"));
        Assert.False(other.Headers.Contains("Authorization"));
        Assert.True(other.Headers.Contains("Accept"));
    }

    [Fact]
    public async Task A_redirect_back_to_the_origin_host_gets_its_own_credentials_again_but_the_detour_never_does()
    {
        var net = new Table(new()
        {
            ["https://a.example.com/x"] = Redirect("https://b.example.com/y"),
            ["https://b.example.com/y"] = Redirect("https://a.example.com/z"),
            ["https://a.example.com/z"] = () => new HttpResponseMessage(HttpStatusCode.OK)
        });
        using var client = new HttpClient(new PluginHttpHandler("t", new PluginService("api", "A", "A", ["a.example.com", "b.example.com"]), () => true, net));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://a.example.com/x");
        request.Headers.TryAddWithoutValidation("x-api-key", "sk-secret");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, net.Sent.Count);
        Assert.False(net.Sent[1].Headers.Contains("x-api-key"));
        Assert.True(net.Sent[2].Headers.Contains("x-api-key"));
    }

    // =====================================================================
    // A third party cannot register a chat provider

    [Fact]
    public void A_third_party_plugin_cannot_declare_or_be_granted_the_chat_provider_capability()
    {
        Assert.NotNull(PluginPolicy.DeclaredMessage(PluginOrigin.ThirdParty, [PluginCapabilities.ChatProvider]));
        Assert.NotNull(PluginPolicy.DeclaredMessage(PluginOrigin.Local, [PluginCapabilities.ChatProvider]));
        Assert.Null(PluginPolicy.DeclaredMessage(PluginOrigin.FirstParty, [PluginCapabilities.ChatProvider]));
        // Even a recorded capability list that contains it grants nothing to a third party.
        Assert.DoesNotContain(PluginCapabilities.ChatProvider, PluginPolicy.Granted(PluginOrigin.ThirdParty, [PluginCapabilities.ChatProvider], 5, [PluginCapabilities.ChatProvider]));
        Assert.Contains(PluginCapabilities.ChatProvider, PluginPolicy.Granted(PluginOrigin.FirstParty, [PluginCapabilities.ChatProvider], 5, [PluginCapabilities.ChatProvider]));
        // The registration is fenced by exactly that capability.
        Assert.Equal(PluginCapabilities.ChatProvider, PluginPolicy.ContextMembers["RegisterChatProvider"]);
    }

    // =====================================================================
    // L9: a legacy key and a settings file that exist both

    private const string Enc = SettingsStore.SecretPrefix;

    private static SettingsStore Store(string file, string? legacy, Action applied) =>
        new(file, () => [], legacy is null ? null : new Dictionary<string, string?> { ["apiKey"] = Enc + legacy }, applied, p => Enc + p, s => s[Enc.Length..]);

    [Fact]
    public void A_legacy_key_moves_into_an_existing_settings_file_that_has_none_and_the_legacy_copy_may_go()
    {
        var file = Path.Combine(_dir, "a.json");
        File.WriteAllText(file, """{"model":"m1"}""");
        var applied = 0;
        var store = Store(file, "legacy", () => applied++);

        Assert.Equal("legacy", store.Get("apiKey"));
        Assert.Equal("m1", store.Get("model"));
        Assert.Equal(1, applied);
        Assert.Contains("legacy", File.ReadAllText(file));
    }

    [Fact]
    public void An_existing_plugin_secret_is_never_overwritten_by_the_legacy_key()
    {
        var file = Path.Combine(_dir, "b.json");
        File.WriteAllText(file, $$"""{"apiKey":"{{Enc}}own"}""");
        var applied = 0;
        var store = Store(file, "legacy", () => applied++);

        Assert.Equal("own", store.Get("apiKey"));
        Assert.Equal(1, applied);
        Assert.DoesNotContain("legacy", File.ReadAllText(file));
    }

    [Fact]
    public void A_seed_that_cannot_be_written_does_not_crash_the_plugin()
    {
        // A directory where the file should be: writing it is an access error, not an IO error.
        var file = Path.Combine(_dir, "c.json");
        Directory.CreateDirectory(file);
        var applied = 0;
        var store = Store(file, "legacy", () => applied++);

        Assert.Equal("legacy", store.Get("apiKey"));
        Assert.Equal(0, applied);
    }

    // =====================================================================
    // L8 / L11: the shell's side (source pins; the shell cannot be started here)

    [Fact]
    public void Calls_into_the_weather_and_news_slots_run_with_a_timeout_and_news_pictures_follow_offline()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CouchHud.Shell", "HudIntegrations.cs"));
        Assert.Contains("SlotCall(w.Plugin, () => w.Source.Place", text);
        Assert.Contains("SlotCall(n.Plugin, () => n.Source.Feeds", text);
        Assert.Contains("w.Source.SetPlace(place); return 0;", text);
        Assert.Contains("n.Source.AddFeed(feedName, clean); return 0;", text);
        Assert.Contains("n.Source.RemoveFeed(feedUrl); return 0;", text);
        Assert.Contains("var pictures = !net.Offline", text);
    }
}

/// <summary>Never called: gives the lint something to find in this assembly's metadata.</summary>
internal static class NetworkBait
{
#pragma warning disable SYSLIB0014, SYSLIB0021, CA1416, CS0618
    internal static void Uses()
    {
        _ = new HttpClient();
        _ = new HttpClientHandler();
        _ = new SocketsHttpHandler();
        _ = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        _ = new System.Net.Sockets.TcpClient();
        _ = new System.Net.WebSockets.ClientWebSocket();
        _ = new System.Net.WebClient();
        _ = System.Net.WebRequest.Create("https://example.invalid/");
        _ = System.Net.Dns.GetHostAddresses("example.invalid");
        _ = new System.Net.NetworkInformation.Ping();
        _ = new System.Net.Mail.SmtpClient();
    }
}
