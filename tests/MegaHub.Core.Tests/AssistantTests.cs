using System.Text.Json;
using System.Text.Json.Nodes;
using MegaHub.Core.Assistant;
using CouchHud.Sdk;

namespace MegaHub.Core.Tests;

public class AssistantTests
{
    private sealed class FakeHost : IToolHost
    {
        public List<string> Called { get; } = [];

        public Task<string> CallAsync(string name, JsonElement args, CancellationToken ct)
        {
            Called.Add(name);
            if (name == "launch" && args.GetProperty("id").GetString() == "missing") throw new KeyNotFoundException("Nicht gefunden");
            if (name == "launch" && args.GetProperty("id").GetString() == "broken") throw new NotSupportedException("Datei fehlt");
            return Task.FromResult(name == "search_library" ? """[{"id":"steam:1091500","name":"Cyberpunk 2077"}]""" : "ok");
        }

        public string Describe(string name, JsonElement args) => $"{name} ausführen";
    }

    /// <summary>Plays back canned replies and records what it was sent.</summary>
    private sealed class ScriptedProvider(params ChatReply[] replies) : IChatProvider
    {
        private int _next;
        public List<int> HistorySizes { get; } = [];
        public string Name => "fake";

        public Task<ChatReply> CompleteAsync(string system, IReadOnlyList<ChatMessage> history, IReadOnlyList<ToolSpec> tools, CancellationToken ct)
        {
            HistorySizes.Add(history.Count);
            if (_next >= replies.Length) throw new AssistantException("offline");
            return Task.FromResult(replies[_next++]);
        }
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Runs_tools_until_the_model_answers()
    {
        var host = new FakeHost();
        var provider = new ScriptedProvider(
            new ChatReply(null, [new ToolCall("t1", "search_library", Args("""{"query":"cyber"}"""))]),
            new ChatReply("Ich starte es.", [new ToolCall("t2", "launch", Args("""{"id":"steam:1091500"}"""))]),
            new ChatReply("Viel Spaß mit Cyberpunk 2077!", []));
        var session = new AssistantSession(provider, host);
        var confirmations = new List<string>();

        var answer = await session.AskAsync("Starte Cyberpunk", (_, text) =>
        {
            confirmations.Add(text);
            return Task.FromResult(true);
        });

        Assert.Equal("Viel Spaß mit Cyberpunk 2077!", answer);
        Assert.Equal(new[] { "search_library", "launch" }, host.Called.ToArray());
        // Only the launch changes something, so only it was confirmed.
        Assert.Equal(new[] { "launch ausführen" }, confirmations.ToArray());
        Assert.Equal(new[] { 1, 3, 5 }, provider.HistorySizes.ToArray());
    }

    [Fact]
    public async Task Declined_and_failing_tools_are_reported_back_to_the_model()
    {
        var host = new FakeHost();
        var provider = new ScriptedProvider(
            new ChatReply(null, [new ToolCall("a", "power", Args("""{"action":"shutdown"}""")), new ToolCall("b", "launch", Args("""{"id":"missing"}"""))]),
            new ChatReply("Okay, nichts gemacht.", []));
        var session = new AssistantSession(provider, host);

        await session.AskAsync("Mach aus und starte was", (call, _) => Task.FromResult(call.Name != "power"));

        Assert.Equal(new[] { "launch" }, host.Called.ToArray());
        var results = session.History[2].Results;
        Assert.True(results[0].IsError);
        Assert.Contains("declined", results[0].Content);
        Assert.True(results[1].IsError);
        Assert.Equal("Nicht gefunden", results[1].Content);
    }

    [Fact]
    public async Task A_failed_request_leaves_the_history_usable()
    {
        var provider = new ScriptedProvider(new ChatReply(null, [new ToolCall("t1", "get_session", Args("{}"))]));
        var session = new AssistantSession(provider, new FakeHost());

        await Assert.ThrowsAsync<AssistantException>(() => session.AskAsync("Was läuft?", (_, _) => Task.FromResult(true)));

        Assert.Empty(session.History);
    }

    [Fact]
    public async Task Unexpected_tool_errors_become_results_and_empty_replies_leave_no_trace()
    {
        var provider = new ScriptedProvider(
            new ChatReply(null, [new ToolCall("t1", "launch", Args("""{"id":"broken"}"""))]),
            new ChatReply(null, []));
        var session = new AssistantSession(provider, new FakeHost());

        await Assert.ThrowsAsync<AssistantException>(() => session.AskAsync("Starte", (_, _) => Task.FromResult(true)));
        // The failing tool was reported (third message), then the empty answer rolled the turn back.
        Assert.Equal(new[] { 1, 3 }, provider.HistorySizes.ToArray());
        Assert.Empty(session.History);
    }

