using System.Text.Json;
using System.Text.Json.Nodes;
using CouchHud.Core.Remote;
using Xunit;

namespace CouchHud.Core.Tests;

/// <summary>
/// The channel end to end without Kestrel: pairing, a session, sealed calls and what reaches the HUD (a fake). Every refusal is checked
/// before the fake sees the call.
/// </summary>
public sealed class RemoteChannelFlowTests
{
    private DateTimeOffset _now = new(2026, 11, 2, 20, 0, 0, TimeSpan.Zero);

    private sealed class FakeHud : IRemoteChannelTarget
    {
        public List<(string Op, string Args)> Calls { get; } = [];
        public List<RemoteDevice> PairedDevices { get; } = [];
        public (string Profile, bool Child) Couch { get; set; } = ("parent", false);
        public string CouchLock { get; set; } = "code";
        public bool PcMode { get; set; }
        public Func<string, JsonElement, RemoteCallResult> Answer { get; set; } = (_, _) => RemoteCallResult.Ok(new { done = true });
        public int PowerCalls { get; private set; }
        public bool PowerEnabled => true;
        public bool PairingOpen { get; set; } = true;
        public int Ends { get; private set; }
        public bool LastPaired { get; private set; }
        public bool BeginPairing() => PairingOpen;
        public void EndPairing(bool paired)
        {
            Ends++;
            LastPaired = paired;
        }

        public void Paired(RemoteDevice device) => PairedDevices.Add(device);

        public Task<RemoteCallResult> CallAsync(RemoteDevice device, RemoteLevel level, string op, JsonElement args)
        {
            Calls.Add((op, args.ValueKind == JsonValueKind.Undefined ? "" : args.GetRawText()));
            return Task.FromResult(Answer(op, args));
        }

        public Task<(RemotePowerRefusal Refusal, string? Game, bool GameRunning)> PowerCheckAsync(string action, RemoteDevice device) => Task.FromResult((RemotePowerRefusal.None, (string?)null, false));

        public Task<RemotePowerResult> PowerAsync(string action, RemoteDevice device)
        {
            PowerCalls++;
            return Task.FromResult(RemotePowerResult.Ran);
        }
    }

    private sealed class Phone
    {
        public required string Id;
        public required byte[] Key;
        public string Sid = "";
        public byte[] SessionKey = [];
        public long Seq;
    }

    private readonly List<RemoteDevice> _devices = [];
    private readonly List<string> _log = [];
    private readonly FakeHud _hud = new();
    private readonly Pairing _pairing;
    private readonly RemoteChannel _channel;

    public RemoteChannelFlowTests()
    {
        _pairing = new Pairing(() => _devices, change => change(_devices), k => "p:" + k, k => k.StartsWith("p:") ? k[2..] : throw new FormatException());
        _channel = new RemoteChannel(_pairing, _hud, new RemotePowerApi(_pairing, _hud, _log.Add, () => _now), _log.Add, () => _now);
    }

    private Phone Pair(string name = "Pixel")
    {
        var secret = RemoteSeal.FromBase64Url(_pairing.CurrentCode(_now))!;
        var pairKey = RemoteSeal.PairKey(secret);
        var reply = _channel.Pair(true, "192.168.1.20", RemoteSeal.Seal(pairKey, SealDirection.Request, 0, "pair", new JsonObject { ["name"] = name }.ToJsonString()));
        Assert.Equal(200, reply.Status);
        var answer = JsonNode.Parse(RemoteSeal.OpenText(pairKey, SealDirection.Response, "pair", reply.Body, out _)!)!;
        return new Phone { Id = answer["id"]!.GetValue<string>(), Key = RemoteSeal.FromBase64Url(answer["key"]!.GetValue<string>())! };
    }

    private string SessionRequest(Phone phone, byte[] nonce) =>
        RemoteSeal.Seal(phone.Key, SealDirection.Request, 0, "session|" + phone.Id, new JsonObject { ["c"] = RemoteSeal.Base64Url(nonce) }.ToJsonString());

    private Phone Open(Phone phone)
    {
        var nonce = Enumerable.Range(0, 16).Select(_ => (byte)Random.Shared.Next(256)).ToArray();
        var reply = _channel.OpenSession(true, phone.Id, SessionRequest(phone, nonce));
        Assert.Equal(200, reply.Status);
        var answer = JsonNode.Parse(RemoteSeal.OpenText(phone.Key, SealDirection.Response, "session|" + phone.Id, reply.Body, out _)!)!;
        Assert.Equal(RemoteSeal.Base64Url(nonce), answer["c"]!.GetValue<string>());
        phone.Sid = answer["sid"]!.GetValue<string>();
        phone.SessionKey = RemoteSeal.SessionKey(phone.Key, nonce, RemoteSeal.FromBase64Url(answer["n"]!.GetValue<string>())!);
        phone.Seq = 0;
        return phone;
    }

