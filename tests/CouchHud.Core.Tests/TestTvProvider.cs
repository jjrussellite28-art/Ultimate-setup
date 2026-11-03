using System.Text.Json;
using CouchHud.Core.Integrations.Tv;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// A small TV provider for the core's tests: SSAP (as <see cref="FakeWebOsTv"/> speaks it) through the <see cref="ITvLink"/> the core hands
/// it, like the webOS plugin does. The real protocol code lives in the plugin's own repository with its own tests; this one only has to
/// be good enough to drive the controller, the link and the hosted session. The hooks let a test make it misbehave like a hostile plugin.
/// </summary>
internal sealed class TestTvProvider(int securePort, int legacyPort, int promptMs = 3000) : ITvProvider
{
    public string Id { get; init; } = TvProviderRegistry.LgWebOs;
    public string Name { get; init; } = "Test TV";
    public IReadOnlyList<TvPort> Ports { get; init; } = [new(securePort, true), new(legacyPort, false)];

    /// <summary>Runs first in PairAsync (a hostile plugin trying things on the link).</summary>
    public Func<ITvLink, CancellationToken, Task>? BeforePair { get; set; }
    /// <summary>Replaces the whole pairing: the plugin returns this without talking to the TV.</summary>
    public Func<ITvLink, CancellationToken, Task<TvPairResult>>? PairOverride { get; set; }
    /// <summary>The keys the provider was handed in ConnectAsync.</summary>
    public List<string> ConnectKeys { get; } = [];
    public int Pairs;

    public async Task<TvPairResult> PairAsync(ITvLink link, Action onPrompt, CancellationToken ct)
    {
        Interlocked.Increment(ref Pairs);
        if (BeforePair is not null) await BeforePair(link, ct);
        if (PairOverride is not null) return await PairOverride(link, ct);
        Ssap ssap;
        try
        {
            ssap = await Ssap.OpenAsync(link, securePort, ct);
        }
        catch (TvException ex) when (ex.Code == TvError.Unreachable)
        {
            // Nobody on the TLS port: an old TV, try the plain one (the link refuses it unless the user allowed it).
            ssap = await Ssap.OpenAsync(link, legacyPort, ct);
        }
        await using var _ = ssap;
        var key = await ssap.RegisterAsync(null, onPrompt, promptMs, ct);
        string? model = null, mac = null, app = null;
        try { model = Str(await ssap.CallAsync("ssap://system/getSystemInfo", null, ct), "modelName"); }
        catch (TvException) { }
        try
        {
            var info = await ssap.CallAsync("ssap://com.webos.service.connectionmanager/getinfo", null, ct);
            mac = info.TryGetProperty("wiredInfo", out var w) ? Str(w, "macAddress") : null;
        }
        catch (TvException) { }
        try { app = Str(await ssap.CallAsync("ssap://com.webos.applicationManager/getForegroundAppInfo", null, ct), "appId"); }
        catch (TvException) { }
        return new TvPairResult(key) { Model = model, Mac = mac, PcApp = app };
    }

