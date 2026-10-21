using System.Text.Json;

namespace CouchHud.Sdk;

// SDK v1: plugins describe their UI as data. CouchHud renders it in the current
// skin, so every plugin screen looks right and works with the controller.

/// <summary>One row on a plugin page.</summary>
public sealed record PluginRow(string Id, string Label)
{
    /// <summary>Second line under the label.</summary>
    public string? Detail { get; init; }

    /// <summary>A short symbol or emoji on the left.</summary>
    public string? Icon { get; init; }

    /// <summary>Value shown on the right, e.g. "An", "42 %", "Leise".</summary>
    public string? Value { get; init; }

    /// <summary>Shows <see cref="Value"/> as active (accent color).</summary>
    public bool On { get; init; }
}

/// <summary>A full-screen list page, reachable from the plugin's entry in the settings.</summary>
public sealed class PluginPage
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string Icon { get; init; } = "◆";

    /// <summary>Called every time the page opens and after each activation.</summary>
    public required Func<CancellationToken, Task<IReadOnlyList<PluginRow>>> Rows { get; init; }

    /// <summary>A pressed on a row. Return a short message for a toast, or null.</summary>
    public Func<string, CancellationToken, Task<string?>>? Activate { get; init; }
}

/// <summary>An entry in the guide menu, e.g. "Licht aus" or "Lüfter leise".</summary>
public sealed class QuickAction
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string Icon { get; init; } = "◆";

    /// <summary>Second line, asked for each time the guide menu opens.</summary>
    public Func<string?>? Detail { get; init; }

    /// <summary>Return a short message for a toast, or null.</summary>
    public required Func<CancellationToken, Task<string?>> Run { get; init; }
}

public enum SettingKind
{
    Toggle,
    Text,
    /// <summary>Like text, but never shown again (tokens, passwords).</summary>
    Secret,
    Choice
}

/// <summary>A setting on the plugin's page in the settings. Values are stored by CouchHud.</summary>
public sealed class PluginSetting
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public SettingKind Kind { get; init; } = SettingKind.Toggle;

    /// <summary>"true"/"false" for toggles, one of <see cref="Choices"/> for a choice.</summary>
    public string? Default { get; init; }

    public IReadOnlyList<string> Choices { get; init; } = [];
}

/// <summary>
/// An operation for the AI assistant and MCP clients. Tools that change something
/// (<see cref="Writes"/>) are confirmed by the user on the TV before they run.
/// </summary>
public sealed class PluginTool
{
    /// <summary>snake_case; CouchHud prefixes it with the plugin id.</summary>
    public required string Name { get; init; }

    /// <summary>English works best for the models.</summary>
    public required string Description { get; init; }

    /// <summary>JSON schema of the arguments, e.g. {"type":"object","properties":{}}.</summary>
    public string InputSchema { get; init; } = """{"type":"object","properties":{}}""";

    public bool Writes { get; init; }

    /// <summary>Returns text (often JSON) for the model.</summary>
    public required Func<JsonElement, CancellationToken, Task<string>> Run { get; init; }
}

/// <summary>A game as plugins see it in events.</summary>
public sealed record GameEvent(string Id, string Name, string? InstallDir, double Minutes);

public interface IPluginEvents
{
    event Action<GameEvent>? GameStarted;

    /// <summary><see cref="GameEvent.Minutes"/> is the length of this session.</summary>
    event Action<GameEvent>? GameEnded;

    /// <summary>The user switched to the Windows desktop.</summary>
    event Action? PcModeEntered;

    /// <summary>Back on the couch from PC mode.</summary>
    event Action? CouchModeEntered;

    /// <summary>
    /// The user switched the language ("de" or "en"). Register settings, pages and quick actions again with the same
    /// ids: a registration replaces the earlier one with that id. Texts built on demand (rows, details) just ask
    /// <see cref="IPluginContext.Language"/> each time.
    /// </summary>
    event Action<string>? LanguageChanged
    {
        add { }
        remove { }
    }

    /// <summary>SDK 2: the couch was locked (true) or unlocked (false). <see cref="IPluginContext.Locked"/> has the current state.</summary>
    event Action<bool>? LockChanged
    {
        add { }
        remove { }
    }
}

/// <summary>Values of the plugin's <see cref="PluginSetting"/>s and anything else it wants to keep.</summary>
public interface IPluginSettings
{
    string? Get(string key);

    bool GetBool(string key, bool fallback = false);

    void Set(string key, string? value);
}
