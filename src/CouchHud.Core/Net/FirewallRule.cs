namespace CouchHud.Core.Net;

/// <summary>A rule of the Windows firewall as the Firewall API reports it (language independent), reduced to what the decisions need.</summary>
/// <param name="Allow">Action is allow (false: block).</param>
/// <param name="Inbound">Direction is in.</param>
/// <param name="Program">Path of the program the rule is for; null or empty when it applies to all.</param>
/// <param name="LocalPorts">"8766,8765", "*" or empty for all ports.</param>
/// <param name="Protocol">6 = TCP, 17 = UDP, 256 = any.</param>
/// <param name="RemoteAddresses">"LocalSubnet", "*" or addresses.</param>
public sealed record FirewallRuleInfo(string Name, bool Allow, bool Inbound, bool Enabled, string? Program, string? LocalPorts, int Protocol, string? RemoteAddresses);

/// <summary>
/// The inbound rule for the phone remote and the MCP server: only the ports in use, only TCP and only from
/// the local subnet. Building the arguments is kept apart from running netsh so it can be checked.
/// The name is ours alone. Windows names the rules it makes itself after the program ("CouchHUD": the block rules that appear when
/// the firewall prompt is cancelled), netsh finds rules by name only and does not tell case apart, so the earlier name "CouchHud"
/// would also have changed or deleted those. Everything here asks for <see cref="Name"/> and nothing else; rules of the earlier name
/// go only when they are verifiably ours (<see cref="IsOurLegacyRule"/>).
/// </summary>
public static class FirewallRule
{
    public const string Name = "CouchHUD Remote (CouchHUD)";

    /// <summary>The name earlier versions used; Windows' own rules for CouchHud.exe carry it too (see <see cref="IsOurLegacyRule"/>).</summary>
    public const string LegacyName = "CouchHud";

    /// <summary>"8766,8765" from the command line of the elevated helper; null if anything is not a valid port.</summary>
    public static IReadOnlyList<int>? ParsePorts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var ports = new List<int>();
        foreach (var part in text.Split(','))
        {
            if (!int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535) return null;
            if (!ports.Contains(port)) ports.Add(port);
        }
        return ports.Count is > 0 and <= 4 ? ports : null;
    }

    public static string[] AddArgs(string exe, IEnumerable<int> ports) =>
    [
        "advfirewall", "firewall", "add", "rule", $"name={Name}", "dir=in", "action=allow", $"program={exe}",
        "protocol=TCP", $"localport={string.Join(',', ports)}", "remoteip=localsubnet", "enable=yes", "profile=private,domain"
    ];

    /// <summary>One netsh command: a rule that exists is changed (any old program path, ports, scope; the action is set to allow), otherwise it is added.</summary>
    public static string[] AllowArgs(bool ruleExists, string exe, IEnumerable<int> ports) => ruleExists
        ?
        [
            "advfirewall", "firewall", "set", "rule", $"name={Name}", "new", $"program={exe}", "protocol=TCP", "action=allow",
            $"localport={string.Join(',', ports)}", "remoteip=localsubnet", "enable=yes", "profile=private,domain"
        ]
        : AddArgs(exe, ports);

    public static string[] DeleteArgs() => ["advfirewall", "firewall", "delete", "rule", $"name={Name}"];

    public static string[] ShowArgs() => ["advfirewall", "firewall", "show", "rule", $"name={Name}"];

    // ---- decisions on the rules that exist ------------------------------------------------------

    private static bool NamedOurs(FirewallRuleInfo r) => string.Equals(r.Name, Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Our rule (by its unique name) exists, whatever it does.</summary>
    public static bool Exists(IEnumerable<FirewallRuleInfo> rules) => rules.Any(NamedOurs);

    /// <summary>
    /// Our rule exists and is what it must be: inbound and allow. A rule of our name that is not (somebody turned it into a block
    /// rule) must be replaced, not just updated.
    /// </summary>
    public static bool IsAllowRule(IEnumerable<FirewallRuleInfo> rules) => rules.Where(NamedOurs).Any(r => r.Allow && r.Inbound);

    /// <summary>The rules of our name that are not an inbound allow rule.</summary>
    public static bool HasWrongRule(IEnumerable<FirewallRuleInfo> rules) => rules.Where(NamedOurs).Any(r => !r.Allow || !r.Inbound);

    private static bool SamePath(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim().Trim('"'), b.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<int> PortsOf(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : -1)
            .ToList();

    /// <summary>
    /// A rule of the earlier name "CouchHud" that we made: inbound allow, TCP, narrowed to the local subnet and to specific ports (the
    /// ones we know, or the program is ours). Windows' own rules for CouchHud.exe carry the same name (differing in case only, which
    /// netsh ignores) but are block rules, or allow rules for all ports and all addresses: never ours, never touched.
    /// </summary>
    public static bool IsOurLegacyRule(FirewallRuleInfo r, string exe, IReadOnlyCollection<int> knownPorts)
    {
        if (!string.Equals(r.Name, LegacyName, StringComparison.OrdinalIgnoreCase)) return false;
        if (!r.Allow || !r.Inbound || r.Protocol != 6) return false;
        if ((r.RemoteAddresses ?? "").IndexOf("LocalSubnet", StringComparison.OrdinalIgnoreCase) < 0) return false;
        var ports = PortsOf(r.LocalPorts);
        if (ports.Count == 0 || ports.Any(p => p < 1024 || p > 65535)) return false;
        return SamePath(r.Program, exe) || ports.All(p => knownPorts.Contains(p));
    }

    /// <summary>The legacy rules among <paramref name="rules"/> that are ours.</summary>
    public static IReadOnlyList<FirewallRuleInfo> OurLegacyRules(IEnumerable<FirewallRuleInfo> rules, string exe, IReadOnlyCollection<int> knownPorts) =>
        rules.Where(r => IsOurLegacyRule(r, exe, knownPorts)).ToList();

    /// <summary>
    /// Deletes exactly one legacy rule of ours: by the earlier name <b>and</b> the program, TCP, the ports and the scope the rule has.
    /// A Windows block rule of the same name (all ports) does not match these and stays.
    /// </summary>
    public static string[] DeleteLegacyArgs(FirewallRuleInfo r)
    {
        var args = new List<string> { "advfirewall", "firewall", "delete", "rule", $"name={LegacyName}", "dir=in", "protocol=TCP", $"localport={r.LocalPorts!.Replace(" ", "")}", "remoteip=localsubnet" };
        if (!string.IsNullOrWhiteSpace(r.Program)) args.Add($"program={r.Program!.Trim().Trim('"')}");
        return args.ToArray();
    }

    /// <summary>
    /// Windows' own inbound block rules for this program: they win against our allow rule (a block rule beats an allow rule), so the
    /// phone could not connect however often the rule is set. Turning them off is the user's decision; they are reported.
    /// </summary>
    public static IReadOnlyList<FirewallRuleInfo> BlockingRules(IEnumerable<FirewallRuleInfo> rules, string exe) =>
        rules.Where(r => r.Enabled && !r.Allow && r.Inbound && SamePath(r.Program, exe)).ToList();
}