    public async Task<ITvSession> ConnectAsync(ITvLink link, string key, CancellationToken ct)
    {
        lock (ConnectKeys) ConnectKeys.Add(key);
        // A TLS pairing: the TLS port; an unencrypted one: the plain port.
        var ssap = await Ssap.OpenAsync(link, link.PlainAllowed ? legacyPort : securePort, ct);
        try
        {
            await ssap.RegisterAsync(key, null, promptMs, ct);
            return new Session(ssap);
        }
        catch
        {
            await ssap.DisposeAsync();
            throw;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed class Ssap(ITvChannel channel) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _one = new(1, 1);
        private int _next;

        public bool Open => channel.Open;

        public static async Task<Ssap> OpenAsync(ITvLink link, int port, CancellationToken ct) => new(await link.OpenAsync(port, "/", ct));

        public async Task<string> RegisterAsync(string? key, Action? onPrompt, int promptMs, CancellationToken ct)
        {
            var payload = new Dictionary<string, object> { ["pairingType"] = "PROMPT", ["manifest"] = new { permissions = new[] { "CONTROL_POWER" } } };
            if (key is not null) payload["client-key"] = key;
            await channel.SendAsync(JsonSerializer.Serialize(new { type = "register", id = "register_0", payload }), ct);
            using var prompt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            prompt.CancelAfter(promptMs);
            try
            {
                while (true)
                {
                    var text = await channel.ReceiveAsync(prompt.Token) ?? throw new TvException(TvError.Unreachable);
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    var type = Str(root, "type");
                    var p = root.TryGetProperty("payload", out var pl) ? pl : default;
                    if (type == "registered") return Str(p, "client-key") ?? throw new TvException(TvError.BadAnswer);
                    if (type == "error") throw new TvException(key is null ? TvError.Denied : TvError.Unpaired);
                    if (type == "response" && Str(p, "pairingType") == "PROMPT")
                    {
                        if (key is not null) throw new TvException(TvError.Unpaired);
                        onPrompt?.Invoke();
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TvException(TvError.Denied);
            }
        }

        public async Task<JsonElement> CallAsync(string uri, object? payload, CancellationToken ct)
        {
            await _one.WaitAsync(ct);
            try
            {
                var id = "c" + Interlocked.Increment(ref _next);
                await channel.SendAsync(JsonSerializer.Serialize(new { type = "request", id, uri, payload }), ct);
                while (true)
                {
                    var text = await channel.ReceiveAsync(ct) ?? throw new TvException(TvError.Unreachable);
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    if (Str(root, "id") != id) continue;
                    if (Str(root, "type") == "error") throw new TvException(TvError.Refused);
                    var p = root.GetProperty("payload").Clone();
                    if (p.TryGetProperty("returnValue", out var rv) && rv.ValueKind == JsonValueKind.False) throw new TvException(TvError.Refused);
                    return p;
                }
            }
            finally
            {
                _one.Release();
            }
        }

        public ValueTask DisposeAsync() => channel.DisposeAsync();
    }

    private sealed class Session(Ssap ssap) : ITvSession
    {
        public bool Open => ssap.Open;

        public Task TurnOffAsync(CancellationToken ct) => ssap.CallAsync("ssap://system/turnOff", null, ct);

        public async Task<TvVolume> GetVolumeAsync(CancellationToken ct)
        {
            var p = await ssap.CallAsync("ssap://audio/getVolume", null, ct);
            if (p.TryGetProperty("volumeStatus", out var s)) p = s;
            var volume = p.TryGetProperty("volume", out var v) ? v.GetInt32() : 0;
            var muted = (p.TryGetProperty("muteStatus", out var m) || p.TryGetProperty("muted", out m)) && m.GetBoolean();
            return new TvVolume(volume, muted);
        }

        public Task VolumeStepAsync(bool up, CancellationToken ct) => ssap.CallAsync(up ? "ssap://audio/volumeUp" : "ssap://audio/volumeDown", null, ct);

        // The plugin passes on what it gets: the hosted session already clamped it.
        public Task SetVolumeAsync(int volume, CancellationToken ct) => ssap.CallAsync("ssap://audio/setVolume", new { volume }, ct);

        public Task SetMuteAsync(bool mute, CancellationToken ct) => ssap.CallAsync("ssap://audio/setMute", new { mute }, ct);

        public async Task<IReadOnlyList<TvInput>> GetInputsAsync(CancellationToken ct)
        {
            var p = await ssap.CallAsync("ssap://tv/getExternalInputList", null, ct);
            // Unfiltered on purpose: cleaning is the hosted session's job.
            return p.GetProperty("devices").EnumerateArray()
                .Select(d => new TvInput(Str(d, "id")!, Str(d, "label")!, d.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True))
                .ToList();
        }

        public Task SwitchInputAsync(string inputId, CancellationToken ct) => ssap.CallAsync("ssap://tv/switchInput", new { inputId }, ct);

        public async Task<string?> GetForegroundAppAsync(CancellationToken ct) =>
            Str(await ssap.CallAsync("ssap://com.webos.applicationManager/getForegroundAppInfo", null, ct), "appId");

        public async Task<string?> GetActiveInputAsync(IReadOnlyList<TvInput> inputs, CancellationToken ct)
        {
            var app = await GetForegroundAppAsync(ct);
            const string prefix = "com.webos.app.hdmi";
            return app is not null && app.StartsWith(prefix, StringComparison.Ordinal) ? "HDMI_" + app[prefix.Length..] : null;
        }

        public ValueTask DisposeAsync() => ssap.DisposeAsync();
    }
}
