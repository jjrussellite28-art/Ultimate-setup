using CouchHud.Core.Net;

namespace CouchHud.Privileged;

/// <summary>
/// The Windows firewall: its rules from the firewall API (HNetCfg.FwPolicy2: language independent, readable without administrator
/// rights) and netsh, whose arguments are always the fixed lists of <see cref="FirewallRule"/>.
/// </summary>
public sealed class WindowsFirewall(IProcessLauncher processes, IEventSink events) : IFirewall
{
    /// <summary>The rules of the Windows firewall; null when the API is not available (then only netsh by our own name is used, never by the old one).</summary>
    public IReadOnlyList<FirewallRuleInfo>? Rules()
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return null;
            dynamic policy = Activator.CreateInstance(type)!;
            var list = new List<FirewallRuleInfo>();
            foreach (dynamic rule in policy.Rules)
            {
                try
                {
                    list.Add(new FirewallRuleInfo(
                        (string?)rule.Name ?? "",
                        (int)rule.Action == 1,
                        (int)rule.Direction == 1,
                        (bool)rule.Enabled,
                        (string?)rule.ApplicationName,
                        (string?)rule.LocalPorts,
                        (int)rule.Protocol,
                        (string?)rule.RemoteAddresses));
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
                {
                    // A rule that cannot be read is not one of ours.
                }
            }
            return list;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException or NotSupportedException or System.Reflection.TargetInvocationException)
        {
            events.Error($"[remote] firewall rules not readable: {ex.Message}");
            return null;
        }
    }

    public int Netsh(IReadOnlyList<string> arguments) =>
        processes.Run(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments, TimeSpan.FromSeconds(15)).ExitCode;
}

/// <summary>What is done with the firewall rules of CouchHUD: found, removed (our rule and the verifiably ours of the earlier name).</summary>
public static class FirewallOps
{
    // The ports earlier versions used by default; a legacy rule with other ports is recognised by its program.
    public static readonly int[] DefaultPorts = [8766, 8765];

    /// <summary>Uninstall (elevated): our rule goes with the program, and the rules of the earlier name that are verifiably ours.</summary>
    public static void Remove(IFirewall firewall, string? exe)
    {
        firewall.Netsh(FirewallRule.DeleteArgs());
        if (firewall.Rules() is not { } rules || exe is null) return;
        foreach (var legacy in FirewallRule.OurLegacyRules(rules, exe, DefaultPorts)) firewall.Netsh(FirewallRule.DeleteLegacyArgs(legacy));
    }

    /// <summary>Is there a rule of ours (readable without administrator rights)? Also one of the earlier name.</summary>
    public static bool Exists(IFirewall firewall, string? exe)
    {
        if (firewall.Rules() is not { } rules) return firewall.Netsh(FirewallRule.ShowArgs()) == 0;
        return FirewallRule.Exists(rules)
               || (exe is not null && FirewallRule.OurLegacyRules(rules, exe, DefaultPorts).Count > 0);
    }

    /// <summary>
    /// Windows' own block rules for this program (they come from cancelling the firewall prompt) and beat our allow rule. The names, for the
    /// message to the user; empty when there are none (or the firewall cannot be read).
    /// </summary>
    public static IReadOnlyList<string> BlockingRuleNames(IFirewall firewall, string? exe) =>
        firewall.Rules() is { } rules && exe is not null
            ? FirewallRule.BlockingRules(rules, exe).Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
}
