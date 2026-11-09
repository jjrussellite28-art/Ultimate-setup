namespace MegaHub.Core.Remote;

/// <summary>
/// Mega Hub: the named commands of the Android companion (docs: android-companion-protocol.md, version 1). They are no new way in: each one
/// ends in a button of the phone remote (<c>RemoteHost.Buttons</c>), the same internal navigation as the d-pad on the phone page.
/// Nothing else is accepted, and the name has to match exactly.
/// </summary>
public static class RemoteCommands
{
    public const string Ping = "session.ping";

    private static readonly IReadOnlyDictionary<string, string> Buttons = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["navigate.up"] = "up",
        ["navigate.down"] = "down",
        ["navigate.left"] = "left",
        ["navigate.right"] = "right",
        ["navigate.select"] = "a",
        ["navigate.back"] = "b"
    };

    /// <summary>The names of version 1, in the order of the protocol document.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Buttons.Keys, Ping];

    /// <summary>The remote button for a navigation command; null for anything else (also for the ping, which presses nothing).</summary>
    public static string? ButtonFor(string? command) => command is not null && Buttons.TryGetValue(command, out var button) ? button : null;
}