    /// <summary>A provider plugin as the SDK sees it (the vendors' own tests live in their plugin repos since 0.7).</summary>
    private sealed class FakePluginProvider(Func<PluginChatReply?>? reply = null, Exception? fail = null, TimeSpan? delay = null) : CouchHud.Sdk.IPluginChatProvider
    {
        public string? Model { get; private set; }
        public IReadOnlyList<PluginChatMessage> Sent { get; private set; } = [];
        public IReadOnlyList<PluginChatTool> Tools { get; private set; } = [];
        public string Id => "fake";
        public string Name => "Fake\u0007 AI";
        public IReadOnlyList<string> Models => ["m1"];
        public bool HasKey => true;
        public void SetKey(string? key) { }

        public async Task<PluginChatReply> CompleteAsync(string model, string system, IReadOnlyList<PluginChatMessage> history, IReadOnlyList<PluginChatTool> tools, CancellationToken ct)
        {
            Model = model;
            Sent = history;
            Tools = tools;
            if (delay is { } d) await Task.Delay(d, ct);
            if (fail is not null) throw fail;
            return reply?.Invoke()!;
        }
    }

    [Fact]
    public async Task Plugin_adapter_hands_the_conversation_over_in_sdk_types()
    {
        var plugin = new FakePluginProvider(() => new PluginChatReply("ok", []));
        var adapter = new PluginChatAdapter(plugin, "m1", TimeSpan.FromSeconds(5));
        var history = new List<ChatMessage>
        {
            ChatMessage.User("Hi"),
            new() { Role = "assistant", Calls = [new ToolCall("c1", "set_volume", Args("""{"percent":30}"""))] },
            new() { Role = "user", Results = [new ToolResult("c1", "done", IsError: true)] }
        };

        var reply = await adapter.CompleteAsync("sys", history, ToolCatalog.All, default);

        Assert.Equal("ok", reply.Text);
        Assert.Equal("m1", plugin.Model);
        Assert.Equal(3, plugin.Sent.Count);
        Assert.Equal("""{"percent":30}""", plugin.Sent[1].Calls![0].ArgumentsJson);
        Assert.True(plugin.Sent[2].Results![0].IsError);
        Assert.Equal(ToolCatalog.All.Count, plugin.Tools.Count);
        Assert.Equal("object", JsonNode.Parse(plugin.Tools[0].InputSchemaJson)!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Plugin_adapter_bounds_and_cleans_the_answer()
    {
        var calls = Enumerable.Range(0, 40).Select(i => new PluginToolCall($"c{i}", "get_audio", i == 0 ? "{not json" : i == 1 ? "[1,2]" : "{}")).ToList();
        calls.Add(new PluginToolCall("x", "", "{}"));
        var plugin = new FakePluginProvider(() => new PluginChatReply(new string('a', PluginChatAdapter.MaxText + 10), calls));

        var reply = await new PluginChatAdapter(plugin, "m1", TimeSpan.FromSeconds(5)).CompleteAsync("s", [ChatMessage.User("Hi")], [], default);

        Assert.Equal(PluginChatAdapter.MaxText, reply.Text!.Length);
        Assert.Equal(PluginChatAdapter.MaxCalls, reply.Calls.Count);
        Assert.All(reply.Calls, c => Assert.Equal(JsonValueKind.Object, c.Arguments.ValueKind));
    }

    [Theory]
    [InlineData(PluginChatError.KeyInvalid, "assistant.keyInvalid")]
    [InlineData(PluginChatError.RateLimited, "assistant.rateLimited")]
    [InlineData(PluginChatError.ModelNotFound, "assistant.modelNotFound")]
    [InlineData(PluginChatError.TooLong, "assistant.tooLong")]
    public async Task Plugin_adapter_words_the_known_failures_itself(PluginChatError error, string key)
    {
        var plugin = new FakePluginProvider(fail: new PluginChatException(error, "sk-secret-from-plugin"));
        var ex = await Assert.ThrowsAsync<AssistantException>(() => new PluginChatAdapter(plugin, "m1", TimeSpan.FromSeconds(5)).CompleteAsync("s", [ChatMessage.User("Hi")], [], default));
        Assert.Equal(MegaHub.Core.Localization.Strings.Get(key), ex.Message);
    }

    [Fact]
    public async Task Plugin_adapter_never_passes_a_plugin_failure_on_as_it_is()
    {
        var other = new FakePluginProvider(fail: new PluginChatException(PluginChatError.Other, "boom\u0007\nline"));
        var ex = await Assert.ThrowsAsync<AssistantException>(() => new PluginChatAdapter(other, "m1", TimeSpan.FromSeconds(5)).CompleteAsync("s", [], [], default));
        Assert.DoesNotContain('\u0007', ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.Contains("Fake AI", ex.Message);

        var crash = new FakePluginProvider(fail: new NullReferenceException("secret detail"));
        ex = await Assert.ThrowsAsync<AssistantException>(() => new PluginChatAdapter(crash, "m1", TimeSpan.FromSeconds(5)).CompleteAsync("s", [], [], default));
        Assert.DoesNotContain("secret detail", ex.Message);

        var blocked = new FakePluginProvider(fail: new HttpRequestException("blocked by privacy"));
        ex = await Assert.ThrowsAsync<AssistantException>(() => new PluginChatAdapter(blocked, "m1", TimeSpan.FromSeconds(5)).CompleteAsync("s", [], [], default));
        Assert.Equal("blocked by privacy", ex.Message);

        var empty = new FakePluginProvider(() => null);
        await Assert.ThrowsAsync<AssistantException>(() => new PluginChatAdapter(empty, "m1", TimeSpan.FromSeconds(5)).CompleteAsync("s", [], [], default));
    }

    [Fact]
    public async Task Plugin_adapter_times_out_but_passes_the_callers_cancel_on()
    {
        var slow = new FakePluginProvider(() => new PluginChatReply("late", []), delay: TimeSpan.FromSeconds(30));
        var ex = await Assert.ThrowsAsync<AssistantException>(() => new PluginChatAdapter(slow, "m1", TimeSpan.FromMilliseconds(50)).CompleteAsync("s", [], [], default));
        Assert.Equal(MegaHub.Core.Localization.Strings.Get("assistant.timeout"), ex.Message);

        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PluginChatAdapter(slow, "m1", TimeSpan.FromSeconds(30)).CompleteAsync("s", [], [], cts.Token));
    }

    [Fact]
    public void Catalog_never_exposes_secrets_and_names_are_unique()
    {
        Assert.Equal(ToolCatalog.All.Count, ToolCatalog.All.Select(t => t.Name).Distinct().Count());
        Assert.DoesNotContain(ToolCatalog.SettingKeys, k => k.Contains("key", StringComparison.OrdinalIgnoreCase) || k.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.True(ToolCatalog.Find("power")!.Writes);
        Assert.False(ToolCatalog.Find("search_library")!.Writes);
    }

    [Fact]
    public async Task Mcp_initialize_list_and_call()
    {
        var host = new FakeHost();
        var server = new McpServer(host, "1.2.3");

        var init = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}"""));
        Assert.Equal("Mega Hub", init!["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("2025-06-18", init["result"]!["protocolVersion"]!.GetValue<string>());

        Assert.Null(await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")));

        var list = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":"two","method":"tools/list"}"""));
        Assert.Equal("two", list!["id"]!.GetValue<string>());
        Assert.Equal(ToolCatalog.All.Count, list["result"]!["tools"]!.AsArray().Count);

        var call = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search_library","arguments":{"query":"cyber"}}}"""));
        Assert.False(call!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("Cyberpunk", call["result"]!["content"]![0]!["text"]!.GetValue<string>());

        var failed = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"launch","arguments":{"id":"missing"}}}"""));
        Assert.True(failed!["result"]!["isError"]!.GetValue<bool>());

        var unknown = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":5,"method":"resources/list"}"""));
        Assert.Equal(-32601, unknown!["error"]!["code"]!.GetValue<int>());

        var malformed = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":5}}"""));
        Assert.Equal(-32602, malformed!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Rescanning_the_library_over_mcp_needs_the_confirmation_but_stays_a_read_for_the_assistant()
    {
        var spec = ToolCatalog.Find("rescan_library")!;
        Assert.True(spec.NeedsConfirmationOverMcp);
        Assert.False(spec.Writes);
        Assert.False(ToolCatalog.AllowedWhileLocked("rescan_library"));

        var host = new FakeHost();
        var server = new McpServer(host, "1");
        var refused = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"rescan_library","arguments":{}}}"""));
        Assert.True(refused!["result"]!["isError"]!.GetValue<bool>());
        Assert.Empty(host.Called);
    }

    [Fact]
    public async Task Mcp_writes_without_a_confirmation_hook_are_refused_not_skipped()
    {
        var host = new FakeHost();
        var server = new McpServer(host, "1");

        var write = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"launch","arguments":{"id":"steam:1"}}}"""));
        var read = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search_library","arguments":{}}}"""));

        Assert.True(write!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("not run", write["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.False(read!["result"]!["isError"]!.GetValue<bool>());
        // The write never reached the host.
        Assert.Equal(new[] { "search_library" }, host.Called.ToArray());
    }

    [Fact]
    public async Task Mcp_writes_need_confirmation()
    {
        var host = new FakeHost();
        var server = new McpServer(host, "1") { Confirm = (_, _) => Task.FromResult(false) };

        var declined = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"launch","arguments":{"id":"steam:1"}}}"""));
        var read = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search_library","arguments":{}}}"""));

        Assert.True(declined!["result"]!["isError"]!.GetValue<bool>());
        Assert.False(read!["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(new[] { "search_library" }, host.Called.ToArray());
    }
}
