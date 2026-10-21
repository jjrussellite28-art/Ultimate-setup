using System.Text.RegularExpressions;

namespace CouchHud.Core.Integrations;

/// <summary>
/// Which Home Assistant things may be switched, and when. The couch lock keeps a guest away from doors, garage gates,
/// scripts and alarms; what the id of an entity may look like is checked before it goes into a request.
/// </summary>
public static partial class HomeAssistantPolicy
{
    [GeneratedRegex(@"^[a-z_]+\.[a-z0-9_]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex EntityIdPattern();

    /// <summary>"light.sofa": domain, a dot, an object id. Commas, slashes or anything else that could mean more than one entity or another URL are out.</summary>
    public static bool IsValidEntityId(string? entityId) =>
        !string.IsNullOrEmpty(entityId) && entityId.Length <= 255 && EntityIdPattern().IsMatch(entityId);

    public static string DomainOf(string entityId) => entityId[..entityId.IndexOf('.')];

    /// <summary>Always fine, locked or not: lights, scenes and the media players of the room.</summary>
    private static readonly HashSet<string> SafeWhileLocked = ["light", "scene", "media_player"];

    /// <summary>Fine locked only when the owner pinned them for the guide menu: plain switches.</summary>
    private static readonly HashSet<string> PinnedWhileLocked = ["switch", "fan", "input_boolean"];

    /// <summary>
    /// Locked couch: lights, scenes and media players; switches, fans and input booleans only if pinned.
    /// Covers (garage, blinds), scripts, automations, buttons, locks and alarms: only when unlocked.
    /// </summary>
    public static bool AllowedWhileLocked(string entityId, bool pinned)
    {
        if (!IsValidEntityId(entityId)) return false;
        var domain = DomainOf(entityId);
        return SafeWhileLocked.Contains(domain) || (pinned && PinnedWhileLocked.Contains(domain));
    }

    /// <summary>The address a token belongs to: a new one means the old token must not go there.</summary>
    public static bool SameServer(string? before, string? after) =>
        string.Equals(Normalize(before), Normalize(after), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? url) => (url ?? "").Trim().TrimEnd('/');
}
