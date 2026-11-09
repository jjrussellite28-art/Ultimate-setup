using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaHub.Core.Localization;

namespace MegaHub.Core.Remote;

/// <summary>The answer of the HUD to one phone operation.</summary>
/// <param name="Result">What the phone gets (serialized as JSON) when it worked.</param>
/// <param name="Error">Why not, in the person's language; null when it worked.</param>
/// <param name="Code">A short word for the page ("locked", "held", "area", "running", "bad" …).</param>
public sealed record RemoteCallResult(object? Result, string? Error = null, string? Code = null)
{
    /// <summary>The couch would ask for the parents' code for this action …</summary>
    public string? ParentAction { get; init; }

    /// <summary>… and this entry (null: none).</summary>
    public string? ParentItem { get; init; }

    /// <summary>The parents' code from the phone was right (the host gave its grant).</summary>
    public bool Granted { get; init; }

    public static RemoteCallResult Ok(object? result) => new(result);
    public static RemoteCallResult Fail(string error, string code) => new(null, error, code);
}

/// <summary>The HUD side of the encrypted channel (Hud implements it; calls arrive on Kestrel threads).</summary>
public interface IRemoteChannelTarget : IRemotePowerTarget
{
    /// <summary>The profile on the couch and whether it is a child's with parental controls on (thread-safe snapshot).</summary>
    (string Profile, bool Child) Couch { get; }

    /// <summary>The couch lock's kind ("code", "windows", "off"); the parents' code is typed on a phone only for "code".</summary>
    string CouchLock { get; }

    /// <summary>Mega Hub is in PC mode: couch lock and parental controls are not enforced there, so a phone is only a remote control (<see cref="RemoteRights.Effective"/>).</summary>
    bool PcMode { get; }

    /// <summary>
    /// A pairing request is about to be looked at. With the second factor on this is true only inside the window a phone's confirmation opened
    /// (0.7), and only for one request at a time (checked and taken in one step, so two requests cannot both pass); a request outside the window is
    /// refused whatever the QR code says. <see cref="EndPairing"/> follows in every case.
    /// </summary>
    bool BeginPairing() => true;

    /// <summary>The request taken by <see cref="BeginPairing"/> is done: a pairing that worked closes the window, a failed one leaves it open.</summary>
    void EndPairing(bool paired)
    {
    }

    /// <summary>A phone was paired: the TV says so (a pairing nobody at the couch expected is seen at once).</summary>
    void Paired(RemoteDevice device);

    /// <summary>
    /// One operation the phone's level allows (checked by the channel before): the HUD runs it through the couch's own path, for library
    /// operations the bridge's dispatch with all its gates.
    /// </summary>
    Task<RemoteCallResult> CallAsync(RemoteDevice device, RemoteLevel level, string op, JsonElement args);
}

/// <summary>An HTTP answer of the channel: status and a sealed (or empty) text body.</summary>
public sealed record RemoteChannelReply(int Status, string? Body = null);

/// <summary>
/// The encrypted channel without Kestrel (RemoteHost maps it, tests call it directly): sealed pairing, sessions, sealed requests and the
/// proofs of the event stream and the images. Order for a request: the address rule and the origin (trusted), the session, the seal, the
/// replay window and the rate, the pairing (a phone unpaired meanwhile is out), then the level of the operation, then the HUD. Nothing is
/// read from a request before its seal is checked; a failed seal answers without any detail.
/// </summary>
public sealed class RemoteChannel(Pairing pairing, IRemoteChannelTarget target, RemotePowerApi power, Action<string> log, Func<DateTimeOffset>? clock = null)
{
    /// <summary>The longest sealed request: the server's body cap (RemoteHost.MaxBodyBytes, 32 KiB) is the real limit, this only repeats it.</summary>
    public const int MaxRequestLength = 32 * 1024;

