namespace MegaHub.Core.Remote;

/// <summary>
/// The settings that open a listener on the network. Switching one on is a parents' decision with parental controls on (VM test 0.7 B,
/// F5): a child with the couch code alone must not open a door to the home network. Switching off is free. One list for the host and the
/// page (the page asks the parents' code for exactly these before the save; the host asks again).
/// </summary>
public static class NetworkListeners
{
    /// <summary>The action of the parents' code (<c>Hud.RequireNetworkListenerGrant</c>).</summary>
    public const string Action = "networkListener";

    /// <summary>Phone connection, MCP server, MCP in the home network.</summary>
    public static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.Ordinal) { "remoteEnabled", "mcpEnabled", "mcpLan" };

    public static bool IsListener(string key) => Keys.Contains(key);

    /// <summary>
    /// Whether this change takes the parents' code: switching a listener ON with parental controls on. Everything else (off, another key,
    /// parental controls off, where the unlocked couch the setter needs anyway is enough) does not. The page keeps the same rule in
    /// <c>listenerNeedsParent</c> (ui/core/net-rules.js); a test holds them equal.
    /// </summary>
    public static bool NeedsParentGrant(string key, bool turningOn, bool parentalOn) => turningOn && parentalOn && IsListener(key);
}
