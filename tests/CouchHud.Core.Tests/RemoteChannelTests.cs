using System.Text;
using System.Text.Json.Nodes;
using CouchHud.Core.Remote;

namespace CouchHud.Core.Tests;

/// <summary>
/// The phone remote's encrypted channel (0.7): the seal format against the vector the JavaScript half produces (scripts/ui-logic-check.mjs
/// checks the same vector there), tampering, replay, sealed pairing, sessions, levels and the child cap, events and the parents' code limit.
/// </summary>
public sealed class RemoteChannelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 20, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Nonce = Enumerable.Range(0, 16).Select(i => (byte)(0xa0 + i)).ToArray();
    private const string Plain = "{\"op\":\"state\",\"args\":{\"name\":\"Grüße aus dem Wohnzimmer, ein etwas längerer Text\"}}";

    /// <summary>Produced by ui/remote/seal.js with the same key, nonce, direction, number and context (pinned in ui-logic-check.mjs too).</summary>
    public const string Vector = "1.7.oKGio6SlpqeoqaqrrK2ur6qdpfF0xhitAqSfKT5uKZcw66PsF0giQccLARPm7A_o0l11C6vJFPQJLYlxitywHw9Xa3xvbB5tlKr1MtA8PbWexEHquLo93L-zLJhVSr3pqhcVh-hiDdY9-UjWDDNU9_9nNvjRj2gXVQ8afZl8LkbVOG14Jw";

    [Fact]
    public void The_seal_is_byte_for_byte_the_one_of_the_phone_page()
    {
        Assert.Equal(Vector, RemoteSeal.Seal(Key, SealDirection.Request, 7, "rpc|abc", Encoding.UTF8.GetBytes(Plain), Nonce));
        Assert.Equal(Plain, RemoteSeal.OpenText(Key, SealDirection.Request, "rpc|abc", Vector, out var seq));
        Assert.Equal(7, seq);
        Assert.Equal("33e89a402023a384db04c474a67aa2f3", RemoteSeal.UrlProof(Key, "events", "sid|5"));
    }

    [Fact]
    public void Anything_but_the_exact_message_does_not_open()
    {
        // Other direction (an answer sent back as a request), other context (another session or operation), other key.
        Assert.Null(RemoteSeal.Open(Key, SealDirection.Response, "rpc|abc", Vector, out _));
        Assert.Null(RemoteSeal.Open(Key, SealDirection.Request, "rpc|abd", Vector, out _));
        var other = (byte[])Key.Clone();
        other[0] ^= 1;
        Assert.Null(RemoteSeal.Open(other, SealDirection.Request, "rpc|abc", Vector, out _));
        // A changed number (the tag covers it), a flipped bit anywhere in the body, cut, padded, malformed.
        Assert.Null(RemoteSeal.Open(Key, SealDirection.Request, "rpc|abc", "1.8." + Vector[4..], out _));
        var body = Vector[4..].ToCharArray();
        for (var i = 0; i < body.Length; i += 9)
        {
            var copy = (char[])body.Clone();
            copy[i] = copy[i] == 'A' ? 'B' : 'A';
            Assert.Null(RemoteSeal.Open(Key, SealDirection.Request, "rpc|abc", "1.7." + new string(copy), out _));
        }
        foreach (var bad in new[] { "", "1.7.", "2.7." + Vector[4..], "1.07." + Vector[4..], "1.-1." + Vector[4..], "1.7." + Vector[4..^2], Vector + "=", Vector + ".x", "1.7.!!!!" })
            Assert.Null(RemoteSeal.Open(Key, SealDirection.Request, "rpc|abc", bad, out _));
        Assert.Null(RemoteSeal.Open(Key, SealDirection.Request, "rpc|abc", Vector, out _, maxLength: 100));
        Assert.False(RemoteSeal.CheckUrlProof(Key, "events", "sid|6", RemoteSeal.UrlProof(Key, "events", "sid|5")));
        Assert.False(RemoteSeal.CheckUrlProof(Key, "art", "sid|5", RemoteSeal.UrlProof(Key, "events", "sid|5")));
        Assert.False(RemoteSeal.CheckUrlProof(Key, "events", "sid|5", null));
    }

    [Fact]
    public void Every_number_counts_once_and_old_numbers_are_refused()
    {
        var w = new ReplayWindow();
        Assert.True(w.Accept(5));
        Assert.False(w.Accept(5));
        Assert.True(w.Accept(3)); // overtaken, still in the window
        Assert.False(w.Accept(3));
        Assert.True(w.Accept(200));
        Assert.False(w.Accept(200 - ReplayWindow.Size));
        Assert.True(w.Accept(200 - ReplayWindow.Size + 1));
        Assert.False(w.Accept(-1));
    }

    private static (Pairing Pairing, List<RemoteDevice> Devices) Create()
    {
        var devices = new List<RemoteDevice>();
        return (new Pairing(() => devices, change => change(devices), k => "p:" + k, k => k.StartsWith("p:") ? k[2..] : throw new FormatException()), devices);
    }

    private static string PairRequest(string secret, string name) =>
        RemoteSeal.Seal(RemoteSeal.PairKey(RemoteSeal.FromBase64Url(secret)!), SealDirection.Request, 0, "pair", new JsonObject { ["name"] = name }.ToJsonString());

    [Fact]
    public void Sealed_pairing_hands_over_a_key_only_to_who_has_the_qr_secret()
    {
        var (pairing, devices) = Create();
        var secret = pairing.CurrentCode(Now);
        Assert.Equal(32, RemoteSeal.FromBase64Url(secret)!.Length);

        var paired = pairing.TryPairSealed(PairRequest(secret, "Pixel\r\n[remote] fake"), Now, "192.168.1.20", "kid", child: true);
        Assert.NotNull(paired);
        var device = Assert.Single(devices);
        Assert.Equal("Pixel[remote] fake", device.Name); // no control characters in names (log lines, the TV)
        Assert.Equal("control", device.Level);
        Assert.True(device.Child);
        Assert.Equal("kid", device.Profile);
        Assert.StartsWith("p:", device.Key); // stored protected

        var answer = JsonNode.Parse(RemoteSeal.OpenText(RemoteSeal.PairKey(RemoteSeal.FromBase64Url(secret)!), SealDirection.Response, "pair", paired!.Response, out _)!)!;
        Assert.Equal(device.Id, answer["id"]!.GetValue<string>());
        var key = RemoteSeal.FromBase64Url(answer["key"]!.GetValue<string>())!;
        var found = pairing.FindChannel(device.Id, Now.AddMinutes(1));
        Assert.NotNull(found);
        Assert.Equal(key, found!.Value.Key);
        // No token for a phone of the channel: nothing to steal from the old way in.
        Assert.Null(pairing.Find(device.TokenHash, Now));
        // The secret is used up: a recorded request pairs nothing again.
        Assert.Null(pairing.TryPairSealed(PairRequest(secret, "again"), Now, "192.168.1.20", null, false));
        Assert.Single(devices);
    }

    [Fact]
    public void A_request_without_the_secret_counts_as_a_wrong_try_and_pauses_that_source()
    {
        var (pairing, devices) = Create();
        var secret = pairing.CurrentCode(Now);
        var wrong = RemoteSeal.Base64Url(new byte[32]);
        for (var i = 0; i < 5; i++) Assert.Null(pairing.TryPairSealed(PairRequest(wrong, "x"), Now, "192.168.1.66", null, false));
        Assert.Null(pairing.TryPairSealed("garbage", Now, "192.168.1.67", null, false));
        // The paused source cannot pair even with the right secret; another source can.
        Assert.Null(pairing.TryPairSealed(PairRequest(secret, "x"), Now.AddSeconds(5), "192.168.1.66", null, false));
        Assert.NotNull(pairing.TryPairSealed(PairRequest(secret, "Sofa"), Now.AddSeconds(5), "192.168.1.20", null, false));
        Assert.Single(devices);
        // An expired secret never pairs.
        var later = pairing.CurrentCode(Now.AddMinutes(1));
        Assert.Null(pairing.TryPairSealed(PairRequest(later, "late"), Now.AddMinutes(1) + Pairing.CodeLifetime, "192.168.1.21", null, false));
    }

    [Fact]
    public void Phones_paired_before_the_channel_keep_their_token_and_have_no_channel()
    {
        var (pairing, devices) = Create();
        var token = pairing.TryPair(pairing.CurrentCode(Now), "Alt", Now)!;
        var device = Assert.Single(devices);
        Assert.NotNull(pairing.Find(token, Now));
        Assert.Null(pairing.FindChannel(device.Id, Now));
        Assert.Equal(RemoteLevel.Control, RemoteRights.Parse(device.Level));
        // A damaged stored key is no key.
        var (other, list) = Create();
        other.TryPairSealed(PairRequest(other.CurrentCode(Now), "x"), Now, null, null, false);
        list[0].Key = "garbage";
        Assert.Null(other.FindChannel(list[0].Id, Now));
        Assert.Null(other.FindChannel("short", Now));
    }

    [Fact]
    public void The_parents_set_the_level_and_unknown_levels_are_refused()
    {
        var (pairing, devices) = Create();
        pairing.TryPairSealed(PairRequest(pairing.CurrentCode(Now), "x"), Now, null, null, false);
        var id = devices[0].Id;
        Assert.True(pairing.SetLevel(id, "full"));
        Assert.Equal(RemoteLevel.Full, RemoteRights.Parse(devices[0].Level));
        Assert.False(pairing.SetLevel(id, "admin"));
        Assert.False(pairing.SetLevel(id, "Full"));
        Assert.False(pairing.SetLevel("0000000000000000", "full"));
        Assert.Equal("full", devices[0].Level);
    }

    [Fact]
    public void The_companion_commands_are_exactly_the_seven_of_version_1_and_end_in_phone_buttons()
    {
        Assert.Equal(["navigate.up", "navigate.down", "navigate.left", "navigate.right", "navigate.select", "navigate.back", "session.ping"], RemoteCommands.Names);
        Assert.Equal("up", RemoteCommands.ButtonFor("navigate.up"));
        Assert.Equal("a", RemoteCommands.ButtonFor("navigate.select"));
        Assert.Equal("b", RemoteCommands.ButtonFor("navigate.back"));
        // Every mapped button is one of the phone remote's (RemoteHost.Buttons); names are exact, no case or spacing tricks.
        foreach (var name in RemoteCommands.Names) Assert.True(RemoteCommands.ButtonFor(name) is null or "up" or "down" or "left" or "right" or "a" or "b");
        Assert.Null(RemoteCommands.ButtonFor("session.ping"));
        Assert.Null(RemoteCommands.ButtonFor("Navigate.Up"));
        Assert.Null(RemoteCommands.ButtonFor("navigate.up "));
        Assert.Null(RemoteCommands.ButtonFor("shell.run"));
        Assert.Null(RemoteCommands.ButtonFor(null));
        Assert.True(RemoteRights.Allows(RemoteLevel.Control, "command"));
    }

    [Fact]
    public void Levels_only_take_away_and_a_childs_phone_never_gets_more_than_the_child()
    {
        Assert.All(new[] { "state", "button", "text", "powerPrepare", "power", "sleepTimer" }, op => Assert.True(RemoteRights.Allows(RemoteLevel.Control, op)));
        Assert.False(RemoteRights.Allows(RemoteLevel.Control, "library"));
        Assert.False(RemoteRights.Allows(RemoteLevel.Control, "launch"));
        Assert.True(RemoteRights.Allows(RemoteLevel.Library, "launch"));
        Assert.False(RemoteRights.Allows(RemoteLevel.Library, "rename"));
        Assert.True(RemoteRights.Allows(RemoteLevel.Full, "rename"));
        Assert.False(RemoteRights.Allows(RemoteLevel.Full, "setSetting"));
        Assert.False(RemoteRights.Allows(RemoteLevel.Full, null));
        // Every operation that changes something ends in a bridge method (the couch's gates), never past it.
        // (The second factor's two operations are the exception: they change nothing, they answer a question the HUD asked itself, see SecondFactorTests.)
        foreach (var op in RemoteRights.Ops.Values.Where(o => o.Level > RemoteLevel.Control && !o.Name.StartsWith("secondFactor"))) Assert.False(string.IsNullOrEmpty(op.BridgeMethod), op.Name);
        // No profile, plugin, setting, code or lock call is reachable from the phone. The one exception is the settings family of phase 3: the
        // op "setting" ends in setSetting, but only with a key and a value of the positive list (RemoteSettings, see RemoteSettingsTests).
        foreach (var op in RemoteRights.Ops.Values)
        {
            Assert.DoesNotContain(op.BridgeMethod ?? "", Config.ProfilePolicy.All);
            if (op.Name == "setting") { Assert.Equal("setSetting", op.BridgeMethod); continue; }
            Assert.DoesNotMatch("^(setSetting|setFeatureAreas|unlock|setPasscode|setCouchLock|setParent|setParental|install|uninstall|addEntries|addInstalledApp|openUrl|browse|editLaunch|setLaunchOverride|removeEntry|setPlugin|approvePlugin|setRemote|revokeRemote|allowFirewall|killProcess|setShell|enterPcMode|quit)", op.BridgeMethod ?? "");
        }

        var adult = new RemoteDevice { Level = "full", Profile = "mom" };
        Assert.Equal(RemoteLevel.Full, RemoteRights.Effective(adult, "kid", false));
        var child = new RemoteDevice { Level = "full", Profile = "kid", Child = true };
        Assert.Equal(RemoteLevel.Library, RemoteRights.Effective(child, "kid", false));
        Assert.Equal(RemoteLevel.Control, RemoteRights.Effective(child, "mom", false));
        Assert.Equal(RemoteLevel.Control, RemoteRights.Effective(new RemoteDevice { Level = "root" }, "x", false));

        Assert.True(RemoteRights.MayEnterParentCode(adult, sealedChannel: true, "code"));
        Assert.False(RemoteRights.MayEnterParentCode(adult, sealedChannel: false, "code"));
        Assert.False(RemoteRights.MayEnterParentCode(adult, sealedChannel: true, "windows"));
        Assert.False(RemoteRights.MayEnterParentCode(child, sealedChannel: true, "code"));
    }

    [Fact]
    public void Sessions_need_both_nonces_count_each_request_once_and_run_out()
    {
        var sessions = new RemoteSessions();
        var clientNonce = new byte[16];
        var (s, serverNonce) = sessions.Open("dev1", Key, clientNonce, Now);
        Assert.Equal(RemoteSeal.SessionKey(Key, clientNonce, serverNonce), s.Key);
        // A recorded opening gives a new session with another server nonce, so another key.
        var (again, _) = sessions.Open("dev1", Key, clientNonce, Now);
        Assert.NotEqual(s.Key, again.Key);

        Assert.Equal(SessionRefusal.None, sessions.Admit(s, 1, Now));
        Assert.Equal(SessionRefusal.Replay, sessions.Admit(s, 1, Now));
        Assert.Same(s, sessions.Get(s.Id, Now.AddMinutes(29)));
        Assert.Null(sessions.Get(s.Id, Now.AddMinutes(29) + RemoteSessions.IdleLifetime + TimeSpan.FromSeconds(1)));
        Assert.Equal(SessionRefusal.NoSession, sessions.Admit(s, 2, Now.AddHours(2)));
        Assert.Null(sessions.Get("nope", Now));
    }

    [Fact]
    public void Sessions_are_bounded_per_phone_rate_limited_and_go_with_the_phone()
    {
        var sessions = new RemoteSessions();
        var first = sessions.Open("dev1", Key, new byte[16], Now).Session;
        for (var i = 0; i < RemoteSessions.MaxPerDevice; i++) sessions.Open("dev1", Key, new byte[16], Now.AddSeconds(i + 1));
        Assert.Null(sessions.Get(first.Id, Now.AddSeconds(10)));
        Assert.Equal(RemoteSessions.MaxPerDevice, sessions.Count);

        var s = sessions.Open("dev2", Key, new byte[16], Now).Session;
        for (var i = 0; i < RemoteSessions.MaxRequestsPerMinute; i++) Assert.Equal(SessionRefusal.None, sessions.Admit(s, i, Now));
        Assert.Equal(SessionRefusal.TooOften, sessions.Admit(s, 100_000, Now.AddSeconds(30)));
        Assert.Equal(SessionRefusal.None, sessions.Admit(s, 100_001, Now.AddSeconds(61)));

        sessions.DropDevice("dev2");
        Assert.Null(sessions.Get(s.Id, Now.AddSeconds(62)));
        sessions.Keep(new HashSet<string>());
        Assert.Equal(0, sessions.Count);
        for (var i = 0; i < RemoteSessions.MaxTotal + 5; i++) sessions.Open($"d{i}", Key, new byte[16], Now);
        Assert.Equal(RemoteSessions.MaxTotal, sessions.Count);
    }

    [Fact]
    public async Task Events_reach_only_phones_whose_level_may_see_them_and_coalesce()
    {
        var hub = new RemoteEventHub();
        var level = RemoteLevel.Control;
        var control = hub.Subscribe("s1", "dev1", () => level);
        var library = hub.Subscribe("s2", "dev2", () => RemoteLevel.Library);

        hub.Publish("library", "{}");
        hub.Publish("sleepTimer", "{\"at\":1}");
        hub.Publish("sleepTimer", "{\"at\":2}");
        hub.Publish("parental", "{}"); // not a phone event at all
        hub.Publish("toast", "{}");

        Assert.Equal(("sleepTimer", "{\"at\":2}"), await control.TakeAsync(TimeSpan.FromMilliseconds(50), default));
        Assert.Null(await control.TakeAsync(TimeSpan.FromMilliseconds(50), default));
        Assert.Equal("library", (await library.TakeAsync(TimeSpan.FromMilliseconds(50), default))!.Value.Type);
        Assert.Equal("sleepTimer", (await library.TakeAsync(TimeSpan.FromMilliseconds(50), default))!.Value.Type);

        // A phone that is gone (no level) loses its stream at the next event; a phone's own events go to it only.
        var gone = hub.Subscribe("s3", "dev3", () => null);
        hub.Publish("keyboard", "{}");
        Assert.True(gone.Closed);
        hub.Publish("level", "{}", onlyDevice: "dev2");
        Assert.Equal("keyboard", (await control.TakeAsync(TimeSpan.FromMilliseconds(50), default))!.Value.Type);
        Assert.Null(await control.TakeAsync(TimeSpan.FromMilliseconds(50), default));

        // Bounded: a session has one stream, a phone two, all together 16.
        var again = hub.Subscribe("s1", "dev1", () => level);
        Assert.True(control.Closed);
        hub.Subscribe("s4", "dev1", () => level);
        hub.Subscribe("s5", "dev1", () => level);
        Assert.True(again.Closed);
        for (var i = 0; i < 40; i++) hub.Subscribe($"x{i}", $"d{i}", () => level);
        Assert.Equal(RemoteEventHub.MaxTotal, hub.Count);
        hub.DropDevice("d39");
        Assert.Equal(RemoteEventHub.MaxTotal - 1, hub.Count);
    }

    [Fact]
    public void The_parents_code_goes_on_only_for_what_was_asked_and_not_too_often()
    {
        var codes = new RemoteParentCodes();
        Assert.Equal(ParentCodeRefusal.NotAsked, codes.Try("dev1", "launch", "steam:1", Now));
        codes.Asked("dev1", "launch", "steam:1", Now);
        Assert.Equal(ParentCodeRefusal.NotAsked, codes.Try("dev1", "launch", "steam:2", Now));
        Assert.Equal(ParentCodeRefusal.NotAsked, codes.Try("dev1", "extend", "steam:1", Now));
        Assert.Equal(ParentCodeRefusal.NotAsked, codes.Try("dev2", "launch", "steam:1", Now));
        for (var i = 0; i < RemoteParentCodes.MaxTries; i++) Assert.Equal(ParentCodeRefusal.None, codes.Try("dev1", "launch", "steam:1", Now.AddSeconds(i)));
        Assert.Equal(ParentCodeRefusal.TooOften, codes.Try("dev1", "launch", "steam:1", Now.AddSeconds(10)));
        Assert.Equal(ParentCodeRefusal.NotAsked, codes.Try("dev1", "launch", "steam:1", Now + RemoteParentCodes.AskLifetime + TimeSpan.FromSeconds(1)));
        codes.Asked("dev1", "launch", "steam:1", Now + RemoteParentCodes.Window + TimeSpan.FromMinutes(1));
        Assert.Equal(ParentCodeRefusal.None, codes.Try("dev1", "launch", "steam:1", Now + RemoteParentCodes.Window + TimeSpan.FromMinutes(1)));
        codes.Answered("dev1");
        Assert.Equal(ParentCodeRefusal.NotAsked, codes.Try("dev1", "launch", "steam:1", Now + RemoteParentCodes.Window + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Pc_mode_lowers_every_phone_to_control_and_says_why()
    {
        var adult = new RemoteDevice { Level = "full", Profile = "mom" };
        var child = new RemoteDevice { Level = "library", Profile = "kid", Child = true };
        Assert.Equal(RemoteLevel.Control, RemoteRights.Effective(adult, "mom", pcMode: true));
        Assert.Equal(RemoteLevel.Control, RemoteRights.Effective(child, "kid", pcMode: true));
        Assert.Equal(RemoteLevel.Control, RemoteRights.Effective(new RemoteDevice { Level = "library" }, "x", pcMode: true));
        Assert.Equal(RemoteLevel.Full, RemoteRights.Effective(adult, "mom", pcMode: false));
        // Control keeps what it has; everything above is refused, and the code names PC mode only where the level would have allowed it.
        foreach (var op in RemoteRights.Ops.Values)
        {
            var allowed = RemoteRights.Allows(RemoteRights.Effective(adult, "mom", true), op.Name);
            Assert.Equal(op.Level == RemoteLevel.Control, allowed);
            if (!allowed) Assert.Equal("pcMode", RemoteRights.DenyCode(adult, "mom", true, op.Name));
        }
        Assert.Equal("level", RemoteRights.DenyCode(new RemoteDevice { Level = "control" }, "mom", true, "library"));
        Assert.Equal("level", RemoteRights.DenyCode(adult, "mom", false, "nope"));
        Assert.Contains("PC", RemoteRights.DenyText("pcMode"));
    }

    [Fact]
    public void A_childs_phone_ends_only_a_timer_a_childs_phone_set()
    {
        Assert.True(RemoteRights.MayEndSleepTimer(deviceIsChild: false, timerSetByChildPhone: false));
        Assert.True(RemoteRights.MayEndSleepTimer(deviceIsChild: false, timerSetByChildPhone: true));
        Assert.True(RemoteRights.MayEndSleepTimer(deviceIsChild: true, timerSetByChildPhone: true));
        Assert.False(RemoteRights.MayEndSleepTimer(deviceIsChild: true, timerSetByChildPhone: false));
    }

    [Fact]
    public void The_phone_gets_fixed_projections_of_downloads_and_collections()
    {
        // There is no session projection (security re-review L1): the running game's name is not an answer a phone can ask for.
        Assert.Null(RemoteCalls.Project("session", new { id = "steam:1", name = "Game" }));
        Assert.False(RemoteRights.Ops.ContainsKey("session"));
        Assert.Null(RemoteCalls.Map("session", default));

        var entries = new[]
        {
            new CouchHud.Core.Library.DownloadEntry("steam:1", CouchHud.Core.Library.DownloadKind.Game, "steam", "Game", CouchHud.Core.Library.DownloadState.Downloading, 0.5, 5, 10, true,
                @"C:\Users\kevin\secret.log", 1234, true)
        };
        var dl = (Dictionary<string, object?>)RemoteCalls.Project("downloads", new { entries, summary = new CouchHud.Core.Library.DownloadSummary(1, 0, 0.5), extra = "x" })!;
        Assert.Equal(["entries", "summary"], dl.Keys);
        var line = Assert.Single((List<Dictionary<string, object?>>)dl["entries"]!);
        Assert.Equal(["name", "kind", "state", "progress", "bytesDone", "bytesTotal", "isUpdate"], line.Keys);
        Assert.Equal("downloading", line["state"]);
        Assert.Equal("game", line["kind"]);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(dl));

        var collections = new List<CouchHud.Core.Config.ItemCollection> { new() { Id = "c1", Name = "Fav", Items = ["a", "b"], OnHome = true } };
        var cols = (List<Dictionary<string, object?>>)RemoteCalls.Project("collections", collections)!;
        Assert.Equal(["id", "name", "items"], Assert.Single(cols).Keys);
        Assert.Equal(["a", "b"], (List<string>)cols[0]["items"]!);

        // Wrong shapes and unknown operations give nothing.
        Assert.Null(RemoteCalls.Project("collections", new { id = "x" }));
        Assert.Null(RemoteCalls.Project("library", collections));
    }

    [Fact]
    public void Collections_for_a_phone_are_cut_to_what_its_library_shows()
    {
        var collections = new List<CouchHud.Core.Config.ItemCollection>
        {
            new() { Id = "c1", Name = "Mixed", Items = ["a", "secret", "b"] },
            new() { Id = "c2", Name = "OnlySecret", Items = ["secret", "other"] },
            new() { Id = "c3", Name = "Empty", Items = [] }
        };
        var seen = (List<Dictionary<string, object?>>)RemoteCalls.Project("collections", collections, new HashSet<string> { "a", "b" })!;
        Assert.Equal(["c1", "c3"], seen.Select(c => (string)c["id"]!));
        Assert.Equal(["a", "b"], (List<string>)seen[0]["items"]!);
        Assert.Empty((List<string>)seen[1]["items"]!);
        var json = System.Text.Json.JsonSerializer.Serialize(seen);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("OnlySecret", json);

        // Nothing visible at all: only the empty collection is left.
        Assert.Equal(["c3"], ((List<Dictionary<string, object?>>)RemoteCalls.Project("collections", collections, new HashSet<string>())!).Select(c => (string)c["id"]!));

        // The visible set follows the library rule: hidden entries only from the full level on.
        var items = new[] { Game("v", false), Game("h", true) };
        Assert.Equal(["v"], RemoteCalls.VisibleIds(items, RemoteLevel.Library));
        Assert.Equal(["h", "v"], RemoteCalls.VisibleIds(items, RemoteLevel.Full).OrderBy(x => x));
    }

    private static CouchHud.Sdk.LibraryItem Game(string id, bool hidden) =>
        new() { Id = id, Source = "steam", Name = id, Hidden = hidden, Launch = new CouchHud.Sdk.LaunchTarget(CouchHud.Sdk.LaunchKind.Url, "steam://rungameid/1") };

    [Fact]
    public void The_source_guard_never_lifts_a_pause_by_overflow_and_caps_its_table()
    {
        var guard = new SourceGuard();
        for (var i = 0; i < 5; i++) guard.Failed("10.0.0.1", Now);
        Assert.True(guard.Blocked("10.0.0.1", Now));
        // Flooding with many other sources fills the table, but the pause stays and the table never grows past its cap.
        for (var i = 0; i < SourceGuard.MaxSources * 3; i++) guard.Failed("10.1." + (i / 250) + "." + (i % 250), Now);
        Assert.True(guard.Blocked("10.0.0.1", Now));
        Assert.True(guard.Count <= SourceGuard.MaxSources);
        // The pause ends on its own.
        Assert.False(guard.Blocked("10.0.0.1", Now.AddSeconds(31)));
    }

    [Fact]
    public void The_source_guard_counts_ipv6_by_prefix_ipv4_mapped_as_ipv4_and_never_pauses_loopback()
    {
        var guard = new SourceGuard();
        // Five different addresses of one /64 are one source.
        for (var i = 0; i < 5; i++) guard.Failed("fd00:1:2:3::" + (i + 1), Now);
        Assert.True(guard.Blocked("fd00:1:2:3:aaaa:bbbb:cccc:dddd", Now));
        Assert.False(guard.Blocked("fd00:1:2:4::1", Now));
        // ::ffff:a.b.c.d is a.b.c.d.
        for (var i = 0; i < 3; i++) guard.Failed("::ffff:192.168.1.7", Now);
        for (var i = 0; i < 2; i++) guard.Failed("192.168.1.7", Now);
        Assert.True(guard.Blocked("192.168.1.7", Now));
        Assert.True(guard.Blocked("::ffff:192.168.1.7", Now));
        // Loopback (a process of this machine or a proxy on it) is never paused.
        foreach (var lo in new[] { "127.0.0.1", "::1", "::ffff:127.0.0.1" })
        {
            for (var i = 0; i < 20; i++) guard.Failed(lo, Now);
            Assert.False(guard.Blocked(lo, Now));
        }
    }

    [Fact]
    public void The_source_guard_sweeps_expired_pauses_at_most_once_a_second()
    {
        var guard = new SourceGuard();
        for (var i = 0; i < 5; i++) guard.Failed("10.0.0.1", Now);
        for (var i = 0; i < 5; i++) guard.Failed("10.0.0.2", Now.AddSeconds(10));
        Assert.Equal(2, guard.Count);
        // 10.0.0.1's pause is over; the next wrong try after the sweep interval removes it.
        guard.Failed("10.0.0.3", Now.AddSeconds(31));
        Assert.Equal(2, guard.Count);
        // Within the same second no sweep: an entry that expires right after stays until the next one.
        guard.Failed("10.0.0.4", Now.AddSeconds(31.5));
        Assert.Equal(3, guard.Count);
    }
}
