using System.Net;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Integrations.CaseHud;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Tests;

/// <summary>CaseHUD 1.0: first-start disclaimer, lighting (capability, PATCH validation, refusals).</summary>
public class CaseHudLightingTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (CaseHudClient Client, FakeHandler Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var info = new CaseHudApiInfo { ApiVersion = 1, Version = "1.0.0", Port = 51234, Token = "tok", Pid = 1, Exe = "x" };
        return (new CaseHudClient(new HttpClient(handler), info), handler);
    }

    private const string OldStatus = """
        {"version":"0.9.0","apiVersion":1,"sensors":{"ok":true},"power":true,"gaming":{"active":false},"rotation":{"enabled":false},
         "awtrix":{"enabled":false},"panels":[]}
        """;

    private static string NewStatus(bool accepted) => OldStatus.Replace("\"panels\":[]",
        $$"""
        "panels":[],"disclaimerAccepted":{{(accepted ? "true" : "false")}},"lighting":{"enabled":true,"active":false}
        """);

    private const string LightingJson = """
        {"enabled":true,"brightness":70,"effect":"rainbow","params":{"speed":5,"direction":"cw"},
         "devices":[{"id":"ctl-1","name":"Controller 1","backend":"hid","state":"ok","disabled":false,"ignoreConflicts":false,"settings":{"mode":"x"},"settingValues":{},"zones":[{"id":"z1"}]},
                    {"id":"strip","name":"Strip","backend":"hid","state":"conflict","message":"Held by another program","disabled":true,"ignoreConflicts":true,"zones":[]}],
         "effects":[
           {"name":"solid","label":"Solid","labelDe":"Einfarbig","featured":true,"params":[{"key":"color","type":"color","label":"Color","default":"#ff0000"}]},
           {"name":"breathing","label":"Breathing","labelDe":"Atmen","featured":true,"params":[{"key":"period","type":"number","label":"Period","min":0.5,"max":10,"default":3,"step":0.1}]},
           {"name":"rainbow","label":"Rainbow wave","featured":true,"params":[
             {"key":"speed","type":"number","label":"Speed","labelDe":"Tempo","min":1,"max":10,"default":5,"step":1},
             {"key":"direction","type":"enum","values":["cw","ccw"],"default":"cw"},
             {"key":"mirror","type":"bool","default":false},
             {"key":"caption","type":"text","default":""},
             {"key":"gain","type":"number","min":0,"max":1,"default":0.5},
             {"key":"future","type":"gradient"}]},
           {"name":"lab-only","featured":false,"params":[]}]}
        """;

    private static CaseHudLightingState Last() => CaseHudLighting.Parse(JsonDocument.Parse(LightingJson).RootElement)!;

    private static CaseHudLightingState Parse(string json) => CaseHudLighting.Parse(JsonDocument.Parse(json).RootElement)!;

    private static Dictionary<string, object> Build(string patch) => CaseHudLighting.BuildPatch(JsonDocument.Parse(patch).RootElement, Last());

    private static string Body(string patch) => JsonSerializer.Serialize(Build(patch), CaseHudJson.Options);

    // ---- status: the optional fields

    [Fact]
    public async Task Old_status_without_the_new_fields_is_unknown_not_refused()
    {
        var (client, _) = Client(_ => Json(OldStatus));
        var s = await client.StatusAsync();
        Assert.Null(s.DisclaimerAccepted);
        Assert.Null(s.Lighting);
        Assert.False(CaseHudLighting.NeedsDisclaimer(s));
        Assert.False(CaseHudLighting.Supported(s));
        Assert.False(CaseHudLighting.NeedsDisclaimer(null));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task New_status_reports_the_disclaimer_and_lighting(bool accepted, bool needs)
    {
        var (client, _) = Client(_ => Json(NewStatus(accepted)));
        var s = await client.StatusAsync();
        Assert.Equal(accepted, s.DisclaimerAccepted);
        Assert.Equal(needs, CaseHudLighting.NeedsDisclaimer(s));
        Assert.True(CaseHudLighting.Supported(s));
        Assert.True(s.Lighting!.Enabled);
        Assert.False(s.Lighting.Active);
    }

    // ---- client

    [Fact]
    public async Task Lighting_is_read_and_patched_on_its_endpoint()
    {
        var (client, handler) = Client(_ => Json(LightingJson));
        var state = await client.LightingAsync();
        Assert.Equal("rainbow", state.Effect);
        Assert.Equal(4, state.Effects!.Count);
        Assert.Equal("Rainbow wave", state.Effects[2].Label);
        Assert.Equal(["cw", "ccw"], state.Effects[2].Params![1].Values!);
        Assert.True(state.Devices![1].Disabled);
        Assert.Equal("conflict", state.Devices[1].State);
        Assert.Equal("Held by another program", state.Devices[1].Message);
        Assert.True(state.Devices[1].IgnoreConflicts);
        Assert.Equal("hid", state.Devices[0].Backend);
        Assert.Equal(5, state.Params!["speed"].GetInt32());
        // The PATCH answer is the full state, the same shape as GET, read through the same parser.
        var changed = await client.PatchLightingAsync(Build("""{"brightness":40}"""));
        Assert.Equal(4, changed!.Effects!.Count);
        Assert.Equal(2, changed.Devices!.Count);
        Assert.Equal("GET /api/v1/lighting", $"{handler.Requests[0].Method} {handler.Requests[0].Path}");
        Assert.Equal("PATCH /api/v1/lighting", $"{handler.Requests[1].Method} {handler.Requests[1].Path}");
        Assert.Equal("""{"brightness":40}""", handler.Requests[1].Body);
    }

    [Theory]
    [InlineData(409, "ack_required", "ackRequired", null)]
    [InlineData(403, "gui_only", "guiOnly", null)]
    [InlineData(400, "bad_request", null, "casehud.err.badRequest")]
    [InlineData(413, "payload_too_large", null, "casehud.err.tooLarge")]
    [InlineData(500, "internal", null, "casehud.err.internal")]
    [InlineData(403, "forbidden", null, "casehud.err.forbidden")]
    [InlineData(401, "unauthorized", null, "casehud.err.forbidden")]
    [InlineData(405, "method_not_allowed", null, "casehud.err.notAllowed")]
    public async Task Refusals_map_to_page_problems_or_readable_texts(int status, string code, string? problem, string? textKey)
    {
        var (client, _) = Client(_ => Json("{\"error\":{\"code\":\"" + code + "\",\"message\":\"unknown device x\"}}", (HttpStatusCode)status));
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => client.PatchLightingAsync(Build("""{"enabled":true}""")));
        Assert.Equal(status, ex.Status);
        Assert.Equal(problem, CaseHudLighting.Problem(ex));
        if (textKey is null) return;
        var text = CaseHudLighting.ErrorText(ex);
        Assert.StartsWith(Strings.Get(textKey, "unknown device x").Split('{')[0][..10], text);
        // A 400 carries CaseHUD's reason along.
        if (status == 400) Assert.Contains("unknown device x", text);
    }

    [Fact]
    public void Unknown_codes_keep_casehuds_message_shortened()
    {
        Assert.Equal("teapot", CaseHudLighting.ErrorText(new CaseHudException("teapot", "teapot", 418)));
        var longText = CaseHudLighting.ErrorText(new CaseHudException("x", new string('m', 500), 418));
        Assert.Equal(201, longText.Length);
    }

    // ---- PATCH validation

    [Theory]
    [InlineData("""{"brightness":150}""", """{"brightness":100}""")]
    [InlineData("""{"brightness":-3}""", """{"brightness":0}""")]
    [InlineData("""{"brightness":42.6}""", """{"brightness":43}""")]
    [InlineData("""{"enabled":false}""", """{"enabled":false}""")]
    [InlineData("""{"effect":"solid"}""", """{"effect":"solid"}""")]
    [InlineData("""{"effect":"lab-only"}""", """{"effect":"lab-only"}""")]
    [InlineData("""{"params":{"speed":99}}""", """{"params":{"speed":10}}""")]
    [InlineData("""{"params":{"speed":0}}""", """{"params":{"speed":1}}""")]
    [InlineData("""{"params":{"speed":3.4}}""", """{"params":{"speed":3}}""")]
    [InlineData("""{"params":{"gain":0.25}}""", """{"params":{"gain":0.25}}""")]
    [InlineData("""{"params":{"direction":"ccw","mirror":true}}""", """{"params":{"direction":"ccw","mirror":true}}""")]
    // Only the changed param goes out: CaseHUD keeps the others.
    [InlineData("""{"params":{"mirror":true}}""", """{"params":{"mirror":true}}""")]
    // Switching effects: the effect alone, CaseHUD restores its last values.
    [InlineData("""{"effect":"breathing"}""", """{"effect":"breathing"}""")]
    // Effect and params together are checked against the new effect.
    [InlineData("""{"effect":"solid","params":{"color":"#00FF7f"}}""", """{"effect":"solid","params":{"color":"#00ff7f"}}""")]
    [InlineData("""{"effect":"breathing","params":{"period":2.34}}""", """{"effect":"breathing","params":{"period":2.34}}""")]
    [InlineData("""{"effect":"breathing","params":{"period":0.1}}""", """{"effect":"breathing","params":{"period":0.5}}""")]
    // Brightness, effect and params can be set before switching on.
    [InlineData("""{"brightness":20,"effect":"solid"}""", """{"brightness":20,"effect":"solid"}""")]
    // Devices: an object keyed by id, only "disabled".
    [InlineData("""{"devices":{"strip":{"disabled":false}}}""", """{"devices":{"strip":{"disabled":false}}}""")]
    [InlineData("""{"devices":{"strip":{"disabled":false},"ctl-1":{"disabled":true}}}""", """{"devices":{"strip":{"disabled":false},"ctl-1":{"disabled":true}}}""")]
    public void Valid_patches_are_clamped_and_rebuilt(string patch, string expected) => Assert.Equal(expected, Body(patch));

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"fan":{"curve":[1,2]}}""")]
    [InlineData("""{"enabled":"yes"}""")]
    [InlineData("""{"brightness":"50"}""")]
    [InlineData("""{"effect":"meteor"}""")]
    [InlineData("""{"effect":7}""")]
    [InlineData("""{"params":{"color":"#ff0000"}}""")]
    [InlineData("""{"params":{"nope":1}}""")]
    [InlineData("""{"params":{"speed":"fast"}}""")]
    [InlineData("""{"params":{"direction":"up"}}""")]
    [InlineData("""{"params":{"mirror":1}}""")]
    [InlineData("""{"params":{"future":"x"}}""")]
    [InlineData("""{"params":[1]}""")]
    [InlineData("""{"effect":"solid","params":{"color":"red"}}""")]
    [InlineData("""{"effect":"solid","params":{"speed":5}}""")]
    [InlineData("""{"devices":[{"id":"strip","disabled":false}]}""")]
    [InlineData("""{"devices":{"other":{"disabled":true}}}""")]
    [InlineData("""{"devices":{"strip":{}}}""")]
    [InlineData("""{"devices":{"strip":true}}""")]
    [InlineData("""{"devices":{"strip":{"disabled":"no"}}}""")]
    [InlineData("""{"devices":{"strip":{"disabled":true,"ignoreConflicts":true}}}""")]
    [InlineData("""{"devices":{"strip":{"settings":{"mode":"y"}}}}""")]
    [InlineData("""{"devices":{"ctl-1":{"disabled":true,"zones":{"z1":{"segments":[]}}}}}""")]
    [InlineData("""{"params":{"caption":"hi"}}""")]
    [InlineData("""{"effect":"breathing","params":{"speed":5}}""")]
    public void Invalid_patches_are_refused(string patch) => Assert.Throws<ArgumentException>(() => Build(patch));

    [Fact]
    public void Labels_and_steps_are_read()
    {
        var state = Last();
        Assert.Equal("Einfarbig", state.Effects![0].LabelDe);
        Assert.Equal("Solid", state.Effects[0].Label);
        var period = state.Effects[1].Params![0];
        Assert.Equal(0.1, period.Step);
        Assert.Equal("Period", period.Label);
        Assert.Null(period.LabelDe);
        var speed = state.Effects[2].Params![0];
        Assert.Equal(1, speed.Step);
        Assert.Equal("Tempo", speed.LabelDe);
        // No step, or one that is no positive number: the page falls back to its own guess.
        Assert.Null(state.Effects[2].Params![4].Step);
        var bad = Parse("""{"effects":[{"name":"x","params":[{"key":"n","type":"number","min":0,"max":5,"step":-1},{"key":"m","type":"number","min":0,"max":5,"step":"1"}]}]}""");
        Assert.All(bad.Effects![0].Params!, p => Assert.Null(p.Step));
    }

    [Fact]
    public void A_whole_step_keeps_numbers_whole()
    {
        var state = Parse("""{"effect":"x","effects":[{"name":"x","params":[{"key":"n","type":"number","min":0.5,"max":5,"step":0.5},{"key":"w","type":"number","min":0,"max":50,"default":10,"step":1}]}]}""");
        Assert.Equal("""{"params":{"n":1.25,"w":3}}""",
            JsonSerializer.Serialize(CaseHudLighting.BuildPatch(JsonDocument.Parse("""{"params":{"n":1.25,"w":2.6}}""").RootElement, state), CaseHudJson.Options));
    }

    [Theory]
    [InlineData(0, 4.5, 1.0, 4.5, 4.5)]        // whole steps, a bound off the grid: rounding up to 5 would leave the range
    [InlineData(0, 4.5, 1.0, 4.4, 4.0)]
    [InlineData(0.333, 1, null, 0.333, 0.333)] // two places would give 0.33, below min
    [InlineData(0, 0.666, null, 0.666, 0.666)] // 0.67 would be above max
    [InlineData(1, 10, 1.0, 9.5, 10.0)]
    public void Numbers_stay_in_range_after_rounding(double min, double max, double? step, double value, double expected)
    {
        var stepJson = step is { } st ? ",\"step\":" + st.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var state = Parse("{\"effect\":\"x\",\"effects\":[{\"name\":\"x\",\"params\":[{\"key\":\"n\",\"type\":\"number\",\"min\":" + min.ToString(inv) + ",\"max\":" + max.ToString(inv) + stepJson + "}]}]}");
        var body = CaseHudLighting.BuildPatch(JsonDocument.Parse("{\"params\":{\"n\":" + value.ToString(inv) + "}}").RootElement, state);
        var n = (double)((Dictionary<string, object>)body["params"])["n"];
        Assert.Equal(expected, n);
        Assert.InRange(n, min, max);
    }

    [Fact]
    public void Messages_lose_control_and_format_characters()
    {
        // Bidi override, zero-width space, BOM, line break and bell: none of them reaches the page.
        Assert.Equal("evil.exe txt", CaseHudLighting.SafeMessage("\u202Eevil\u200B.exe\uFEFF\n\a txt"));
        Assert.Equal("", CaseHudLighting.SafeMessage(null));
        var ex = new CaseHudException("bad_request", "bad \u202Edevice\u0000", 400);
        Assert.DoesNotContain('\u202E', CaseHudLighting.ErrorText(ex));
        Assert.DoesNotContain('\0', CaseHudLighting.ErrorText(ex));
    }

    [Fact]
    public void Messages_are_cut_by_text_element_never_inside_a_pair()
    {
        var emoji = "\U0001F600";
        var cut = CaseHudLighting.SafeMessage(new string('a', CaseHudLighting.MaxMessage - 1) + emoji + "bbb");
        Assert.EndsWith(emoji + "…", cut);
        Assert.False(char.IsHighSurrogate(cut[^2]));
        // A letter with a combining accent counts as one element and stays whole.
        var accented = string.Concat(Enumerable.Repeat("é", CaseHudLighting.MaxMessage + 5));
        var cutAccent = CaseHudLighting.SafeMessage(accented);
        Assert.Equal(CaseHudLighting.MaxMessage * 2 + 1, cutAccent.Length);
        Assert.EndsWith("é…", cutAccent);
    }

    [Fact]
    public void Device_state_is_only_one_of_the_documented_values()
    {
        var state = Parse("""{"devices":[{"id":"a","state":"exploded"},{"id":"b","state":"error","message":"I/O"}]}""");
        Assert.Equal("", state.Devices![0].State);
        Assert.Equal("error", state.Devices[1].State);
        Assert.Equal("I/O", state.Devices[1].Message);
    }

    [Fact]
    public void Without_effects_no_effect_or_params_can_be_sent()
    {
        var bare = new CaseHudLightingState(true, 50);
        Assert.Throws<ArgumentException>(() => CaseHudLighting.BuildPatch(JsonDocument.Parse("""{"effect":"solid"}""").RootElement, bare));
        Assert.Throws<ArgumentException>(() => CaseHudLighting.BuildPatch(JsonDocument.Parse("""{"params":{"color":"#ffffff"}}""").RootElement, bare));
        Assert.Single(CaseHudLighting.BuildPatch(JsonDocument.Parse("""{"brightness":10}""").RootElement, bare));
    }

    [Fact]
    public void Number_params_without_a_range_are_refused()
    {
        foreach (var range in new[] { "", "\"min\":1,", "\"max\":9,", "\"min\":9,\"max\":1," })
        {
            var state = Parse("{\"enabled\":true,\"brightness\":5,\"effect\":\"x\",\"effects\":[{\"name\":\"x\",\"params\":[{" + range + "\"key\":\"n\",\"type\":\"number\"}]}]}");
            Assert.Throws<ArgumentException>(() => CaseHudLighting.BuildPatch(JsonDocument.Parse("{\"params\":{\"n\":3}}").RootElement, state));
        }
    }

    // ---- reading CaseHUD's answer defensively

    [Fact]
    public void Entries_with_a_wrong_name_type_are_skipped_not_fatal()
    {
        var state = Parse("""
            {"enabled":"yes","brightness":"bright","effect":5,"params":{"a":[1],"b":{"x":1},"c":"ok","d":2},
             "effects":[{"name":7,"featured":true},{"label":"no name"},{"name":"wave","featured":"true","params":[{"key":1,"type":"number"},{"key":"k"},{"key":"s","type":"enum","values":["a",2,null,"b"]}]}],
             "devices":[{"id":3},{"name":"x"},{"id":"ok","name":5},{"id":"good","disabled":true}]}
            """);
        Assert.False(state.Enabled);
        Assert.Equal(0, state.Brightness);
        Assert.Equal("", state.Effect);
        Assert.Equal(["c", "d"], state.Params!.Keys.Order());
        var wave = Assert.Single(state.Effects!);
        Assert.Equal("wave", wave.Name);
        Assert.False(wave.Featured);
        Assert.Equal(["a", "b"], Assert.Single(wave.Params!).Values!);
        Assert.Equal("good", Assert.Single(state.Devices!).Id);
        // A patch can name only what survived.
        Assert.Throws<ArgumentException>(() => CaseHudLighting.BuildPatch(JsonDocument.Parse("""{"effect":"7"}""").RootElement, state));
    }

    [Fact]
    public void Lists_are_capped_and_long_strings_dropped()
    {
        var longText = new string('x', CaseHudLighting.MaxString + 1);
        var many = string.Join(',', Enumerable.Range(0, 300).Select(i => $"{{\"name\":\"e{i}\",\"params\":[]}}"));
        var devices = string.Join(',', Enumerable.Range(0, 300).Select(i => $"{{\"id\":\"d{i}\"}}"));
        var values = string.Join(',', Enumerable.Range(0, 300).Select(i => $"\"v{i}\""));
        var parameters = string.Join(',', Enumerable.Range(0, 300).Select(i => $"\"p{i}\":{i}"));
        var state = Parse("{\"enabled\":true,\"brightness\":250,\"effect\":\"" + longText + "\",\"params\":{" + parameters + ",\"long\":\"" + longText + "\"}," +
            "\"devices\":[" + devices + ",{\"id\":\"" + longText + "\"}]," +
            "\"effects\":[{\"name\":\"" + longText + "\"},{\"name\":\"fx\",\"label\":\"" + longText + "\",\"params\":[{\"key\":\"s\",\"type\":\"enum\",\"values\":[" + values + "]}]}," + many + "]}");
        Assert.Equal(100, state.Brightness);
        Assert.Equal("", state.Effect);
        Assert.Equal(CaseHudLighting.MaxEntries, state.Params!.Count);
        Assert.DoesNotContain("long", state.Params.Keys);
        Assert.Equal(CaseHudLighting.MaxEntries, state.Devices!.Count);
        Assert.Equal(CaseHudLighting.MaxEntries - 1, state.Effects!.Count);   // the first 128 entries, one of them without a valid name
        var fx = state.Effects[0];
        Assert.Equal("fx", fx.Name);
        Assert.Null(fx.Label);
        Assert.Equal(CaseHudLighting.MaxEntries, fx.Params![0].Values!.Count);
        Assert.Null(CaseHudLighting.Parse(JsonDocument.Parse("[]").RootElement));
    }

    /// <summary>A body that never ends and has no length: only a limit while streaming stops it.</summary>
    private sealed class EndlessStream : Stream
    {
        public long Served { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)' ', offset, count);
            Served += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Oversized_answer_is_cut_off_while_it_streams()
    {
        var body = new EndlessStream();
        var (client, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => client.LightingAsync());
        Assert.Equal("bad_response", ex.Code);
        Assert.InRange(body.Served, CaseHudClient.MaxJsonBytes, CaseHudClient.MaxJsonBytes + 1024 * 1024);
    }

    [Fact]
    public async Task Oversized_error_body_is_cut_off_too()
    {
        var body = new EndlessStream();
        var (client, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StreamContent(body) });
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => client.LightingAsync());
        Assert.Equal(409, ex.Status);
        Assert.True(body.Served < 1024 * 1024);
    }

    // ---- couch lock

    [Fact]
    public void Lighting_reads_while_locked_but_changes_need_the_code()
    {
        Assert.True(CaseHudPolicy.Allowed("caseHudLighting", locked: true));
        Assert.False(CaseHudPolicy.Allowed("caseHudPatchLighting", locked: true));
        Assert.True(CaseHudPolicy.Allowed("caseHudPatchLighting", locked: false));
    }
}