    private string Request(Phone phone, string op, JsonObject? args = null, long? seq = null) =>
        RemoteSeal.Seal(phone.SessionKey, SealDirection.Request, seq ?? ++phone.Seq, "rpc|" + phone.Sid, new JsonObject { ["op"] = op, ["args"] = args ?? new JsonObject() }.ToJsonString());

    private async Task<JsonNode> Call(Phone phone, string op, JsonObject? args = null)
    {
        var body = Request(phone, op, args);
        var reply = await _channel.CallAsync(true, phone.Sid, body);
        Assert.Equal(200, reply.Status);
        var text = RemoteSeal.OpenText(phone.SessionKey, SealDirection.Response, "rpc|" + phone.Sid, reply.Body, out var seq);
        Assert.Equal(phone.Seq, seq); // the answer carries the request's number: no answer fits another request
        return JsonNode.Parse(text!)!;
    }

    private void SetLevel(Phone phone, string level) => Assert.True(_pairing.SetLevel(phone.Id, level));

    [Fact]
    public async Task A_new_phone_is_paired_on_the_tv_with_control_only()
    {
        var phone = Open(Pair());
        Assert.Single(_hud.PairedDevices);
        var answer = await Call(phone, "library");
        Assert.Equal("level", answer["code"]!.GetValue<string>());
        Assert.Empty(_hud.Calls);

        Assert.True((await Call(phone, "state"))["ok"]!.GetValue<bool>());
        Assert.Equal("state", Assert.Single(_hud.Calls).Op);
    }

    [Fact]
    public void A_pairing_outside_the_window_of_the_second_factor_is_refused_even_with_the_right_secret()
    {
        _hud.PairingOpen = false;
        var secret = RemoteSeal.FromBase64Url(_pairing.CurrentCode(_now))!;
        var pairKey = RemoteSeal.PairKey(secret);
        var reply = _channel.Pair(true, "192.168.1.20", RemoteSeal.Seal(pairKey, SealDirection.Request, 0, "pair", new JsonObject { ["name"] = "Pixel" }.ToJsonString()));
        Assert.Equal(403, reply.Status);
        Assert.Empty(_hud.PairedDevices);
        Assert.Empty(_devices);
        _hud.PairingOpen = true;
        Assert.NotNull(Pair());
    }

    [Fact]
    public void Every_pairing_request_that_was_let_in_is_ended_once_and_only_a_real_pairing_counts()
    {
        var phone = Pair();
        Assert.NotNull(phone);
        Assert.Equal(1, _hud.Ends);
        Assert.True(_hud.LastPaired);

        // A request with a wrong secret passes the window and fails: ended as not paired, the window stays for the real one.
        var bogus = _channel.Pair(true, "192.168.1.21", RemoteSeal.Seal(new byte[32], SealDirection.Request, 0, "pair", "{}"));
        Assert.Equal(400, bogus.Status);
        Assert.Equal(2, _hud.Ends);
        Assert.False(_hud.LastPaired);

        // Outside the window nothing is looked at and nothing is ended.
        _hud.PairingOpen = false;
        Assert.Equal(403, _channel.Pair(true, "192.168.1.22", "x").Status);
        Assert.Equal(2, _hud.Ends);
    }

    [Fact]
    public async Task The_second_factor_operations_reach_the_hud_only_from_a_full_phone()
    {
        var phone = Open(Pair());
        foreach (var level in new[] { "control", "library" })
        {
            SetLevel(phone, level);
            Assert.Equal("level", (await Call(phone, "secondFactor"))["code"]!.GetValue<string>());
            Assert.Equal("level", (await Call(phone, "secondFactorAnswer", new JsonObject { ["requestId"] = "x", ["choice"] = 47 }))["code"]!.GetValue<string>());
        }
        Assert.Empty(_hud.Calls);
        SetLevel(phone, "full");
        Assert.True((await Call(phone, "secondFactor"))["ok"]!.GetValue<bool>());
        Assert.Equal("secondFactor", Assert.Single(_hud.Calls).Op);
    }

    [Fact]
    public async Task A_child_phone_never_reaches_the_second_factor_even_stored_as_full()
    {
        _hud.Couch = ("kid", true);
        var phone = Open(Pair());
        SetLevel(phone, "full");
        Assert.Equal("level", (await Call(phone, "secondFactor"))["code"]!.GetValue<string>());
        Assert.Empty(_hud.Calls);
    }

