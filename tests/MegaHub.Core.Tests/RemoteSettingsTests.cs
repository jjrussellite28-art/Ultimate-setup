using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MegaHub.Core.Guard;
using MegaHub.Core.Remote;
using MegaHub.Core.Skins;
using Xunit;

namespace MegaHub.Core.Tests;

/// <summary>
/// Settings from the phone (0.7, phase 3): the positive list with type and range, the fixed projection back, the rights (Full only, not in PC
/// mode, never a child's phone), the parents' code on the same path as the TV, and the ties to the sources (Hud, TV page, phone page).
/// </summary>
public sealed class RemoteSettingsTests
{
    private static JsonElement A(string json) => JsonDocument.Parse(json).RootElement;

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MegaHub.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Root(), .. path])).Replace("\r\n", "\n");

    [Fact]
    public void The_positive_list_is_exactly_this_and_nothing_else()
    {
        Assert.Equal(
            ["language", "favoritesMode", "favoritesRecentDays", "favoritesSites", "screensaverMinutes", "screensaverStyle", "audioMode", "soundVolume", "musicVolume"],
            RemoteSettings.All.Select(s => s.Key));
        Assert.Equal(RemoteSettings.All.Count, RemoteSettings.All.Select(s => s.Key).Distinct().Count());
    }

    [Theory]
    // Anything that takes protection away, starts something or changes who may do what: never from a phone.
    [InlineData("couchLock")]
    [InlineData("setCouchLock")]
    [InlineData("passcode")]
    [InlineData("parentCode")]
    [InlineData("parentalEnabled")]
    [InlineData("profiles")]
    [InlineData("shell")]
    [InlineData("autoLogon")]
    [InlineData("autoLogonDeclined")]
    [InlineData("services")]
    [InlineData("offline")]
    [InlineData("netCatalog")]
    [InlineData("netSystemUpdates")]
    [InlineData("netSaves")]
    [InlineData("netWeather")]
    [InlineData("netNews")]
    [InlineData("netAssistant")]
    [InlineData("catalogUpdateCheck")]
    [InlineData("pluginAutoUpdate")]
    [InlineData("selfUpdateCheck")]
    [InlineData("onlineArtwork")]
    [InlineData("remoteEnabled")]
    [InlineData("remotePower")]
    [InlineData("remoteLevel")]
    [InlineData("level")]
    [InlineData("mcpEnabled")]
    [InlineData("mcpLan")]
    [InlineData("mcpToken")]
    [InlineData("steamGridDbApiKey")]
    [InlineData("anthropicApiKey")]
    [InlineData("openAiApiKey")]
    [InlineData("aiProvider")]
    [InlineData("areas")]
    [InlineData("expertMode")]
    [InlineData("suppressCompetitors")]
    [InlineData("guideTapInGame")]
    [InlineData("gameBarButton")]
    [InlineData("mouseUnlocked")]
    [InlineData("pointer")]
    [InlineData("runStartupApps")]
    [InlineData("killExplorerOnReturn")]
    [InlineData("closeExplorerInLight")]
    [InlineData("closePcAppsOnReturn")]
    [InlineData("browserHistory")]
    [InlineData("modeSwitchJournal")]
    [InlineData("setupWizardDone")]
    [InlineData("acceptedTerms")]
    [InlineData("displayOffMinutes")]
    [InlineData("tvInGuide")]
    [InlineData("tvOnWithMegaHub")]
    [InlineData("tvOffWithPc")]
    [InlineData("weatherLocation")]
    [InlineData("mirrorNotifications")]
    [InlineData("clearArtworkCache")]
    [InlineData("soundSet")]
    [InlineData("skin")]
    [InlineData("__proto__")]
    [InlineData("constructor")]
    [InlineData("LANGUAGE")]
    [InlineData("language ")]
    [InlineData("")]
    public void What_is_not_on_the_list_is_refused_with_any_value(string key)
    {
        Assert.Null(RemoteSettings.Find(key));
        foreach (var value in new[] { "true", "false", "1", "\"x\"", "\"de\"", "null", "{}", "[]" })
            Assert.Null(RemoteSettings.Map("setting", A("{\"key\":" + JsonSerializer.Serialize(key) + ",\"value\":" + value + "}")));
    }

    [Fact]
    public void The_list_holds_no_setting_that_is_protected_elsewhere()
    {
        // The settings the privacy tab, the plugins and the remote itself own are not on it (their keys are checked by name above); and every
        // key of the list is a key the host's setter really has, so the phone never reaches a setting by a name the host would not know.
        var hud = Read("src", "MegaHub.Shell", "Hud.cs");
        foreach (var s in RemoteSettings.All) Assert.Contains($"case \"{s.Key}\":", hud);
        Assert.DoesNotContain(RemoteSettings.All, s => LockPolicy.ProfileIdleSettings.Contains(s.Key) && s.Key != "screensaverMinutes");
    }

    [Theory]
    [InlineData("language", "\"de\"", true)]
    [InlineData("language", "\"auto\"", true)]
    [InlineData("language", "\"fr\"", false)]
    [InlineData("language", "\"DE\"", false)]
    [InlineData("language", "\"de \"", false)]
    [InlineData("language", "\"../de\"", false)]
    [InlineData("language", "true", false)]
    [InlineData("language", "1", false)]
    [InlineData("favoritesMode", "\"combined\"", true)]
    [InlineData("favoritesMode", "\"recent\"", true)]
    [InlineData("favoritesMode", "\"all\"", false)]
    [InlineData("favoritesRecentDays", "30", true)]
    [InlineData("favoritesRecentDays", "0", true)]
    [InlineData("favoritesRecentDays", "31", false)]
    [InlineData("favoritesRecentDays", "-7", false)]
    [InlineData("favoritesRecentDays", "\"30\"", false)]
    [InlineData("favoritesSites", "true", true)]
    [InlineData("favoritesSites", "\"true\"", false)]
    [InlineData("favoritesSites", "1", false)]
    [InlineData("favoritesSites", "null", false)]
    [InlineData("screensaverMinutes", "15", true)]
    [InlineData("screensaverMinutes", "0", true)]
    [InlineData("screensaverMinutes", "240", false)]
    [InlineData("screensaverMinutes", "3", false)]
    [InlineData("screensaverMinutes", "5.5", false)]
    [InlineData("screensaverMinutes", "1e1", false)]
    [InlineData("screensaverMinutes", "99999999999", false)]
    [InlineData("screensaverStyle", "\"black\"", true)]
    [InlineData("screensaverStyle", "\"video\"", false)]
    [InlineData("audioMode", "\"music\"", true)]
    [InlineData("audioMode", "\"loud\"", false)]
    [InlineData("soundVolume", "0", true)]
    [InlineData("soundVolume", "100", true)]
    [InlineData("soundVolume", "101", false)]
    [InlineData("soundVolume", "-1", false)]
    [InlineData("soundVolume", "50.0", false)]
    [InlineData("soundVolume", "\"50\"", false)]
    [InlineData("soundVolume", "2147483648", false)]
    [InlineData("musicVolume", "35", true)]
    [InlineData("musicVolume", "1000", false)]
    public void A_value_must_have_the_type_and_the_range_of_its_setting(string key, string value, bool ok)
    {
        var call = RemoteSettings.Map("setting", A("{\"key\":\"" + key + "\",\"value\":" + value + "}"));
        Assert.Equal(ok, call is not null);
        if (call is null) return;
        Assert.Equal("setSetting", call.Method);
        // The call is rebuilt from the checked value: key and value, exactly two arguments.
        Assert.Equal(2, call.Args.GetArrayLength());
        Assert.Equal(key, call.Args[0].GetString());
        Assert.Equal(A(value).GetRawText(), call.Args[1].GetRawText());
    }

    [Fact]
    public void Malformed_arguments_are_refused_and_extra_fields_are_not_passed_on()
    {
        Assert.Null(RemoteSettings.Map("setting", default));
        Assert.Null(RemoteSettings.Map("setting", A("[]")));
        Assert.Null(RemoteSettings.Map("setting", A("\"language\"")));
        Assert.Null(RemoteSettings.Map("setting", A("{}")));
        Assert.Null(RemoteSettings.Map("setting", A("{\"key\":\"language\"}")));
        Assert.Null(RemoteSettings.Map("setting", A("{\"value\":\"de\"}")));
        Assert.Null(RemoteSettings.Map("setting", A("{\"key\":1,\"value\":\"de\"}")));
        Assert.Null(RemoteSettings.Map("setting", A("{\"key\":[\"language\"],\"value\":\"de\"}")));
        Assert.Null(RemoteSettings.Map("nope", A("{\"key\":\"language\",\"value\":\"de\"}")));
        // Two values for one key (JSON allows it): the last one counts for the parser and is checked like any value.
        Assert.Null(RemoteSettings.Map("setting", A("{\"key\":\"language\",\"value\":\"de\",\"value\":\"fr\"}")));
        var call = RemoteSettings.Map("setting", A("{\"key\":\"language\",\"value\":\"en\",\"extra\":{\"key\":\"couchLock\"},\"confirm\":true}"))!;
        Assert.Equal("[\"language\",\"en\"]", call.Args.GetRawText());
    }

    [Theory]
    [InlineData("default", true)]
    [InlineData("user:neoncity", true)]
    [InlineData("user:Mein Skin 2", true)]
    [InlineData("user:a.b-c_d", true)]
    [InlineData("", false)]
    [InlineData("user:", false)]
    [InlineData("Default", false)]
    [InlineData("ps4", false)]
    [InlineData("user:..", false)]
    [InlineData("user:../x", false)]
    [InlineData("user:a/b", false)]
    [InlineData("user:a\\b", false)]
    [InlineData("user:C:\\Windows", false)]
    [InlineData("user:a..b", false)]
    [InlineData("user:x.", false)]
    [InlineData("user: x", false)]
    [InlineData("user:a\u0000b", false)]
    [InlineData("user:a\nb", false)]
    [InlineData("user:a*", false)]
    [InlineData("user:<script>", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("https://evil.example/x.css", false)]
    [InlineData("\\\\server\\share", false)]
    public void A_skin_id_is_never_a_path(string id, bool ok)
    {
        Assert.Equal(ok, RemoteSettings.IsSkinId(id));
        var call = RemoteSettings.Map("skin", A("{\"id\":" + JsonSerializer.Serialize(id) + "}"));
        Assert.Equal(ok, call is not null);
        if (call is not null)
        {
            Assert.Equal("setSkin", call.Method);
            Assert.Equal(id, call.Args[0].GetString());
        }
    }

    [Fact]
    public void A_skin_id_has_a_length_limit_and_the_right_type()
    {
        Assert.False(RemoteSettings.IsSkinId("user:" + new string('a', 200)));
        Assert.True(RemoteSettings.IsSkinId("user:" + new string('a', 90)));
        Assert.Null(RemoteSettings.Map("skin", A("{\"id\":1}")));
        Assert.Null(RemoteSettings.Map("skin", A("{\"id\":null}")));
        Assert.Null(RemoteSettings.Map("skin", A("{\"id\":[\"default\"]}")));
        Assert.Null(RemoteSettings.Map("skin", A("{}")));
        Assert.Null(RemoteSettings.Map("skin", A("[]")));
        Assert.Null(RemoteSettings.Map("skin", default));
    }

    [Fact]
    public void Remote_calls_hand_setting_and_skin_to_the_settings_rules_and_nothing_else_reaches_setSetting()
    {
        Assert.Equal("setSetting", RemoteCalls.Map("setting", A("{\"key\":\"audioMode\",\"value\":\"off\"}"))!.Method);
        Assert.Null(RemoteCalls.Map("setting", A("{\"key\":\"offline\",\"value\":true}")));
        Assert.Equal("setSkin", RemoteCalls.Map("skin", A("{\"id\":\"default\"}"))!.Method);
        Assert.Equal("getSettings", RemoteCalls.Map("settings", A("{}"))!.Method);
        // Only the op "setting" ends in setSetting, only "skin" in setSkin, only "settings" in getSettings.
        Assert.Equal(["setting"], RemoteRights.Ops.Values.Where(o => o.BridgeMethod == "setSetting").Select(o => o.Name));
        Assert.Equal(["skin"], RemoteRights.Ops.Values.Where(o => o.BridgeMethod == "setSkin").Select(o => o.Name));
        Assert.Equal(["settings"], RemoteRights.Ops.Values.Where(o => o.BridgeMethod == "getSettings").Select(o => o.Name));
        // And the rest of the settings family is not reachable at all.
        foreach (var method in new[] { "setFeatureAreas", "setPluginSetting", "setPluginEnabled", "setRemoteDeviceLevel", "revokeRemote", "setCouchLock", "setPasscode", "setParentCode", "setParental", "setProfile", "setProfileRule", "setIntegrationMode", "setShell", "setAutoLogon", "setBlockTaskManager", "setLauncherMode", "addCatalogSource", "setStartupMode", "setSaveSetting", "setVolume", "setAudioOutput" })
            Assert.DoesNotContain(RemoteRights.Ops.Values, o => o.BridgeMethod == method);
    }

    [Fact]
    public void Settings_are_for_the_level_full_alone_and_never_in_pc_mode_or_for_a_child()
    {
        foreach (var op in new[] { "settings", "setting", "skin" })
        {
            Assert.False(RemoteRights.Allows(RemoteLevel.Control, op), op);
            Assert.False(RemoteRights.Allows(RemoteLevel.Library, op), op);
            Assert.True(RemoteRights.Allows(RemoteLevel.Full, op), op);
        }
        var adult = new RemoteDevice { Level = "full", Profile = "mom" };
        var child = new RemoteDevice { Level = "full", Profile = "kid", Child = true };
        foreach (var op in new[] { "settings", "setting", "skin" })
        {
            Assert.True(RemoteRights.Allows(RemoteRights.Effective(adult, "mom", pcMode: false), op));
            // PC mode: no couch lock, no parental controls there; the phone is a remote control, the code says why.
            Assert.False(RemoteRights.Allows(RemoteRights.Effective(adult, "mom", pcMode: true), op));
            Assert.Equal("pcMode", RemoteRights.DenyCode(adult, "mom", true, op));
            // A child's phone is capped at Library, whatever was stored; with another profile on the couch it is a plain remote.
            Assert.False(RemoteRights.Allows(RemoteRights.Effective(child, "kid", pcMode: false), op));
            Assert.False(RemoteRights.Allows(RemoteRights.Effective(child, "mom", pcMode: false), op));
            // A phone paired before 0.7 (no level) and an invented level have Control.
            Assert.False(RemoteRights.Allows(RemoteRights.Effective(new RemoteDevice(), "mom", false), op));
            Assert.False(RemoteRights.Allows(RemoteRights.Effective(new RemoteDevice { Level = "admin" }, "mom", false), op));
        }
        Assert.Equal(RemoteLevel.Full, RemoteRights.EventLevel("settings"));
    }

    [Fact]
    public void A_phone_cannot_raise_its_own_level_through_the_settings()
    {
        // The level lives in the pairing record and changes only through setRemoteDeviceLevel (TV page, parents' code); no key of the list
        // and no operation of the phone touches it.
        foreach (var s in RemoteSettings.All) Assert.DoesNotMatch("(?i)remote|level|device|phone|pair", s.Key);
        Assert.DoesNotContain(RemoteRights.Ops.Keys, k => k.Contains("evel", StringComparison.Ordinal) || k.Contains("pair", StringComparison.OrdinalIgnoreCase) || k.Contains("revoke", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_screensaver_is_the_parents_with_protected_profiles_and_asks_for_the_same_grant_as_the_tv()
    {
        Assert.Equal(("idleSetting", "screensaverMinutes"), RemoteSettings.ParentScope("screensaverMinutes", profilesGuarded: true));
        Assert.Null(RemoteSettings.ParentScope("screensaverMinutes", profilesGuarded: false));
        foreach (var s in RemoteSettings.All.Where(s => s.Key != "screensaverMinutes")) Assert.Null(RemoteSettings.ParentScope(s.Key, true));
        // One rule, not two: the host's grant for the setting, the TV page's request and the phone's question name the same action and key.
        Assert.Equal("idleSetting", RemoteSettings.IdleAction);
        Assert.Contains("RequireParentGrant(\"idleSetting\", key)", Read("src", "MegaHub.Shell", "Hud.cs"));
        Assert.Contains("{ action: 'idleSetting', item: key }", Read("ui", "views", "settings.js"));
        var remote = Read("src", "MegaHub.Shell", "HudRemote.cs");
        Assert.Contains("RemoteSettings.ParentScope(key, ProfilesGuarded)", remote);
        Assert.Contains("ParentAction = scope.Action, ParentItem = scope.Item", remote);
        // Every setting the host binds to the grant "idleSetting" is the list's screensaver (the displayOff one is not on the list).
        Assert.Contains("screensaverMinutes", LockPolicy.ProfileIdleSettings);
    }

    [Fact]
    public void The_screensaver_choices_are_the_tv_pickers()
    {
        var m = Regex.Match(Read("ui", "views", "settings.js"), @"chooseDialog\(t\('settings\.screensaver\.title'\), \[([0-9, ]+)\]\.map");
        Assert.True(m.Success);
        var tv = m.Groups[1].Value.Split(',').Select(x => int.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(RemoteSettings.ScreensaverMinutes, tv);
        Assert.Equal(MegaHub.Core.Library.FavoritesShelf.RecentDaysChoices, RemoteSettings.Find("favoritesRecentDays")!.Numbers);
    }

    [Fact]
    public void The_phone_gets_only_the_listed_values_in_a_fixed_shape()
    {
        var host = A("""
        {
          "language": "de", "favoritesMode": "recent", "favoritesRecentDays": 90, "favoritesSites": true,
          "screensaverMinutes": 10, "screensaverStyle": "clock", "audioMode": "music", "soundVolume": 70, "musicVolume": 35,  "skin": "user:neoncity",
          "mcpToken": "SECRET", "mcpEnabled": true, "anthropicKeySet": true, "configPath": "C:\\Users\\kevin\\x", "offline": true,
          "areas": ["gaming"], "weatherPlace": {"name": "Home"}, "steamGridDbKeySet": true, "isShell": true
        }
        """);
        var skins = new[] { new SkinInfo("user:neoncity", "Neon\u0007 City", "d", new Dictionary<string, string>(), "body{}") { Sounds = new Dictionary<string, string> { ["x"] = "C:\\secret.ogg" } } };
        var json = JsonSerializer.Serialize(RemoteSettings.Project(host, skins, "de"));
        var node = JsonNode.Parse(json)!;
        Assert.Equal(["values", "skin", "skins", "schema"], node.AsObject().Select(p => p.Key));
        Assert.Equal(RemoteSettings.All.Select(s => s.Key).Order(), node["values"]!.AsObject().Select(p => p.Key).Order());
        Assert.Equal(10, node["values"]!["screensaverMinutes"]!.GetValue<int>());
        Assert.Equal("recent", node["values"]!["favoritesMode"]!.GetValue<string>());
        Assert.Null(node["values"]!["mirrorNotifications"]);
        Assert.Equal("user:neoncity", node["skin"]!.GetValue<string>());
        foreach (var secret in new[] { "SECRET", "mcpToken", "kevin", "configPath", "secret.ogg", "body{}", "Home", "areas" }) Assert.DoesNotContain(secret, json);
        // The skins: the Couch skin first, then the installed ones, id and name only (control characters out).
        var list = node["skins"]!.AsArray();
        Assert.Equal(2, list.Count);
        Assert.Equal("default", list[0]!["id"]!.GetValue<string>());
        Assert.Equal("Neon City", list[1]!["name"]!.GetValue<string>());
        Assert.Equal(["id", "name"], list[1]!.AsObject().Select(p => p.Key));
        // The schema is the list itself.
        Assert.Equal(RemoteSettings.All.Select(s => s.Key), node["schema"]!.AsArray().Select(d => d!["key"]!.GetValue<string>()));
        Assert.Equal("range", node["schema"]!.AsArray().Single(d => d!["key"]!.GetValue<string>() == "soundVolume")!["kind"]!.GetValue<string>());
        Assert.Equal(100, node["schema"]!.AsArray().Single(d => d!["key"]!.GetValue<string>() == "soundVolume")!["max"]!.GetValue<int>());
    }

    [Fact]
    public void A_value_of_the_wrong_shape_is_left_out_of_the_projection()
    {
        var host = A("{\"language\":\"fr\",\"soundVolume\":\"70\",\"favoritesSites\":1,\"screensaverMinutes\":7,\"skin\":\"../x\",\"musicVolume\":55}");
        var node = JsonNode.Parse(JsonSerializer.Serialize(RemoteSettings.Project(host, [], "en")))!;
        Assert.Equal(["musicVolume"], node["values"]!.AsObject().Select(p => p.Key));
        Assert.Null(node["skin"]);
        Assert.Single(node["skins"]!.AsArray());
        Assert.Empty(JsonNode.Parse(JsonSerializer.Serialize(RemoteSettings.Project(default, [], "en")))!["values"]!.AsObject());
    }

    [Fact]
    public void The_skin_list_drops_bad_ids_duplicates_and_is_bounded()
    {
        static SkinInfo S(string id, string name) => new(id, name, "", new Dictionary<string, string>(), "");
        var many = Enumerable.Range(0, 500).Select(i => S("user:s" + i, "S" + i)).Concat([S("default", "Fake"), S("user:../x", "Bad"), S("user:s1", "Twice")]);
        var list = RemoteSettings.SkinList(many, "en");
        Assert.Equal(RemoteSettings.MaxSkins, list.Count);
        Assert.Equal("default", list[0]["id"]);
        Assert.NotEqual("Fake", list[0]["name"]);
        Assert.Equal(list.Count, list.Select(x => x["id"]).Distinct().Count());
        var longName = RemoteSettings.SkinList([S("user:a", new string('n', 500)), S("user:b", "\u0001\u0002")], "en");
        Assert.Equal(RemoteSettings.MaxSkinName, ((string)longName[1]["name"]!).Length);
        Assert.Equal("b", longName[2]["name"]);
    }

    [Fact]
    public void The_host_checks_before_the_bridge_and_runs_the_tv_pages_setters()
    {
        var remote = Read("src", "MegaHub.Shell", "HudRemote.cs");
        // Every phone operation is mapped (and so checked against the positive list) before the bridge sees it.
        Assert.Contains("if (RemoteCalls.Map(op, args) is not { } call) return Bad();", remote);
        Assert.True(remote.IndexOf("RemoteCalls.Map(op, args)", StringComparison.Ordinal) < remote.IndexOf("PhoneBridgeAsync(device, op, call, confirm)", StringComparison.Ordinal));
        // Values and skins run through the bridge's dispatch (setSetting / setSkin: the couch lock, the areas, the profile, then the TV page's setter).
        Assert.Contains("await bridge.InvokeAsync(call.Method, call.Args);", remote);
        // The phone gets the projection, never the host's settings record.
        Assert.Contains("RemoteSettings.Project(", remote);
        // A change that took is told to the TV page and to the phones (from the setter itself, so the TV page's own changes reach the phones too).
        Assert.Contains("Emit(\"settings\", null);", remote);
        Assert.Contains("Emit(\"skin\", id);", remote);
        var hud = Read("src", "MegaHub.Shell", "Hud.cs");
        Assert.Contains("if (RemoteSettings.Find(key) is not null) _remote.Channel?.Publish(\"settings\");", hud);
        Assert.Contains("_remote.Channel?.Publish(\"settings\");", hud);
    }

    [Fact]
    public void The_phone_page_asks_only_for_the_listed_settings_and_shows_the_artwork_button()
    {
        var page = Read("ui", "remote", "index.html");
        // Everything the page sends is one of three operations; no free key.
        Assert.Matches(@"act\('setting', \{ key, value \}\)", page);
        Assert.Matches(@"act\('skin', \{ id \}\)", page);
        Assert.Contains("rpc('settings')", page);
        // The settings tab is hidden below the level Full; PC mode lowers the level, and the page then drops what it showed.
        Assert.Contains("$('tabSettings').hidden = !full;", page);
        Assert.Contains("const full = channel && atLeast('full');", page);
        // Names from the PC are set as text (the page's one innerHTML is its own fixed text, see the contract test).
        Assert.Single(Regex.Matches(page, "innerHTML"));
        // The artwork button of the entry panel, for Full.
        Assert.Contains("full && el('button', { text: tr('reloadArt')", page);
        Assert.Contains("act('reloadArtwork', { id: item.id })", page);
        // Every setting of the list has a title and an event: the page's groups name each key once.
        foreach (var s in RemoteSettings.All) Assert.Contains($"'{s.Key}'", page);
        Assert.Contains("if (type === 'settings') return loadSettings();", page);
        // The page's texts exist in both languages.
        foreach (var s in RemoteSettings.All) Assert.Equal(2, Regex.Matches(page, $@"\bs{char.ToUpperInvariant(s.Key[0])}{s.Key[1..]}: '").Count);
    }
}