    /// <summary>The page: its scripts are its own (inline) and /seal.js, it talks only to this server, covers come from here.</summary>
    public const string PageCsp = "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>The Host header names an IP address (the QR link's), never a host name: protection against DNS rebinding.</summary>
    public static bool HostIsAddress(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var h = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        if (h.Contains(':')) return System.Net.IPAddress.TryParse(h, out var v6) && v6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        // IPv4 only in the dotted four-part form ("10.1" or a bare number would parse too, but no QR link looks like that).
        return h.Count(c => c == '.') == 3 && h.All(c => c == '.' || char.IsAsciiDigit(c)) && System.Net.IPAddress.TryParse(h, out _);
    }
    private const int MaxSeenNonces = 4096;

    /// <summary>The answers in the TV page's shape: camelCase names and enums as camelCase words (as the bridge sends them).</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _nonceLock = new();
    private readonly HashSet<string> _seenNonces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _streamSeq = new(StringComparer.Ordinal);

    private readonly SourceGuard _guard = new();

    public RemoteSessions Sessions { get; } = new();
    public RemoteEventHub Events { get; } = new();
    public RemoteParentCodes ParentCodes { get; } = new();

    /// <summary>The level a phone has right now (its stored level, capped for a child's phone, see <see cref="RemoteRights.Effective"/>).</summary>
    public RemoteLevel LevelOf(RemoteDevice device) => RemoteRights.Effective(device, target.Couch.Profile, target.PcMode);

    /// <summary>POST /api/pair: the request sealed with the QR code's secret; the answer sealed the same way.</summary>
    public RemoteChannelReply Pair(bool trusted, string? source, string? body)
    {
        if (!trusted || !target.BeginPairing()) return new(403);
        var done = false;
        try
        {
            var couch = target.Couch;
            var paired = pairing.TryPairSealed(body, _now(), source, couch.Profile, couch.Child);
            if (paired is null) return new(400);
            done = true;
            log($"[remote] paired {paired.Device.Id} \"{RemotePower.CleanName(paired.Device.Name)}\"{(couch.Child ? " (child profile)" : "")}");
            target.Paired(paired.Device);
            return new(200, paired.Response);
        }
        finally
        {
            target.EndPairing(done);
        }
    }

