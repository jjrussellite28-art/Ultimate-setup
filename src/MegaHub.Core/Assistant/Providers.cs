using System.Text.Json;
using MegaHub.Core.Localization;
using CouchHud.Sdk;

namespace MegaHub.Core.Assistant;

public sealed record ToolCall(string Id, string Name, JsonElement Arguments);

public sealed record ToolResult(string CallId, string Content, bool IsError = false);

/// <summary>Provider-neutral conversation entry.</summary>
public sealed class ChatMessage
{
    /// <summary>"user" or "assistant". Tool results travel as user messages.</summary>
    public required string Role { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<ToolCall> Calls { get; init; } = [];
    public IReadOnlyList<ToolResult> Results { get; init; } = [];

    public static ChatMessage User(string text) => new() { Role = "user", Text = text };
}

public sealed record ChatReply(string? Text, IReadOnlyList<ToolCall> Calls);

public interface IChatProvider
{
    string Name { get; }
    Task<ChatReply> CompleteAsync(string system, IReadOnlyList<ChatMessage> history, IReadOnlyList<ToolSpec> tools, CancellationToken ct);
}

public sealed class AssistantException(string message) : Exception(message);

/// <summary>
/// The assistant's view of a provider plugin (SDK 5, <see cref="IPluginChatProvider"/>): the conversation goes over in the SDK's plain
/// types (JSON as text), the answer comes back the same way, and the plugin's failures become the messages the assistant shows. The
/// vendors' APIs live in the plugins; the core only knows this contract.
/// </summary>
public sealed class PluginChatAdapter(IPluginChatProvider provider, string model, TimeSpan timeout) : IChatProvider
{
    /// <summary>Longest text a provider may hand back (a runaway plugin must not flood the conversation).</summary>
    public const int MaxText = 64 * 1024;

    public const int MaxCalls = 16;

    public string Name => model;

    private string ProviderName
    {
        get
        {
            try
            {
                return Clean(provider.Name, 40);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return "?";
            }
        }
    }

    public async Task<ChatReply> CompleteAsync(string system, IReadOnlyList<ChatMessage> history, IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        var messages = history.Select(ToSdk).ToList();
        var specs = tools.Select(t => new PluginChatTool(t.Name, t.Description, t.Schema.ToJsonString())).ToList();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        PluginChatReply reply;
        try
        {
            reply = await provider.CompleteAsync(model, system, messages, specs, cts.Token).ConfigureAwait(false)
                    ?? throw new AssistantException(Strings.Get("assistant.emptyReply"));
        }
        catch (PluginChatException ex)
        {
            throw new AssistantException(ex.Error switch
            {
                PluginChatError.KeyInvalid => Strings.Get("assistant.keyInvalid"),
                PluginChatError.RateLimited => Strings.Get("assistant.rateLimited"),
                PluginChatError.ModelNotFound => Strings.Get("assistant.modelNotFound"),
                PluginChatError.TooLong => Strings.Get("assistant.tooLong"),
                _ => Strings.Get("assistant.providerError", ProviderName, Clean(ex.Message, 200))
            });
        }
        catch (HttpRequestException ex)
        {
            // The privacy settings changed under a running conversation (or the network is gone): a plain message, not a crash.
            throw new AssistantException(Clean(ex.Message, 200));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AssistantException(Strings.Get("assistant.timeout"));
        }
        catch (Exception ex) when (ex is not (AssistantException or OperationCanceledException or OutOfMemoryException))
        {
            // Whatever else a plugin throws is its fault: shown as a provider error, never passed on as it is.
            throw new AssistantException(Strings.Get("assistant.providerError", ProviderName, ex.GetType().Name));
        }
        return FromSdk(reply);
    }

    internal static PluginChatMessage ToSdk(ChatMessage m) => new(
        m.Role,
        m.Text,
        m.Calls.Select(c => new PluginToolCall(c.Id, c.Name, c.Arguments.ValueKind == JsonValueKind.Undefined ? "{}" : c.Arguments.GetRawText())).ToList(),
        m.Results.Select(r => new PluginToolResult(r.CallId, r.Content, r.IsError)).ToList());

    /// <summary>The plugin's answer, bounded and cleaned: text cut, at most 16 calls, arguments that are not a JSON object become {}.</summary>
    internal static ChatReply FromSdk(PluginChatReply reply)
    {
        var calls = new List<ToolCall>();
        foreach (var c in (reply.Calls ?? []).Take(MaxCalls))
        {
            if (c is null || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 128) continue;
            calls.Add(new ToolCall(Clean(c.Id, 128), c.Name, Arguments(c.ArgumentsJson)));
        }
        var text = reply.Text is { } t ? t.Length > MaxText ? t[..MaxText] : t : null;
        return new ChatReply(text, calls);
    }

    private static JsonElement Arguments(string? json)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(json) && json.Length <= MaxText)
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
        }
        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    private static string Clean(string? text, int max)
    {
        var s = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > max ? s[..max] : s;
    }
}