    [Fact]
    public async Task Each_level_reaches_exactly_its_operations()
    {
        var phone = Open(Pair());
        SetLevel(phone, "library");
        Assert.True((await Call(phone, "library"))["ok"]!.GetValue<bool>());
        Assert.True((await Call(phone, "launch", new JsonObject { ["id"] = "steam:1" }))["ok"]!.GetValue<bool>());
        Assert.Equal("level", (await Call(phone, "rename", new JsonObject { ["id"] = "steam:1", ["name"] = "X" }))["code"]!.GetValue<string>());
        Assert.Equal("level", (await Call(phone, "hide", new JsonObject { ["id"] = "steam:1", ["hidden"] = true }))["code"]!.GetValue<string>());
        Assert.Equal("level", (await Call(phone, "collectionDelete", new JsonObject { ["collection"] = "c1" }))["code"]!.GetValue<string>());
        Assert.Equal(["library", "launch"], _hud.Calls.Select(c => c.Op));

        SetLevel(phone, "full");
        Assert.True((await Call(phone, "rename", new JsonObject { ["id"] = "steam:1", ["name"] = "X" }))["ok"]!.GetValue<bool>());

        // Taken back: the next call already has the lower level, without a new session.
        SetLevel(phone, "control");
        Assert.Equal("level", (await Call(phone, "library"))["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_or_malformed_operations_never_reach_the_hud()
    {
        var phone = Open(Pair());
        SetLevel(phone, "full");
        Assert.Equal("bad", (await Call(phone, "setConfig"))["code"]!.GetValue<string>());
        Assert.Equal("bad", (await Call(phone, "installPlugin"))["code"]!.GetValue<string>());
        Assert.Equal("bad", (await Call(phone, "__proto__"))["code"]!.GetValue<string>());
        Assert.Empty(_hud.Calls);
    }

    [Fact]
    public async Task A_childs_phone_never_gets_more_than_the_child()
    {
        _hud.Couch = ("kid", true);
        var phone = Open(Pair());
        SetLevel(phone, "full");
        Assert.Equal("level", (await Call(phone, "rename", new JsonObject { ["id"] = "a", ["name"] = "b" }))["code"]!.GetValue<string>());
        Assert.True((await Call(phone, "library"))["ok"]!.GetValue<bool>());

        // Another profile on the couch: the child's phone is back to buttons.
        _hud.Couch = ("parent", false);
        Assert.Equal("level", (await Call(phone, "library"))["code"]!.GetValue<string>());

        // And it never types the parents' code, not even when the HUD asks for it.
        _hud.Couch = ("kid", true);
        _hud.Answer = (_, _) => RemoteCallResult.Fail("code", "parent") with { ParentAction = "launch", ParentItem = "steam:1" };
        var asked = await Call(phone, "launch", new JsonObject { ["id"] = "steam:1" });
        Assert.Equal("parentTv", asked["code"]!.GetValue<string>());
        Assert.Equal("parentTv", (await Call(phone, "parentCode", new JsonObject { ["code"] = new JsonArray("a", "b", "x", "y"), ["action"] = "launch", ["item"] = "steam:1" }))["code"]!.GetValue<string>());
        Assert.DoesNotContain(_hud.Calls, c => c.Op == "parentCode");
    }

    [Fact]
    public async Task The_parents_code_is_typed_only_after_the_pc_asked_and_with_the_code_as_couch_lock()
    {
        var phone = Open(Pair());
        SetLevel(phone, "library");
        var code = new JsonObject { ["code"] = new JsonArray("a", "b", "x", "y"), ["action"] = "launch", ["item"] = "steam:1" };

        // Not asked: refused without the HUD.
        Assert.Equal("notAsked", (await Call(phone, "parentCode", (JsonObject)code.DeepClone()))["code"]!.GetValue<string>());

        // The HUD asks (the couch would want the code for this start): the phone gets "parent" with the action and the entry.
        _hud.Answer = (op, _) => op == "launch" ? RemoteCallResult.Fail("code", "parent") with { ParentAction = "launch", ParentItem = "steam:1" } : RemoteCallResult.Ok(null) with { Granted = true };
        var asked = await Call(phone, "launch", new JsonObject { ["id"] = "steam:1" });
        Assert.Equal("parent", asked["code"]!.GetValue<string>());
        Assert.Equal("launch", asked["parent"]!["action"]!.GetValue<string>());

        // A code for another entry is not what was asked.
        Assert.Equal("notAsked", (await Call(phone, "parentCode", new JsonObject { ["code"] = new JsonArray("a"), ["action"] = "launch", ["item"] = "steam:2" }))["code"]!.GetValue<string>());
        Assert.True((await Call(phone, "parentCode", (JsonObject)code.DeepClone()))["ok"]!.GetValue<bool>());
        // Used up with the grant.
        Assert.Equal("notAsked", (await Call(phone, "parentCode", (JsonObject)code.DeepClone()))["code"]!.GetValue<string>());
        // The log names phone and action, never the code's buttons.
        Assert.DoesNotContain(_log, l => l.Contains("\"a\"") || l.Contains("a,b,x,y") || l.Contains("[\"a\""));

        // With Windows' sign-in (or no lock) as couch lock the code belongs to the TV.
        _hud.CouchLock = "windows";
        Assert.Equal("parentTv", (await Call(phone, "launch", new JsonObject { ["id"] = "steam:1" }))["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Wrong_codes_are_limited_per_phone()
    {
        var phone = Open(Pair());
        SetLevel(phone, "library");
        _hud.Answer = (op, _) => op == "launch" ? RemoteCallResult.Fail("code", "parent") with { ParentAction = "launch", ParentItem = "steam:1" } : RemoteCallResult.Fail("wrong", "wrongCode");
        var code = new JsonObject { ["code"] = new JsonArray("a", "a", "a", "a"), ["action"] = "launch", ["item"] = "steam:1" };
        string last = "";
        for (var i = 0; i < RemoteParentCodes.MaxTries + 1; i++)
        {
            await Call(phone, "launch", new JsonObject { ["id"] = "steam:1" });
            last = (await Call(phone, "parentCode", (JsonObject)code.DeepClone()))["code"]!.GetValue<string>();
        }
        Assert.Equal("tooOften", last);
        Assert.Equal(RemoteParentCodes.MaxTries, _hud.Calls.Count(c => c.Op == "parentCode"));
    }

    [Fact]
    public async Task Sealed_requests_are_refused_when_tampered_replayed_foreign_or_untrusted()
    {
        var phone = Open(Pair());
        var other = Open(Pair("Other"));
        var body = Request(phone, "state");
        Assert.Equal(200, (await _channel.CallAsync(true, phone.Sid, body)).Status);
        Assert.Equal(409, (await _channel.CallAsync(true, phone.Sid, body)).Status); // replay
        Assert.Equal(400, (await _channel.CallAsync(true, other.Sid, Request(phone, "state"))).Status); // another session's key
        var tampered = Request(phone, "state");
        tampered = tampered[..^2] + (tampered[^2] == 'A' ? "B" : "A") + tampered[^1];
        Assert.Equal(400, (await _channel.CallAsync(true, phone.Sid, tampered)).Status);
        Assert.Equal(401, (await _channel.CallAsync(true, "nope", Request(phone, "state"))).Status);
        Assert.Equal(401, (await _channel.CallAsync(false, phone.Sid, Request(phone, "state"))).Status); // foreign origin or public address
        Assert.Equal(400, (await _channel.CallAsync(true, phone.Sid, new string('A', RemoteChannel.MaxRequestLength + 10))).Status);
        Assert.Single(_hud.Calls);
    }

    [Fact]
    public async Task An_unpaired_phone_is_out_at_once()
    {
        var phone = Open(Pair());
        Assert.True(_pairing.Revoke(phone.Id));
        Assert.Equal(401, (await _channel.CallAsync(true, phone.Sid, Request(phone, "state"))).Status);
        Assert.Equal(401, _channel.OpenSession(true, phone.Id, SessionRequest(phone, new byte[16])).Status);
        Assert.Empty(_hud.Calls);
    }

    [Fact]
    public void A_recorded_session_opening_opens_nothing_again()
    {
        var phone = Pair();
        var body = SessionRequest(phone, Enumerable.Repeat((byte)7, 16).ToArray());
        Assert.Equal(200, _channel.OpenSession(true, phone.Id, body).Status);
        Assert.Equal(401, _channel.OpenSession(true, phone.Id, body).Status);
        Assert.Equal(401, _channel.OpenSession(false, phone.Id, SessionRequest(phone, Enumerable.Repeat((byte)8, 16).ToArray())).Status);
        Assert.Equal(401, _channel.OpenSession(true, phone.Id, "1.0.garbage").Status);
        // Another phone's key for this id.
        var other = Pair("Other");
        Assert.Equal(401, _channel.OpenSession(true, phone.Id, RemoteSeal.Seal(other.Key, SealDirection.Request, 0, "session|" + phone.Id, "{\"c\":\"" + RemoteSeal.Base64Url(new byte[16]) + "\"}")).Status);
    }

    [Fact]
    public void Pairing_needs_the_trusted_origin_and_the_sealed_secret()
    {
        Assert.Equal(403, _channel.Pair(false, "192.168.1.20", "x").Status);
        Assert.Equal(400, _channel.Pair(true, "192.168.1.20", "{\"code\":\"" + _pairing.CurrentCode(_now) + "\",\"name\":\"x\"}").Status); // the old plain form
        Assert.Empty(_devices);
    }

    [Fact]
    public void The_event_stream_needs_its_proof_and_a_new_counter()
    {
        var phone = Open(Pair());
        string Proof(long n) => RemoteSeal.UrlProof(phone.SessionKey, "events", phone.Sid + "|" + n);
        Assert.Null(_channel.OpenStream(true, phone.Sid, "1", "00"));
        Assert.Null(_channel.OpenStream(false, phone.Sid, "1", Proof(1)));
        var stream = _channel.OpenStream(true, phone.Sid, "1", Proof(1));
        Assert.NotNull(stream);
        Assert.Null(_channel.OpenStream(true, phone.Sid, "1", Proof(1))); // a recorded URL opens nothing again
        Assert.NotNull(_channel.OpenStream(true, phone.Sid, "2", Proof(2)));

        var sealedEvent = RemoteChannel.SealEvent(stream!.Value.Session, "library", "{}");
        var text = RemoteSeal.OpenText(phone.SessionKey, SealDirection.Event, "event|" + phone.Sid, sealedEvent, out _);
        Assert.Equal("library", JsonNode.Parse(text!)!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Covers_need_the_library_level_and_a_proof_for_exactly_that_image()
    {
        var phone = Open(Pair());
        var hash = new string('a', 64);
        var proof = RemoteSeal.UrlProof(phone.SessionKey, "art", hash);
        Assert.False(_channel.MayLoadArt(true, phone.Sid, hash, proof)); // control only
        SetLevel(phone, "library");
        Assert.True(_channel.MayLoadArt(true, phone.Sid, hash, proof));
        Assert.False(_channel.MayLoadArt(true, phone.Sid, new string('b', 64), proof));
        Assert.False(_channel.MayLoadArt(true, phone.Sid, "../" + hash[3..], proof));
        Assert.False(_channel.MayLoadArt(false, phone.Sid, hash, proof));
    }

    [Fact]
    public async Task Power_over_the_channel_takes_the_same_two_steps()
    {
        var phone = Open(Pair());
        var prepared = await Call(phone, "powerPrepare", new JsonObject { ["action"] = "sleep" });
        Assert.True(prepared["ok"]!.GetValue<bool>());
        var ticket = prepared["result"]!["ticket"]!.GetValue<string>();
        Assert.Equal(0, _hud.PowerCalls);
        // Too fast: the ticket is spent, nothing runs.
        var tooFast = await Call(phone, "power", new JsonObject { ["action"] = "sleep", ["ticket"] = ticket });
        Assert.False(tooFast["ok"]!.GetValue<bool>());
        Assert.Equal(0, _hud.PowerCalls);

        prepared = await Call(phone, "powerPrepare", new JsonObject { ["action"] = "sleep" });
        _now = _now.AddSeconds(2);
        var done = await Call(phone, "power", new JsonObject { ["action"] = "sleep", ["ticket"] = prepared["result"]!["ticket"]!.GetValue<string>() });
        Assert.True(done["ok"]!.GetValue<bool>());
        Assert.Equal(1, _hud.PowerCalls);
        Assert.DoesNotContain(_log, l => l.Contains(ticket));
    }

    [Fact]
    public void Phone_arguments_are_checked_and_mapped_to_the_couchs_bridge_calls()
    {
        static JsonElement A(string json) => JsonDocument.Parse(json).RootElement;
        Assert.Equal("launch", RemoteCalls.Map("launch", A("{\"id\":\"steam:1\"}"))!.Method);
        Assert.Null(RemoteCalls.Map("launch", A("{\"id\":\"\"}")));
        Assert.Null(RemoteCalls.Map("launch", A("{\"id\":\"a\\u0000b\"}")));
        Assert.Null(RemoteCalls.Map("launch", A("{\"id\":\"" + new string('x', 300) + "\"}")));
        Assert.Null(RemoteCalls.Map("launch", A("[]")));
        Assert.Equal("editItem", RemoteCalls.Map("rename", A("{\"id\":\"a\",\"name\":\"  Neu  \"}"))!.Method);
        Assert.Contains("\"Neu\"", RemoteCalls.Map("rename", A("{\"id\":\"a\",\"name\":\"  Neu  \"}"))!.Args.GetRawText());
        Assert.Null(RemoteCalls.Map("rename", A("{\"id\":\"a\",\"name\":\"   \"}")));
        Assert.Null(RemoteCalls.Map("rename", A("{\"id\":\"a\",\"name\":\"" + new string('n', 101) + "\"}")));
        Assert.Null(RemoteCalls.Map("hide", A("{\"id\":\"a\",\"hidden\":\"yes\"}")));
        Assert.Null(RemoteCalls.Map("favorite", A("{\"id\":\"a\"}")));
        Assert.Null(RemoteCalls.Map("parentCode", A("{\"code\":[\"a\",\"q\"],\"action\":\"launch\"}")));
        Assert.Null(RemoteCalls.Map("parentCode", A("{\"code\":[" + string.Join(',', Enumerable.Repeat("\"a\"", 17)) + "],\"action\":\"launch\"}")));
        Assert.Equal("parentalAuth", RemoteCalls.Map("parentCode", A("{\"code\":[\"a\",\"b\"],\"action\":\"launch\",\"item\":\"x\"}"))!.Method);
        Assert.Null(RemoteCalls.Map("setConfig", A("{}")));
    }

    [Fact]
    public void Hidden_entries_reach_only_a_phone_that_may_show_them_again()
    {
        var items = new[]
        {
            new CouchHud.Sdk.LibraryItem { Id = "a", Source = "steam", Name = "<img src=x onerror=alert(1)>", Launch = new CouchHud.Sdk.LaunchTarget(CouchHud.Sdk.LaunchKind.Url, "steam://rungameid/1") },
            new CouchHud.Sdk.LibraryItem { Id = "b", Source = "steam", Name = "B", Hidden = true, Launch = new CouchHud.Sdk.LaunchTarget(CouchHud.Sdk.LaunchKind.Url, "steam://rungameid/2") }
        };
        Assert.Single(RemoteCalls.Library(items, RemoteLevel.Library));
        Assert.Equal(2, RemoteCalls.Library(items, RemoteLevel.Full).Count);
        // Names travel as data; the page sets them with textContent (contract test on the page).
        Assert.Equal("<img src=x onerror=alert(1)>", RemoteCalls.Library(items, RemoteLevel.Library)[0]["name"]);
    }

    [Fact]
    public async Task In_pc_mode_a_phone_is_only_a_remote_control_whatever_its_level()
    {
        var phone = Open(Pair());
        SetLevel(phone, "full");
        Assert.True((await Call(phone, "library"))["ok"]!.GetValue<bool>());

        // PC mode: no couch lock, no parental controls on the TV, so no library operation from the phone either.
        _hud.PcMode = true;
        _hud.Calls.Clear();
        foreach (var op in new[] { "library", "collections", "downloads", "launch", "favorite", "parentCode", "rename", "hide", "collectionMember", "collectionCreate", "collectionRename", "collectionDelete", "reloadArtwork" })
        {
            var answer = await Call(phone, op, new JsonObject { ["id"] = "steam:1", ["name"] = "X", ["hidden"] = true, ["favorite"] = true, ["collection"] = "c", ["member"] = true, ["action"] = "launch", ["code"] = new JsonArray("a") });
            Assert.False(answer["ok"]!.GetValue<bool>(), op);
            Assert.Equal("pcMode", answer["code"]!.GetValue<string>());
            Assert.Contains("PC", answer["error"]!.GetValue<string>());
        }
        Assert.Empty(_hud.Calls);
        // What a control phone does stays.
        Assert.True((await Call(phone, "state"))["ok"]!.GetValue<bool>());
        Assert.True((await Call(phone, "button", new JsonObject { ["button"] = "a" }))["ok"]!.GetValue<bool>());
        Assert.Equal(["state", "button"], _hud.Calls.Select(c => c.Op));
        Assert.Equal("control", RemoteRights.Name(_channel.LevelOf(_devices[0])));

        // Back on the couch: the stored level counts again, without a new session.
        _hud.PcMode = false;
        Assert.True((await Call(phone, "rename", new JsonObject { ["id"] = "steam:1", ["name"] = "X" }))["ok"]!.GetValue<bool>());
        // A level the phone never had stays a plain "level" refusal in PC mode, too.
        SetLevel(phone, "control");
        _hud.PcMode = true;
        Assert.Equal("level", (await Call(phone, "library"))["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_child_phone_gets_control_only_in_pc_mode_too()
    {
        _hud.Couch = ("kid", true);
        var phone = Open(Pair());
        SetLevel(phone, "library");
        Assert.True((await Call(phone, "library"))["ok"]!.GetValue<bool>());
        _hud.PcMode = true;
        Assert.Equal("pcMode", (await Call(phone, "launch", new JsonObject { ["id"] = "steam:1" }))["code"]!.GetValue<string>());
    }

    [Fact]
    public void A_request_that_fails_the_seal_renews_nothing_a_good_one_does()
    {
        var phone = Pair();
        _devices[0].LastUsed = _now.AddDays(-3);
        var before = _devices[0].LastUsed;
        // The id alone (a wrong seal, garbage, another phone's key) keeps no phone alive.
        Assert.Equal(401, _channel.OpenSession(true, phone.Id, "1.0.garbage", "10.0.0.9").Status);
        Assert.Equal(401, _channel.OpenSession(true, phone.Id, RemoteSeal.Seal(new byte[32], SealDirection.Request, 0, "session|" + phone.Id, "{}"), "10.0.0.9").Status);
        Assert.Equal(before, _devices[0].LastUsed);
        Assert.Equal(200, _channel.OpenSession(true, phone.Id, SessionRequest(phone, new byte[16]), "10.0.0.9").Status);
        Assert.Equal(_now, _devices[0].LastUsed);
    }

    [Fact]
    public async Task A_phone_that_ran_out_is_still_removed_without_a_renewal()
    {
        var phone = Pair();
        _devices[0].LastUsed = _now.AddDays(-181);
        Assert.Equal(401, _channel.OpenSession(true, phone.Id, SessionRequest(phone, new byte[16])).Status);
        Assert.Empty(_devices);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Wrong_tries_pause_their_source_for_a_moment_like_pairing_does()
    {
        var phone = Open(Pair());
        for (var i = 0; i < 5; i++) Assert.Equal(401, _channel.OpenSession(true, phone.Id, "1.0.garbage", "10.0.0.9").Status);
        // The source is paused, a good request from it too ...
        Assert.Equal(429, _channel.OpenSession(true, phone.Id, SessionRequest(phone, Enumerable.Repeat((byte)1, 16).ToArray()), "10.0.0.9").Status);
        Assert.Equal(429, (await _channel.CallAsync(true, phone.Sid, Request(phone, "state"), "10.0.0.9")).Status);
        // ... another source is not.
        Assert.Equal(200, (await _channel.CallAsync(true, phone.Sid, Request(phone, "state"), "10.0.0.10")).Status);
        _now = _now.AddSeconds(31);
        Assert.Equal(200, (await _channel.CallAsync(true, phone.Sid, Request(phone, "state"), "10.0.0.9")).Status);

        // Bad seals of the rpc endpoint count the same.
        for (var i = 0; i < 5; i++) Assert.Equal(400, (await _channel.CallAsync(true, phone.Sid, "1.1.garbage", "10.0.0.11")).Status);
        Assert.Equal(429, (await _channel.CallAsync(true, phone.Sid, Request(phone, "state"), "10.0.0.11")).Status);
        // A good request clears the count before it reaches the limit.
        for (var i = 0; i < 4; i++) Assert.Equal(400, (await _channel.CallAsync(true, phone.Sid, "1.1.garbage", "10.0.0.12")).Status);
        Assert.Equal(200, (await _channel.CallAsync(true, phone.Sid, Request(phone, "state"), "10.0.0.12")).Status);
        for (var i = 0; i < 4; i++) Assert.Equal(400, (await _channel.CallAsync(true, phone.Sid, "1.1.garbage", "10.0.0.12")).Status);
        Assert.Equal(200, (await _channel.CallAsync(true, phone.Sid, Request(phone, "state"), "10.0.0.12")).Status);
    }

    [Fact]
    public async Task A_replayed_request_does_not_clear_the_count_of_wrong_tries()
    {
        var phone = Open(Pair());
        var good = Request(phone, "state");
        Assert.Equal(200, (await _channel.CallAsync(true, phone.Sid, good, "10.0.0.20")).Status);
        for (var i = 0; i < 4; i++) Assert.Equal(400, (await _channel.CallAsync(true, phone.Sid, "1.1.garbage", "10.0.0.20")).Status);
        // A recorded request sent again opens (it is a genuine seal) but is refused as a replay: it must not reset the four wrong tries.
        Assert.Equal(409, (await _channel.CallAsync(true, phone.Sid, good, "10.0.0.20")).Status);
        Assert.Equal(400, (await _channel.CallAsync(true, phone.Sid, "1.1.garbage", "10.0.0.20")).Status);
        Assert.Equal(429, (await _channel.CallAsync(true, phone.Sid, Request(phone, "state"), "10.0.0.20")).Status);
    }

    [Fact]
    public async Task The_session_record_is_not_an_operation_of_a_phone()
    {
        var phone = Open(Pair());
        SetLevel(phone, "full");
        _hud.Calls.Clear();
        var answer = await Call(phone, "session");
        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.Equal("bad", answer["code"]!.GetValue<string>());
        Assert.Empty(_hud.Calls);
    }

    [Fact]
    public async Task Settings_reach_the_hud_only_for_a_full_phone_outside_pc_mode()
    {
        var phone = Open(Pair());
        var change = new JsonObject { ["key"] = "language", ["value"] = "en" };
        foreach (var level in new[] { "control", "library" })
        {
            SetLevel(phone, level);
            foreach (var op in new[] { "settings", "setting", "skin" })
                Assert.Equal("level", (await Call(phone, op, op == "skin" ? new JsonObject { ["id"] = "default" } : (JsonObject)change.DeepClone()))["code"]!.GetValue<string>());
        }
        Assert.Empty(_hud.Calls);

        SetLevel(phone, "full");
        Assert.True((await Call(phone, "setting", (JsonObject)change.DeepClone()))["ok"]!.GetValue<bool>());
        Assert.True((await Call(phone, "settings"))["ok"]!.GetValue<bool>());
        Assert.Equal(["setting", "settings"], _hud.Calls.Select(c => c.Op));

        // PC mode lowers the phone at once; the answer names PC mode.
        _hud.PcMode = true;
        _hud.Calls.Clear();
        Assert.Equal("pcMode", (await Call(phone, "setting", (JsonObject)change.DeepClone()))["code"]!.GetValue<string>());
        Assert.Equal("pcMode", (await Call(phone, "skin", new JsonObject { ["id"] = "default" }))["code"]!.GetValue<string>());
        Assert.Empty(_hud.Calls);
    }

    [Fact]
    public async Task A_childs_phone_has_no_settings_and_a_phone_cannot_ask_for_a_level_or_a_key_outside_the_list()
    {
        _hud.Couch = ("kid", true);
        var phone = Open(Pair());
        SetLevel(phone, "full");
        Assert.Equal("level", (await Call(phone, "setting", new JsonObject { ["key"] = "screensaverMinutes", ["value"] = 0 }))["code"]!.GetValue<string>());
        Assert.Equal("level", (await Call(phone, "settings"))["code"]!.GetValue<string>());

        _hud.Couch = ("parent", false);
        // There is no operation to change a phone's level or the settings by another name.
        foreach (var op in new[] { "setRemoteDeviceLevel", "remoteLevel", "setSetting", "getSettings", "setSkin", "setConfig", "level" })
            Assert.Equal("bad", (await Call(phone, op, new JsonObject { ["id"] = phone.Id, ["level"] = "full" }))["code"]!.GetValue<string>());
        Assert.Empty(_hud.Calls);
        Assert.Equal("control", RemoteRights.Name(RemoteRights.Effective(new RemoteDevice { Level = null }, "parent", false)));
    }

    [Fact]
    public async Task A_setting_behind_the_parents_code_is_asked_and_answered_on_the_one_code_path()
    {
        var phone = Open(Pair());
        SetLevel(phone, "full");
        // The HUD answers like the TV page would be asked: the parents' code for exactly "idleSetting" and this key.
        _hud.Answer = (op, _) => op switch
        {
            "setting" => RemoteCallResult.Fail("code", "parent") with { ParentAction = RemoteSettings.IdleAction, ParentItem = "screensaverMinutes" },
            "parentCode" => RemoteCallResult.Ok(null) with { Granted = true },
            _ => RemoteCallResult.Ok(null)
        };
        var code = new JsonObject { ["code"] = new JsonArray("a", "b", "x", "y"), ["action"] = "idleSetting", ["item"] = "screensaverMinutes" };
        Assert.Equal("notAsked", (await Call(phone, "parentCode", (JsonObject)code.DeepClone()))["code"]!.GetValue<string>());

        var asked = await Call(phone, "setting", new JsonObject { ["key"] = "screensaverMinutes", ["value"] = 0 });
        Assert.Equal("parent", asked["code"]!.GetValue<string>());
        Assert.Equal("idleSetting", asked["parent"]!["action"]!.GetValue<string>());
        Assert.Equal("screensaverMinutes", asked["parent"]!["item"]!.GetValue<string>());

        // A code for another setting is not what was asked; for this one it goes to the same parentCode path as every other code.
        Assert.Equal("notAsked", (await Call(phone, "parentCode", new JsonObject { ["code"] = new JsonArray("a"), ["action"] = "idleSetting", ["item"] = "displayOffMinutes" }))["code"]!.GetValue<string>());
        Assert.True((await Call(phone, "parentCode", (JsonObject)code.DeepClone()))["ok"]!.GetValue<bool>());
        Assert.Equal("notAsked", (await Call(phone, "parentCode", (JsonObject)code.DeepClone()))["code"]!.GetValue<string>());

        // With Windows' sign-in as couch lock the code belongs to the TV.
        _hud.CouchLock = "windows";
        Assert.Equal("parentTv", (await Call(phone, "setting", new JsonObject { ["key"] = "screensaverMinutes", ["value"] = 0 }))["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_recorded_settings_request_is_not_run_again()
    {
        var phone = Open(Pair());
        SetLevel(phone, "full");
        var body = Request(phone, "setting", new JsonObject { ["key"] = "soundVolume", ["value"] = 0 });
        Assert.Equal(200, (await _channel.CallAsync(true, phone.Sid, body)).Status);
        Assert.Equal(409, (await _channel.CallAsync(true, phone.Sid, body)).Status);
        Assert.Single(_hud.Calls);
    }

    [Fact]
    public async Task The_settings_event_goes_to_full_phones_only()
    {
        var phone = Open(Pair());
        SetLevel(phone, "library");
        var stream = _channel.OpenStream(true, phone.Sid, "1", RemoteSeal.UrlProof(phone.SessionKey, "events", phone.Sid + "|1"));
        Assert.NotNull(stream);
        _channel.Publish("settings");
        _channel.Publish("library");
        var first = await stream!.Value.Queue.TakeAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.Equal("library", first!.Value.Type);
        Assert.Null(await stream.Value.Queue.TakeAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

        SetLevel(phone, "full");
        _channel.Publish("settings");
        Assert.Equal("settings", (await stream.Value.Queue.TakeAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None))!.Value.Type);
    }
}
