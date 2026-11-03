using System.Text;
using System.Text.Json.Nodes;
using CouchHud.Core.Config;
using CouchHud.Core.Net;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// The additive SDK 5 members of 0.7: <see cref="IPluginContext.ServiceAllowed"/> with the "lan" host token, the read-only host state
/// (capability "host.state"), <see cref="IPluginEvents.HostStopping"/>, <see cref="IPluginSettings.SettingChanged"/> and the reserved names.
/// </summary>
public sealed class PluginSdkHostTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("couchhud-sdkhost-").FullName;
    private readonly AppConfig _config = new();
    private readonly List<string> _switchedOff = [];
    private bool _locked;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static PluginService Svc(string id, params string[] hosts) => new(id, id, id, hosts);

    private PluginManager NewManager() => new([(Path.Combine(_dir, "plugins"), true)], _dir, _ => true)
    {
        Net = new NetPolicy(() => _config),
        OfflineNow = () => _config.Offline,
        ServiceAllowed = (plugin, service) => !_switchedOff.Contains(PluginService.Key(plugin, service)),
        IsLocked = () => _locked
    };

    private (PluginManager Manager, IPluginContext Context) Load(string id, PluginOrigin origin, IEnumerable<string> caps, params PluginService[] services)
    {
        var m = NewManager();
        var grant = new PluginManager.PluginGrant(id, origin, new HashSet<string>(caps), 5, services);
        return (m, m.ContextFor(id, grant, markLoaded: true));
    }

    private static T Wait<T>(Func<T> read, Func<T, bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        var value = read();
        while (!done(value) && DateTime.UtcNow < until)
        {
            Thread.Sleep(10);
            value = read();
        }
        return value;
    }

    // =====================================================================
    // "lan": a host token that is checked like "*" is, but is its own approval

    [Fact]
    public void The_lan_token_passes_the_manifest_and_the_statement_checks()
    {
        var parsed = PluginNetRules.Parse([new PluginServiceManifest("hub", LocalizedText.Plain("Hub"), ["lan"])], 5);
        Assert.Equal(["lan"], parsed.Single().Hosts);
        Assert.True(parsed.Single().Lan);
        Assert.False(parsed.Single().AnyHost);

        var json = new JsonObject
        {
            ["id"] = "demo", ["folder"] = "Demo", ["version"] = "1.0.0", ["sha256"] = new string('a', 64), ["sdk"] = 5,
            ["capabilities"] = new JsonArray("network:lan"), ["publisherKey"] = new string('b', 64), ["license"] = "MIT",
            ["services"] = new JsonArray(new JsonObject { ["id"] = "hub", ["hosts"] = new JsonArray("lan") })
        };
        var statement = PackageStatements.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Equal(["lan"], statement.Services.Single().Hosts);
        // Anything else that is no DNS name stays refused: "lan2", "local", "a b".
        foreach (var bad in new[] { "lan2", "local", "a b", "LAN-" })
        {
            json["services"] = new JsonArray(new JsonObject { ["id"] = "hub", ["hosts"] = new JsonArray(bad) });
            Assert.ThrowsAny<Exception>(() => PackageStatements.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
        }
    }

    [Fact]
    public void The_lan_token_is_a_new_permission_and_the_any_host_star_does_not_cover_it()
    {
        // A service that gains "lan" is widened, and so is a new service that is only "lan".
        Assert.Equal(["hub: lan"], PluginNetRules.Widened([new PackageService("hub", ["api.example.com"])], [new PackageService("hub", ["api.example.com", "lan"])]));
        Assert.Equal(["hub: lan"], PluginNetRules.Widened(null, [new PackageService("hub", ["lan"])]));
        // "*" covers named hosts but not the home network.
        Assert.Equal(["hub: lan"], PluginNetRules.Widened([new PackageService("hub", ["*"])], [new PackageService("hub", ["*", "lan"])]));
        Assert.Empty(PluginNetRules.Widened([new PackageService("hub", ["*"])], [new PackageService("hub", ["api.example.com"])]));
        // "lan" does not cover "*".
        Assert.Equal(["hub: *"], PluginNetRules.Widened([new PackageService("hub", ["lan"])], [new PackageService("hub", ["lan", "*"])]));
        // Staying inside the approval is not a widening.
        Assert.Empty(PluginNetRules.Widened([new PackageService("hub", ["lan"])], [new PackageService("hub", ["lan"])]));
    }

    [Fact]
    public void An_update_that_adds_lan_counts_as_asking_for_more_rights()
    {
        var installed = new PluginTrustEntry { Hash = "", Capabilities = [PluginCapabilities.NetworkLan], Services = [new PackageService("hub", ["api.example.com"])] };
        var json = new JsonObject
        {
            ["id"] = "demo", ["folder"] = "Demo", ["version"] = "1.1.0", ["sha256"] = new string('a', 64), ["sdk"] = 5,
            ["capabilities"] = new JsonArray("network:lan"), ["publisherKey"] = new string('b', 64), ["license"] = "MIT",
            ["services"] = new JsonArray(new JsonObject { ["id"] = "hub", ["hosts"] = new JsonArray("api.example.com", "lan") })
        };
        Assert.True(CapabilityCompare.AddsRights(installed, PackageStatements.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()))));
        // The new capability does too.
        var withHost = new PluginTrustEntry { Hash = "", Capabilities = [PluginCapabilities.NetworkLan] };
        json["capabilities"] = new JsonArray("network:lan", "host.state");
        json["services"] = new JsonArray();
        Assert.True(CapabilityCompare.AddsRights(withHost, PackageStatements.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()))));
    }

    [Fact]
    public void A_third_party_plugin_gets_lan_only_when_the_approval_had_it()
    {
        var declared = PluginNetRules.Parse([new PluginServiceManifest("hub", LocalizedText.Plain("Hub"), ["api.example.com", "lan"])], 5);
        var withoutLan = PluginNetRules.Restrict(declared, [new PackageService("hub", ["api.example.com"])]);
        Assert.Equal(["api.example.com"], withoutLan.Single().Hosts);
        // "*" in the approval is no approval for the home network.
        var starOnly = PluginNetRules.Restrict(declared, [new PackageService("hub", ["*"])]);
        Assert.Equal(["api.example.com"], starOnly.Single().Hosts);
        var withLan = PluginNetRules.Restrict(declared, [new PackageService("hub", ["api.example.com", "lan"])]);
        Assert.Equal(["api.example.com", "lan"], withLan.Single().Hosts);
    }

    [Fact]
    public void Http_stays_closed_for_the_home_network()
    {
        var lanOnly = Svc("hub", "lan");
        Assert.False(PluginNetRules.AllowsUri(lanOnly, new Uri("https://lan/")));
        Assert.False(PluginNetRules.AllowsUri(lanOnly, new Uri("http://192.168.1.5/")));
        Assert.False(PluginNetRules.AllowsUri(Svc("hub", "api.example.com", "lan"), new Uri("https://lan/x")));
        Assert.False(PluginNetRules.AllowsUri(Svc("hub", "*", "lan"), new Uri("https://lan/x")));
        // The named host still works.
        Assert.True(PluginNetRules.AllowsUri(Svc("hub", "api.example.com", "lan"), new Uri("https://api.example.com/x")));
        // A service that names only the home network gets no client at all.
        var (_, ctx) = Load("hub", PluginOrigin.FirstParty, [PluginCapabilities.NetworkLan, PluginCapabilities.NetworkInternet], lanOnly);
        Assert.Null(ctx.Http("hub"));
        var (_, mixed) = Load("hub", PluginOrigin.FirstParty, [PluginCapabilities.NetworkInternet], Svc("hub", "api.example.com", "lan"));
        Assert.NotNull(mixed.Http("hub"));
    }

    // =====================================================================
    // ServiceAllowed

    [Fact]
    public void A_declared_service_is_allowed_when_offline_is_off_and_its_switch_is_on()
    {
        var (_, ctx) = Load("hub", PluginOrigin.FirstParty, [PluginCapabilities.NetworkLan], Svc("hub", "lan"));
        Assert.True(ctx.ServiceAllowed("hub"));
    }

    [Fact]
    public void Offline_switches_a_service_off_also_for_a_plugin_that_keeps_running_on_the_home_network()
    {
        var (m, ctx) = Load("hub", PluginOrigin.FirstParty, [PluginCapabilities.NetworkLan], Svc("hub", "lan"));
        _config.Offline = true;
        Assert.False(m.IsPaused("hub"));
        Assert.False(ctx.ServiceAllowed("hub"));
        _config.Offline = false;
        Assert.True(ctx.ServiceAllowed("hub"));
    }

    [Fact]
    public void The_switch_of_the_service_in_the_privacy_tab_decides()
    {
        var (_, ctx) = Load("hub", PluginOrigin.FirstParty, [PluginCapabilities.NetworkLan], Svc("hub", "lan"), Svc("other", "lan"));
        _switchedOff.Add(PluginService.Key("hub", "hub"));
        Assert.False(ctx.ServiceAllowed("hub"));
        Assert.True(ctx.ServiceAllowed("other"));
    }

    [Fact]
    public void An_unknown_or_undeclared_service_is_never_allowed()
    {
        var (_, ctx) = Load("hub", PluginOrigin.FirstParty, [PluginCapabilities.NetworkLan], Svc("hub", "lan"));
        Assert.False(ctx.ServiceAllowed("nope"));
        Assert.False(ctx.ServiceAllowed(""));
        Assert.False(ctx.ServiceAllowed(null!));
        Assert.False(ctx.ServiceAllowed("HUB"));
    }

    [Fact]
    public void A_plugin_without_a_network_capability_is_not_allowed_anything()
    {
        var (_, ctx) = Load("hub", PluginOrigin.FirstParty, [PluginCapabilities.Pages], Svc("hub", "lan"));
        Assert.False(ctx.ServiceAllowed("hub"));
    }

    [Fact]
    public void Without_a_net_policy_or_with_a_failing_switch_the_answer_is_false()
    {
        var m = new PluginManager([(Path.Combine(_dir, "plugins"), true)], _dir, _ => true);
        var grant = new PluginManager.PluginGrant("hub", PluginOrigin.FirstParty, new HashSet<string> { PluginCapabilities.NetworkLan }, 5, [Svc("hub", "lan")]);
        Assert.False(m.ContextFor("hub", grant, true).ServiceAllowed("hub"));
        m.Net = new NetPolicy(() => _config);
        m.ServiceAllowed = (_, _) => throw new InvalidOperationException("boom");
        Assert.False(m.ContextFor("hub", grant, true).ServiceAllowed("hub"));
    }

    [Fact]
    public void A_plugin_that_is_not_loaded_is_not_allowed()
    {
        var m = NewManager();
        Assert.False(m.ServiceAllowedFor("ghost", "hub"));
    }

    [Fact]
    public void A_paused_plugin_that_may_reach_the_internet_is_not_allowed()
    {
        var (m, ctx) = Load("hub", PluginOrigin.FirstParty, [PluginCapabilities.NetworkInternet], Svc("hub", "api.example.com"));
        Assert.True(ctx.ServiceAllowed("hub"));
        _config.Offline = true;
        Assert.True(m.IsPaused("hub"));
        Assert.False(ctx.ServiceAllowed("hub"));
    }

    [Fact]
    public void The_privacy_tab_lists_the_services_of_a_home_network_plugin_too()
    {
        var (m, _) = Load("hub", PluginOrigin.FirstParty, [PluginCapabilities.NetworkLan], Svc("hub", "lan"));
        Assert.Single(m.Services);
        Assert.True(m.Services[0].Service.Lan);
        var (m2, _) = Load("quiet", PluginOrigin.FirstParty, [PluginCapabilities.Pages], Svc("hub", "lan"));
        Assert.Empty(m2.Services);
    }

    [Fact]
    public void ServiceAllowedChanged_is_raised_when_the_answer_changes_and_not_otherwise()
    {
        var (m, ctx) = Load("hub", PluginOrigin.FirstParty, [PluginCapabilities.NetworkLan], Svc("hub", "lan"));
        var seen = new List<bool>();
        ctx.ServiceAllowedChanged += v => { lock (seen) seen.Add(v); };

        m.NotifyServicesChanged();
        Thread.Sleep(100);
        Assert.Empty(seen);

        _config.Offline = true;
        m.NotifyServicesChanged();
        Assert.Equal([false], Wait(() => { lock (seen) return seen.ToList(); }, l => l.Count >= 1));

        m.NotifyServicesChanged();
        Thread.Sleep(100);
        lock (seen) Assert.Single(seen);

        _config.Offline = false;
        _switchedOff.Add(PluginService.Key("hub", "hub"));
        m.NotifyServicesChanged();
        Thread.Sleep(100);
        lock (seen) Assert.Single(seen);

        _switchedOff.Clear();
        m.NotifyServicesChanged();
        Assert.Equal([false, true], Wait(() => { lock (seen) return seen.ToList(); }, l => l.Count >= 2));
    }

    [Fact]
    public void A_failing_ServiceAllowedChanged_handler_does_not_stop_the_others()
    {
        var (m, ctx) = Load("hub", PluginOrigin.FirstParty, [PluginCapabilities.NetworkLan], Svc("hub", "lan"));
        var heard = 0;
        ctx.ServiceAllowedChanged += _ => throw new InvalidOperationException("plugin bug");
        ctx.ServiceAllowedChanged += _ => Interlocked.Increment(ref heard);
        _config.Offline = true;
        m.NotifyServicesChanged();
        Assert.Equal(1, Wait(() => Volatile.Read(ref heard), n => n >= 1));
    }

    [Fact]
    public void The_new_members_are_in_the_gate_table_with_the_right_capability()
    {
        Assert.Null(PluginPolicy.ContextMembers["ServiceAllowed"]);
        Assert.Equal(PluginCapabilities.HostState, PluginPolicy.ContextMembers["Host"]);
        Assert.Contains(PluginCapabilities.HostState, PluginCapabilities.All);
        Assert.Null(PluginPolicy.CheckDeclared(PluginOrigin.ThirdParty, [PluginCapabilities.HostState]));
    }

    // =====================================================================
    // host.state

    [Fact]
    public void Without_the_capability_Host_is_null()
    {
        var (_, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.Pages]);
        Assert.Null(ctx.Host);
    }

    [Fact]
    public void The_state_is_readable_at_once_and_follows_the_shell()
    {
        var (m, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.HostState]);
        var shell = true;
        var pc = false;
        PluginAppInfo? app = new("steam:1", "Game");
        m.HostStateReader = () => new PluginHostSnapshot("0.7.0", shell ? PluginShellMode.Shell : PluginShellMode.App, pc, app);
        var host = ctx.Host!;
        Assert.NotNull(host);
        Assert.Equal("0.7.0", host.Version);
        Assert.Equal(PluginShellMode.Shell, host.Mode);
        Assert.False(host.PcMode);
        Assert.Equal(new PluginAppInfo("steam:1", "Game"), host.RunningApp);
        Assert.Null(host.SleepTimerEnds);
        Assert.False(host.Locked);

        shell = false;
        pc = true;
        app = null;
        _locked = true;
        m.NotifyHostChanged(); // the readings are kept for a quarter of a second; a change drops them
        Assert.Equal(PluginShellMode.App, host.Mode);
        Assert.True(host.PcMode);
        Assert.Null(host.RunningApp);
        Assert.True(host.Locked);
    }

    [Fact]
    public void Without_a_reader_or_with_a_failing_one_the_state_is_neutral()
    {
        var (m, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.HostState]);
        var host = ctx.Host!;
        Assert.False(string.IsNullOrEmpty(host.Version));
        Assert.Equal(PluginShellMode.App, host.Mode);
        Assert.False(host.PcMode);
        Assert.Null(host.RunningApp);
        m.HostStateReader = () => throw new InvalidOperationException("boom");
        m.NotifyHostChanged();
        Assert.Equal(PluginShellMode.App, host.Mode);
        Assert.Null(host.RunningApp);
    }

    private static LibraryItem Item(string id, string name, ItemType type = ItemType.Game, bool hidden = false) =>
        new() { Id = id, Source = "steam", Name = name, Type = type, Hidden = hidden, Launch = new LaunchTarget(LaunchKind.Url, "steam://run/1") };

    [Fact]
    public void The_running_app_follows_the_rules_for_hidden_entries_and_switched_off_areas()
    {
        var on = AreaSet.AllOn;
        Assert.Equal(new PluginAppInfo("steam:1", "Game"), PluginHostRules.RunningApp(Item("steam:1", "Game"), on));
        Assert.Null(PluginHostRules.RunningApp(null, on));
        // Hidden by the user: not described.
        Assert.Null(PluginHostRules.RunningApp(Item("steam:1", "Game", hidden: true), on));
        // Gaming switched off: a game is not described, an app is.
        var noGames = new AreaSet([FeatureAreas.Games]);
        Assert.Null(PluginHostRules.RunningApp(Item("steam:1", "Game"), noGames));
        Assert.NotNull(PluginHostRules.RunningApp(Item("app:1", "Tool", ItemType.App), noGames));
        var noApps = new AreaSet([FeatureAreas.Apps]);
        Assert.Null(PluginHostRules.RunningApp(Item("app:1", "Tool", ItemType.App), noApps));
    }

    [Fact]
    public void The_app_name_is_cleaned_like_any_plugin_text()
    {
        var info = PluginHostRules.Clean("steam:1", "  Ga\u0000me\r\n‮" + new string('x', 300));
        Assert.NotNull(info);
        Assert.DoesNotContain(info!.Name, c => char.IsControl(c) || c == '‮');
        Assert.True(info.Name.Length <= PluginAppInfo.MaxName);
        Assert.Null(PluginHostRules.Clean("steam:1", "\u0000\u0007 "));
        Assert.Null(PluginHostRules.Clean("", "Game"));
        // Even a reader that hands over an unclean name is cleaned by the host.
        var (m, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.HostState]);
        m.HostStateReader = () => new PluginHostSnapshot("0.7.0", PluginShellMode.App, false, new PluginAppInfo("a\nb", "Na\u0000me"));
        m.NotifyHostChanged();
        Assert.Equal(new PluginAppInfo("ab", "Name"), ctx.Host!.RunningApp);
    }

    [Fact]
    public void The_sleep_timer_shows_while_it_runs_and_not_after_it_ended()
    {
        var (m, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.HostState]);
        var end = DateTimeOffset.UtcNow.AddMinutes(30);
        m.SleepTimerEnds = end;
        Assert.Equal(end, ctx.Host!.SleepTimerEnds);
        m.SleepTimerEnds = DateTimeOffset.UtcNow.AddSeconds(-5);
        Assert.Null(ctx.Host.SleepTimerEnds);
        m.SleepTimerEnds = null;
        Assert.Null(ctx.Host.SleepTimerEnds);
    }

    [Fact]
    public void Changes_that_come_close_together_are_merged_into_one_call()
    {
        var (m, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.HostState]);
        m.HostChangeInterval = TimeSpan.FromMilliseconds(120);
        var calls = 0;
        ctx.Host!.Changed += () => Interlocked.Increment(ref calls);
        for (var i = 0; i < 20; i++) m.NotifyHostChanged();
        Assert.Equal(1, Wait(() => Volatile.Read(ref calls), n => n >= 1));
        Thread.Sleep(300);
        Assert.Equal(1, Volatile.Read(ref calls));
        // The next burst after the gap is one more call.
        for (var i = 0; i < 5; i++) m.NotifyHostChanged();
        Assert.Equal(2, Wait(() => Volatile.Read(ref calls), n => n >= 2));
        Thread.Sleep(300);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public void Only_plugins_with_the_capability_hear_about_changes_and_a_paused_one_does_not()
    {
        var m = NewManager();
        m.HostChangeInterval = TimeSpan.FromMilliseconds(20);
        var with = m.ContextFor("a", new PluginManager.PluginGrant("a", PluginOrigin.FirstParty, new HashSet<string> { PluginCapabilities.HostState }, 5), true);
        var without = m.ContextFor("b", new PluginManager.PluginGrant("b", PluginOrigin.FirstParty, new HashSet<string> { PluginCapabilities.Pages }, 5), true);
        var paused = m.ContextFor("c", new PluginManager.PluginGrant("c", PluginOrigin.FirstParty, new HashSet<string> { PluginCapabilities.HostState, PluginCapabilities.NetworkInternet }, 5), true);
        var heard = new int[3];
        with.Host!.Changed += () => Interlocked.Increment(ref heard[0]);
        paused.Host!.Changed += () => Interlocked.Increment(ref heard[2]);
        Assert.Null(without.Host);
        _config.Offline = true;
        m.NotifyHostChanged();
        Assert.Equal(1, Wait(() => Volatile.Read(ref heard[0]), n => n >= 1));
        Thread.Sleep(150);
        Assert.Equal(0, Volatile.Read(ref heard[2]));
    }

    [Fact]
    public void A_failing_Changed_handler_does_not_stop_the_others()
    {
        var (m, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.HostState]);
        m.HostChangeInterval = TimeSpan.FromMilliseconds(10);
        var heard = 0;
        ctx.Host!.Changed += () => throw new InvalidOperationException("plugin bug");
        ctx.Host.Changed += () => Interlocked.Increment(ref heard);
        m.NotifyHostChanged();
        Assert.Equal(1, Wait(() => Volatile.Read(ref heard), n => n >= 1));
    }

    [Fact]
    public void The_host_state_has_no_member_that_hands_out_a_handle_or_does_something()
    {
        // Read only: properties and one event, no methods (a new member must be a conscious decision and changes this test).
        var members = typeof(IPluginHostState).GetMembers().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "Changed", "Locked", "Mode", "PcMode", "RunningApp", "SleepTimerEnds", "Version", "add_Changed", "get_Locked", "get_Mode", "get_PcMode", "get_RunningApp", "get_SleepTimerEnds", "get_Version", "remove_Changed" }.OrderBy(n => n, StringComparer.Ordinal), members);
        Assert.All(typeof(IPluginHostState).GetProperties(), p => Assert.False(p.CanWrite));
        Assert.DoesNotContain(typeof(PluginAppInfo).GetProperties(), p => p.PropertyType == typeof(object) || p.PropertyType == typeof(IntPtr) || p.PropertyType == typeof(System.Diagnostics.Process));
    }

    // =====================================================================
    // HostStopping

    [Fact]
    public void HostStopping_reaches_the_handlers_of_a_plugin_and_is_raised_once()
    {
        var (m, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.Pages]);
        var heard = 0;
        ctx.Events.HostStopping += () => Interlocked.Increment(ref heard);
        Assert.Equal(0, m.RaiseHostStopping());
        Assert.Equal(1, Volatile.Read(ref heard));
        Assert.Equal(0, m.RaiseHostStopping());
        Assert.Equal(1, Volatile.Read(ref heard));
    }

    [Fact]
    public void A_handler_that_hangs_does_not_block_the_shutdown_beyond_the_time_limit()
    {
        var (m, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.Pages]);
        var release = new ManualResetEventSlim();
        var quick = 0;
        ctx.Events.HostStopping += () => release.Wait(TimeSpan.FromSeconds(30));
        ctx.Events.HostStopping += () => Interlocked.Increment(ref quick);
        ctx.Events.HostStopping += () => throw new InvalidOperationException("plugin bug");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var late = m.Events.RaiseHostStopping(TimeSpan.FromSeconds(2));
        watch.Stop();
        release.Set();
        Assert.Equal(1, late);
        Assert.Equal(1, Volatile.Read(ref quick));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void The_default_time_limit_is_two_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), PluginEvents.StoppingTimeout);
    }

    [Fact]
    public void A_paused_plugin_is_not_told_that_the_host_stops()
    {
        var (m, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.NetworkInternet]);
        var heard = 0;
        ctx.Events.HostStopping += () => Interlocked.Increment(ref heard);
        _config.Offline = true;
        m.RaiseHostStopping();
        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref heard));
    }

    [Fact]
    public void An_unsubscribed_stopping_handler_is_not_called()
    {
        var (m, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.Pages]);
        var heard = 0;
        void Handler() => Interlocked.Increment(ref heard);
        ctx.Events.HostStopping += Handler;
        ctx.Events.HostStopping -= Handler;
        m.RaiseHostStopping();
        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref heard));
    }

    // =====================================================================
    // SettingChanged

    [Fact]
    public void SettingChanged_names_the_key_when_a_value_really_changes()
    {
        var (_, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.Pages]);
        var keys = new List<string>();
        ctx.Settings.SettingChanged += k => { lock (keys) keys.Add(k); };
        ctx.Settings.Set("mode", "a");
        Assert.Equal(["mode"], Wait(() => { lock (keys) return keys.ToList(); }, l => l.Count >= 1));
        ctx.Settings.Set("mode", "a");
        Thread.Sleep(100);
        lock (keys) Assert.Single(keys);
        ctx.Settings.Set("mode", "b");
        ctx.Settings.Set("mode", null);
        Assert.Equal(["mode", "mode", "mode"], Wait(() => { lock (keys) return keys.ToList(); }, l => l.Count >= 3));
    }

    [Fact]
    public void SettingChanged_is_per_plugin_and_never_carries_a_value()
    {
        var (m, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.Pages]);
        var other = m.ContextFor("q", new PluginManager.PluginGrant("q", PluginOrigin.FirstParty, new HashSet<string> { PluginCapabilities.Pages }, 5), true);
        var heardP = 0;
        var heardQ = 0;
        ctx.Settings.SettingChanged += _ => Interlocked.Increment(ref heardP);
        other.Settings.SettingChanged += _ => Interlocked.Increment(ref heardQ);
        other.Settings.Set("k", "v");
        Assert.Equal(1, Wait(() => Volatile.Read(ref heardQ), n => n >= 1));
        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref heardP));
        Assert.Equal(typeof(Action<string>), typeof(IPluginSettings).GetEvent(nameof(IPluginSettings.SettingChanged))!.EventHandlerType);
    }

    [Fact]
    public void A_secret_that_changes_raises_it_and_the_same_secret_does_not()
    {
        var m = NewManager();
        m.ProtectSecret = (_, plain) => "dpapi:" + Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + plain;
        m.UnprotectSecret = (_, stored) => stored[(stored.IndexOf(':') + 25)..];
        var ctx = m.ContextFor("p", new PluginManager.PluginGrant("p", PluginOrigin.FirstParty, new HashSet<string> { PluginCapabilities.Pages }, 5), true);
        var heard = 0;
        ctx.Settings.SettingChanged += _ => Interlocked.Increment(ref heard);
        ctx.Settings.SetSecret("token", "abc");
        Assert.Equal(1, Wait(() => Volatile.Read(ref heard), n => n >= 1));
        ctx.Settings.SetSecret("token", "abc");
        Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref heard));
    }

    [Fact]
    public void A_refused_value_raises_nothing()
    {
        var (_, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.Pages]);
        var heard = 0;
        ctx.Settings.SettingChanged += _ => Interlocked.Increment(ref heard);
        ctx.Settings.Set("k", new string('x', SettingsStore.MaxValueLength + 1));
        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref heard));
    }

    // =====================================================================
    // Reserved names

    [Theory]
    [InlineData("streaming")]
    [InlineData("fancontrol")]
    [InlineData("webostv")]
    [InlineData("samsungtv")]
    [InlineData("weather")]
    [InlineData("news")]
    [InlineData("anthropic")]
    [InlineData("openai")]
    [InlineData("Str3aming")]
    [InlineData("fan-control")]
    [InlineData("OpenAI")]
    public void The_new_own_names_are_reserved_for_third_parties(string name) => Assert.True(PluginNames.IsReserved(name));

    [Theory]
    [InlineData("my-weather-widget")]
    [InlineData("game-streaming-helper")]
    [InlineData("fan-controller")]
    [InlineData("newsflash")]
    [InlineData("my-news")]
    public void Plain_words_are_free_inside_a_longer_name(string name) => Assert.False(PluginNames.IsReserved(name));

    [Theory]
    [InlineData("weather")]
    [InlineData("weather-plus")]
    [InlineData("Weather_Plus")]
    [InlineData("fan-control-pro")]
    [InlineData("news.reader")]
    [InlineData("streaming 2")]
    [InlineData("vpn-pro")]
    [InlineData("openai-bridge")]
    [InlineData("my-anthropic-tool")]
    [InlineData("samsungtv-remote")]
    public void Plain_words_at_the_start_and_brands_anywhere_are_taken(string name) => Assert.True(PluginNames.IsReserved(name));

    [Theory]
    [InlineData("0penAI")]
    [InlineData("anthr0pic")]
    [InlineData("\u0430nthropic")] // Cyrillic a
    [InlineData("openai\u200B")]   // zero width space
    [InlineData("\uFF4Fpenai")]    // fullwidth o
    public void Look_alikes_of_a_brand_are_taken(string name) => Assert.True(PluginNames.IsReserved(name));

    [Fact]
    public void The_display_name_is_checked_against_the_brands_only()
    {
        Assert.True(PluginNames.NameImitatesBrand("Home Assistant"));
        Assert.True(PluginNames.NameImitatesBrand("My OpenAI Bridge"));
        Assert.True(PluginNames.NameImitatesBrand("An\u00ADthr\u00F3pic"));
        Assert.False(PluginNames.NameImitatesBrand("My Weather Widget"));
        Assert.False(PluginNames.NameImitatesBrand("Streaming Helper"));
        Assert.False(PluginNames.NameImitatesBrand(null));
        Assert.Contains("openai", PluginNames.Brands);
        Assert.DoesNotContain("openai", PluginNames.Generic);
        Assert.All(PluginNames.Brands.Concat(PluginNames.Generic), n => Assert.Contains(n, PluginNames.Reserved));
    }

    private sealed class IdsProvider(params string[] ids) : ILibraryProvider
    {
        public string Name => "ids";
        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LibraryItem>>(ids.Select(id => new LibraryItem { Id = id, Source = "x", Name = id, Launch = new LaunchTarget(LaunchKind.Exe, "x.exe") }).ToList());
    }

    [Fact]
    public async Task A_plugin_entry_with_an_id_of_the_cores_kind_is_skipped()
    {
        var m = NewManager();
        var ctx = m.ContextFor("streaming", new PluginManager.PluginGrant("streaming", PluginOrigin.FirstParty, new HashSet<string> { PluginCapabilities.Library }, 5), true);
        ctx.RegisterLibraryProvider(new IdsProvider("streaming:ok", "steam:10", "STEAM:11", "Dir:c:/x", "plugin:other:a", "plugin:", "streaming:two"));
        var items = await m.LibraryProviders[0].GetItemsAsync(default);
        Assert.Equal(["streaming:ok", "streaming:two"], items.Select(i => i.Id));
    }

    [Fact]
    public void A_game_event_is_not_sent_for_a_hidden_entry_or_a_switched_off_area_and_is_cleaned()
    {
        var on = AreaSet.AllOn;
        var started = PluginHostRules.EventFor(Item("steam:1", "Ga\u0000me"), on, 0, 42);
        Assert.NotNull(started);
        Assert.Equal("Game", started!.Name);
        Assert.Equal(42, started.ProcessId);
        Assert.Equal("steam:1", started.Id);
        Assert.Null(PluginHostRules.EventFor(Item("steam:1", "Game", hidden: true), on, 1, null));
        Assert.Null(PluginHostRules.EventFor(Item("steam:1", "Game"), new AreaSet([FeatureAreas.Games]), 1, null));
        Assert.NotNull(PluginHostRules.EventFor(Item("app:1", "Tool", ItemType.App), new AreaSet([FeatureAreas.Games]), 1, null));
        // The install folder stays for compatibility.
        var withDir = Item("steam:2", "Dir") with { InstallDir = @"C:\Games\Dir" };
        Assert.Equal(@"C:\Games\Dir", PluginHostRules.EventFor(withDir, on, 0, null)!.InstallDir);
    }

    [Fact]
    public void Two_reads_in_a_row_see_one_snapshot_and_a_change_drops_it()
    {
        var (m, ctx) = Load("p", PluginOrigin.FirstParty, [PluginCapabilities.HostState]);
        var reads = 0;
        m.HostStateReader = () => { reads++; return new PluginHostSnapshot("0.7.0", PluginShellMode.App, reads > 1, null); };
        var host = ctx.Host!;
        _ = host.Mode;
        _ = host.PcMode;
        _ = host.RunningApp;
        Assert.Equal(1, reads);
        Assert.False(host.PcMode);
        m.NotifyHostChanged();
        Assert.True(host.PcMode);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void An_uninstalled_plugin_hears_no_more_events()
    {
        var (m, ctx) = Load("hub", PluginOrigin.FirstParty, [PluginCapabilities.NetworkLan], Svc("hub", "lan"));
        var heard = 0;
        ctx.ServiceAllowedChanged += _ => Interlocked.Increment(ref heard);
        m.ForgetSecrets(["hub"]);
        _config.Offline = true;
        m.NotifyServicesChanged();
        Thread.Sleep(150);
        Assert.Equal(0, Volatile.Read(ref heard));
    }

    [Fact]
    public void An_unrelated_name_is_still_free()
    {
        Assert.False(PluginNames.IsReserved("my-lights"));
        Assert.False(PluginNames.IsReserved("snewsflash"[..0] + "kodi"));
    }

    // =====================================================================
    // SDK 4 plugins keep working: every new member has a default

    private sealed class OldSettings : IPluginSettings
    {
        public string? Get(string key) => null;
        public bool GetBool(string key, bool fallback = false) => fallback;
        public void Set(string key, string? value) { }
    }

    private sealed class OldEvents : IPluginEvents
    {
        public event Action<GameEvent>? GameStarted { add { } remove { } }
        public event Action<GameEvent>? GameEnded { add { } remove { } }
        public event Action? PcModeEntered { add { } remove { } }
        public event Action? CouchModeEntered { add { } remove { } }
    }

    [Fact]
    public void A_class_written_for_the_older_interfaces_still_compiles_and_the_new_members_do_nothing()
    {
        IPluginSettings settings = new OldSettings();
        settings.SettingChanged += _ => { };
        IPluginEvents events = new OldEvents();
        events.HostStopping += () => { };
        Assert.Equal(5, SdkVersion.Current);
        // The context's new members have defaults too (a host older than this build answers false / null).
        var members = typeof(IPluginContext).GetMethods().Where(m => m.Name is "ServiceAllowed" or "get_Host");
        Assert.All(members, m => Assert.False(m.IsAbstract));
        Assert.False(typeof(IPluginContext).GetEvent(nameof(IPluginContext.ServiceAllowedChanged))!.AddMethod!.IsAbstract);
    }

    // =====================================================================
    // LibraryItem.LimitGroup

    private sealed class GroupProvider(params string?[] groups) : ILibraryProvider
    {
        public string Name => "g";
        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LibraryItem>>(groups.Select((g, n) => new LibraryItem
            {
                Id = "x:" + n, Source = "steam", Name = "S" + n, InstallDir = @"C:\Moonlight", LimitGroup = g, Launch = new LaunchTarget(LaunchKind.Url, "steam://run/10")
            }).ToList());
    }

    private async Task<IReadOnlyList<LibraryItem>> Items(string pluginId, params string?[] groups)
    {
        var m = NewManager();
        var ctx = m.ContextFor(pluginId, new PluginManager.PluginGrant(pluginId, PluginOrigin.FirstParty, new HashSet<string> { PluginCapabilities.Library }, 5), true);
        ctx.RegisterLibraryProvider(new GroupProvider(groups));
        return await m.LibraryProviders[0].GetItemsAsync(default);
    }

    [Fact]
    public async Task Entries_of_one_plugin_with_different_groups_do_not_share_a_limit_and_the_same_group_does()
    {
        var items = await Items("streaming", "pc1", "pc2", "pc1");
        var keys = items.Select(Core.Parental.ParentalService.LimitKey).ToList();
        Assert.Equal("plugin:streaming:pc1", keys[0]);
        Assert.NotEqual(keys[0], keys[1]);
        Assert.Equal(keys[0], keys[2]);
    }

    [Fact]
    public async Task A_group_never_meets_the_key_of_another_plugin_a_store_id_or_an_install_folder()
    {
        var a = (await Items("streaming", "pc1"))[0];
        var b = (await Items("other", "pc1"))[0];
        Assert.NotEqual(Core.Parental.ParentalService.LimitKey(a), Core.Parental.ParentalService.LimitKey(b));
        // A plugin that names its group like a core key gets the plugin prefix in front.
        var forged = (await Items("streaming", "steam:10", "dir:c:/moonlight"))
            .Select(Core.Parental.ParentalService.LimitKey).ToList();
        Assert.All(forged, k => Assert.StartsWith("plugin:streaming:", k));
        var plain = new LibraryItem { Id = "steam:10", Source = "steam", Name = "S", Launch = new LaunchTarget(LaunchKind.Url, "steam://run/10") };
        Assert.Equal("steam:10", Core.Parental.ParentalService.LimitKey(plain));
        Assert.DoesNotContain(Core.Parental.ParentalService.LimitKey(plain), forged);
    }

    [Fact]
    public async Task An_invalid_group_counts_as_unset_and_falls_back_to_the_old_key()
    {
        var items = await Items("streaming", new string('x', 65), "a\nb", " ", "ok");
        Assert.All(items.Take(3), i => Assert.Null(i.LimitGroup));
        Assert.Equal("steam:10", Core.Parental.ParentalService.LimitKey(items[0]));
        Assert.Equal("plugin:streaming:ok", Core.Parental.ParentalService.LimitKey(items[3]));
        var dir = new LibraryItem { Id = "p:1", Source = "p", Name = "P", InstallDir = @"C:\Moonlight\", Launch = new LaunchTarget(LaunchKind.Exe, "m.exe") };
        Assert.Equal("dir:c:" + (char)92 + "moonlight", Core.Parental.ParentalService.LimitKey(dir));
    }
}
