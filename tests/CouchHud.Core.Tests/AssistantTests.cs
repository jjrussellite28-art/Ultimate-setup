using System.Text.Json;
using System.Text.Json.Nodes;
using CouchHud.Core.Assistant;

namespace CouchHud.Core.Tests;

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
        var provider = new ScriptedProvider(new ChatReply(null, [new ToolCall("t1", "get_weather", Args("{}"))]));
        var session = new AssistantSession(provider, new FakeHost());

        await Assert.ThrowsAsync<AssistantException>(() => session.AskAsync("Wetter?", (_, _) => Task.FromResult(true)));

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

    [Fact]
    public void OpenAi_reply_cut_off_by_the_token_limit_is_an_error()
    {
        Assert.Throws<AssistantException>(() => OpenAiProvider.ParseReply(JsonNode.Parse("""
            {"choices":[{"finish_reason":"length","message":{"content":""}}]}
            """)!));
        Assert.Equal("low", OpenAiProvider.BuildRequest("gpt-5-mini", "s", [], [])["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public void Anthropic_request_pairs_tool_use_with_results()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.User("Hi"),
            new() { Role = "assistant", Calls = [new ToolCall("tu1", "get_weather", Args("{}"))] },
            new() { Role = "user", Results = [new ToolResult("tu1", "sonnig")] }
        };

        var body = AnthropicProvider.BuildRequest("model-x", "sys", history, ToolCatalog.All);

        Assert.Equal("sys", body["system"]!.GetValue<string>());
        Assert.Equal(ToolCatalog.All.Count, body["tools"]!.AsArray().Count);
        Assert.Equal("object", body["tools"]![0]!["input_schema"]!["type"]!.GetValue<string>());
        var messages = body["messages"]!.AsArray();
        Assert.Equal("tool_use", messages[1]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("tu1", messages[2]!["content"]![0]!["tool_use_id"]!.GetValue<string>());
    }

    [Fact]
    public void Anthropic_reply_with_text_and_tool_use()
    {
        var reply = AnthropicProvider.ParseReply(JsonNode.Parse("""
            {"content":[{"type":"text","text":"Moment "},{"type":"tool_use","id":"x1","name":"launch","input":{"id":"steam:1"}}],"stop_reason":"tool_use"}
            """)!);

        Assert.Equal("Moment", reply.Text);
        var call = Assert.Single(reply.Calls);
        Assert.Equal("launch", call.Name);
        Assert.Equal("steam:1", call.Arguments.GetProperty("id").GetString());
    }

    [Fact]
    public void OpenAi_request_uses_system_and_tool_messages()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.User("Hi"),
            new() { Role = "assistant", Calls = [new ToolCall("c1", "get_audio", Args("{}"))] },
            new() { Role = "user", Results = [new ToolResult("c1", "{\"volume\":40}")] }
        };

        var body = OpenAiProvider.BuildRequest("gpt-x", "sys", history, ToolCatalog.All);
        var messages = body["messages"]!.AsArray();

        Assert.Equal(new[] { "system", "user", "assistant", "tool" }, messages.Select(m => m!["role"]!.GetValue<string>()).ToArray());
        Assert.Equal("{}", messages[2]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("c1", messages[3]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("function", body["tools"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAi_reply_tolerates_broken_arguments()
    {
        var reply = OpenAiProvider.ParseReply(JsonNode.Parse("""
            {"choices":[{"message":{"content":null,"tool_calls":[
              {"id":"a","type":"function","function":{"name":"set_volume","arguments":"{\"percent\":30}"}},
              {"id":"b","type":"function","function":{"name":"get_audio","arguments":"{not json"}}]}}]}
            """)!);

        Assert.Null(reply.Text);
        Assert.Equal(30, reply.Calls[0].Arguments.GetProperty("percent").GetInt32());
        Assert.Equal(JsonValueKind.Object, reply.Calls[1].Arguments.ValueKind);
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
        Assert.Equal("CouchHUD", init!["result"]!["serverInfo"]!["name"]!.GetValue<string>());
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