    /// <summary>
    /// POST /api/session: the phone's id in a header, a fresh nonce sealed with its device key. The answer (sealed with the device key) names
    /// the session, the server's nonce and the phone's own nonce again (so the phone takes no answer to another request), and its level.
    /// </summary>
    public RemoteChannelReply OpenSession(bool trusted, string? deviceId, string? body, string? source = null)
    {
        if (!trusted) return new(401);
        if (_guard.Blocked(source, _now())) return new(429);
        if (pairing.FindChannel(deviceId, _now()) is not { } found)
        {
            _guard.Failed(source, _now());
            return new(401);
        }
        var aad = "session|" + found.Device.Id;
        var text = RemoteSeal.OpenText(found.Key, SealDirection.Request, aad, body, out _, 4096);
        if (text is null)
        {
            _guard.Failed(source, _now());
            return new(401);
        }
        _guard.Succeeded(source);
        // The seal opened: only now the phone counts as used (its 180 days start again).
        pairing.Used(found.Device, _now());
        var clientNonce = RemoteSeal.FromBase64Url(JsonString(text, "c"));
        if (clientNonce is not { Length: RemoteSeal.NonceSize }) return new(400);
        // A recorded opening sent again gives no usable session (the key needs the device key), but it would push the phone's real
        // sessions out: every nonce once while the server runs.
        lock (_nonceLock)
        {
            if (_seenNonces.Count >= MaxSeenNonces) _seenNonces.Clear();
            if (!_seenNonces.Add(found.Device.Id + ":" + RemoteSeal.Base64Url(clientNonce))) return new(401);
        }
        var (session, serverNonce) = Sessions.Open(found.Device.Id, found.Key, clientNonce, _now());
        var answer = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["sid"] = session.Id,
            ["n"] = RemoteSeal.Base64Url(serverNonce),
            ["c"] = RemoteSeal.Base64Url(clientNonce),
            ["level"] = RemoteRights.Name(LevelOf(found.Device)),
            ["name"] = found.Device.Name
        });
        return new(200, RemoteSeal.Seal(found.Key, SealDirection.Response, 0, aad, answer));
    }

    /// <summary>POST /api/rpc: one sealed operation <c>{op, args}</c> of a session; the answer sealed with the same number.</summary>
    public async Task<RemoteChannelReply> CallAsync(bool trusted, string? sessionId, string? body, string? source = null)
    {
        if (!trusted) return new(401);
        if (_guard.Blocked(source, _now())) return new(429);
        if (Sessions.Get(sessionId, _now()) is not { } session) return new(401);
        var aad = "rpc|" + session.Id;
        var text = RemoteSeal.OpenText(session.Key, SealDirection.Request, aad, body, out var seq, MaxRequestLength);
        if (text is null)
        {
            _guard.Failed(source, _now());
            return new(400);
        }
        switch (Sessions.Admit(session, seq, _now()))
        {
            case SessionRefusal.NoSession: return new(401);
            case SessionRefusal.Replay: return new(409);
            case SessionRefusal.TooOften: return new(429);
        }
        // Only a request that opened and was admitted clears the source's count (a replay or a flood is no good request).
        _guard.Succeeded(source);
        if (pairing.FindChannel(session.DeviceId, _now()) is not { } found)
        {
            Forget(session.DeviceId);
            return new(401);
        }
        pairing.Used(found.Device, _now());
        var answer = await RunAsync(found.Device, text);
        return new(200, RemoteSeal.Seal(session.Key, SealDirection.Response, seq, aad, JsonSerializer.Serialize(answer, Json)));
    }

    private async Task<Dictionary<string, object?>> RunAsync(RemoteDevice device, string text)
    {
        string? op = null;
        JsonElement args = default;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String) op = o.GetString();
                args = doc.RootElement.TryGetProperty("args", out var a) ? a.Clone() : default;
            }
        }
        catch (JsonException)
        {
            op = null;
        }
        if (op is null || !RemoteRights.Ops.ContainsKey(op)) return Answer(RemoteCallResult.Fail(Strings.Get("remote.badRequest"), "bad"));
        var level = LevelOf(device);
        if (!RemoteRights.Allows(level, op))
        {
            var code = RemoteRights.DenyCode(device, target.Couch.Profile, target.PcMode, op);
            return Answer(RemoteCallResult.Fail(RemoteRights.DenyText(code), code));
        }
        switch (op)
        {
            case "powerPrepare":
                return Power(await power.PrepareForAsync(device, Node(args)));
            case "power":
                return Power(await power.RunForAsync(device, Node(args)));
            case "parentCode":
            {
                if (RemoteCalls.Map(op, args) is not { } call) return Answer(RemoteCallResult.Fail(Strings.Get("remote.badRequest"), "bad"));
                if (!RemoteRights.MayEnterParentCode(device, sealedChannel: true, target.CouchLock))
                    return Answer(RemoteCallResult.Fail(Strings.Get("remote.parent.onTv"), "parentTv"));
                var (action, item) = RemoteCalls.ParentScope(call);
                switch (ParentCodes.Try(device.Id, action, item, _now()))
                {
                    case ParentCodeRefusal.NotAsked:
                        return Answer(RemoteCallResult.Fail(Strings.Get("remote.parent.notAsked"), "notAsked"));
                    case ParentCodeRefusal.TooOften:
                        log($"[remote] parents' code from {device.Id}: too many tries");
                        return Answer(RemoteCallResult.Fail(Strings.Get("remote.parent.tooOften"), "tooOften"));
                }
                var result = await target.CallAsync(device, level, op, args);
                // Never the code itself in the log: the phone, the action and the outcome.
                log($"[remote] parents' code from {device.Id} for {action}: {(result.Granted ? "accepted" : "refused")}");
                if (result.Granted) ParentCodes.Answered(device.Id);
                return Answer(result);
            }
            default:
            {
                var result = await target.CallAsync(device, level, op, args);
                if (result.ParentAction is { } action)
                {
                    if (!RemoteRights.MayEnterParentCode(device, sealedChannel: true, target.CouchLock))
                        return Answer(RemoteCallResult.Fail(Strings.Get("remote.parent.onTv"), "parentTv"));
                    ParentCodes.Asked(device.Id, action, result.ParentItem, _now());
                    var asked = Answer(result with { Code = "parent" });
                    asked["parent"] = new Dictionary<string, object?> { ["action"] = action, ["item"] = result.ParentItem };
                    return asked;
                }
                return Answer(result);
            }
        }
    }

    /// <summary>
    /// GET /api/events: the session, a counter above the last one used for a stream of this session, and the URL proof over both (the URL
    /// travels in clear; a recorded one opens nothing again). The stream ends when the session ends or the phone is unpaired.
    /// </summary>
    public (RemoteSession Session, RemoteEventQueue Queue)? OpenStream(bool trusted, string? sessionId, string? counter, string? proof)
    {
        if (!trusted || Sessions.Get(sessionId, _now()) is not { } session) return null;
        if (!long.TryParse(counter, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0) return null;
        if (!RemoteSeal.CheckUrlProof(session.Key, "events", session.Id + "|" + n, proof)) return null;
        var last = _streamSeq.GetOrAdd(session.Id, 0);
        if (n <= last || !_streamSeq.TryUpdate(session.Id, n, last)) return null;
        if (pairing.ById(session.DeviceId) is null) return null;
        Sessions.Touch(session, _now());
        var queue = Events.Subscribe(session.Id, session.DeviceId, () =>
            Sessions.Alive(session, _now()) && pairing.ById(session.DeviceId) is { } d ? LevelOf(d) : null);
        return (session, queue);
    }

    /// <summary>One event for a stream, sealed under the session key with the session's next event number.</summary>
    public static string SealEvent(RemoteSession session, string type, string json) =>
        RemoteSeal.Seal(session.Key, SealDirection.Event, session.NextEventSeq(), "event|" + session.Id,
            "{\"type\":" + JsonSerializer.Serialize(type) + ",\"data\":" + json + "}");

    /// <summary>GET /art/&lt;hash&gt;: a session of a phone with the library level and the proof for exactly this image.</summary>
    public bool MayLoadArt(bool trusted, string? sessionId, string? hash, string? proof)
    {
        if (!trusted || !RemoteCalls.IsHash(hash) || Sessions.Get(sessionId, _now()) is not { } session) return false;
        if (!RemoteSeal.CheckUrlProof(session.Key, "art", hash!, proof)) return false;
        return pairing.ById(session.DeviceId) is { } device && LevelOf(device) >= RemoteLevel.Library;
    }

    /// <summary>An event for the phones (what the TV got); only kinds of <see cref="RemoteRights.EventLevel"/> go out.</summary>
    public void Publish(string type, string json = "{}", string? onlyDevice = null) => Events.Publish(type, json, onlyDevice);

    /// <summary>A phone was unpaired: its sessions and streams end now.</summary>
    public void Forget(string deviceId)
    {
        Sessions.DropDevice(deviceId);
        Events.DropDevice(deviceId);
    }

    /// <summary>Every phone that is no longer paired loses its sessions and streams (after "unpair all").</summary>
    public void KeepOnly(IReadOnlySet<string> pairedIds)
    {
        Sessions.Keep(pairedIds);
        // Every stream looks at its phone again: those of unpaired phones (or ended sessions) close.
        Events.Publish("level", "{}");
    }

    private static Dictionary<string, object?> Answer(RemoteCallResult result) =>
        result.Error is null
            ? new() { ["ok"] = true, ["result"] = result.Result }
            : new() { ["ok"] = false, ["error"] = result.Error, ["code"] = result.Code ?? "error" };

    /// <summary>The power endpoints' answer in the channel's shape (status and code kept for the page's power section).</summary>
    private static Dictionary<string, object?> Power(RemotePowerReply reply)
    {
        var body = reply.Body is null ? null : JsonSerializer.SerializeToNode(reply.Body, Json) as JsonObject;
        if (reply.Status == 200) return new() { ["ok"] = true, ["result"] = body };
        return new()
        {
            ["ok"] = false,
            ["status"] = reply.Status,
            ["error"] = body?["error"]?.GetValue<string>() ?? Strings.Get("remote.badRequest"),
            ["code"] = body?["code"]?.GetValue<string>() ?? "status"
        };
    }

    private static JsonNode? Node(JsonElement args) => args.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(args.GetRawText());

    private static string? JsonString(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
