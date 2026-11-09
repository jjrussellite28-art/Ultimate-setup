using System.Text.RegularExpressions;
using MegaHub.Core.Assistant;
using MegaHub.Core.Remote;

namespace MegaHub.Core.Tests;

/// <summary>VM test 0.7 B, F5: switching on a network listener takes the parents' code with parental controls on.</summary>
public class NetworkListenerTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MegaHub.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "the repository root (MegaHub.slnx) was not found: the source checks cannot run");
        return dir!.FullName;
    }

    [Fact]
    public void The_list_is_phone_connection_mcp_and_mcp_in_the_home_network()
    {
        Assert.Equal(["mcpEnabled", "mcpLan", "remoteEnabled"], NetworkListeners.Keys.OrderBy(k => k).ToArray());
        Assert.True(NetworkListeners.IsListener("remoteEnabled"));
        Assert.False(NetworkListeners.IsListener("remotePower"));
        Assert.False(NetworkListeners.IsListener("RemoteEnabled"));
        Assert.True(SecondFactorActions.GrantActions.ContainsKey(NetworkListeners.Action));
    }

    [Theory]
    [InlineData("remoteEnabled", true, true, true)]
    [InlineData("mcpEnabled", true, true, true)]
    [InlineData("mcpLan", true, true, true)]
    [InlineData("remoteEnabled", false, true, false)]
    [InlineData("mcpEnabled", false, true, false)]
    [InlineData("mcpLan", false, true, false)]
    [InlineData("remoteEnabled", true, false, false)]
    [InlineData("mcpEnabled", true, false, false)]
    [InlineData("mcpLan", true, false, false)]
    [InlineData("remotePower", true, true, false)]
    [InlineData("mcpToken", true, true, false)]
    [InlineData("", true, true, false)]
    [InlineData("REMOTEENABLED", true, true, false)]
    public void Only_switching_a_listener_on_with_parental_controls_on_takes_the_parents_code(string key, bool turningOn, bool parentalOn, bool expected) =>
        Assert.Equal(expected, NetworkListeners.NeedsParentGrant(key, turningOn, parentalOn));

    [Fact]
    public void The_page_keeps_the_same_list_and_the_same_rule()
    {
        var js = File.ReadAllText(Path.Combine(Root(), "ui", "core", "net-rules.js"));
        var list = Regex.Match(js, @"LISTENER_KEYS = \[([^\]]*)\]");
        Assert.True(list.Success, "LISTENER_KEYS not found in ui/core/net-rules.js");
        var keys = Regex.Matches(list.Groups[1].Value, "'([A-Za-z]+)'").Select(m => m.Groups[1].Value).OrderBy(k => k).ToArray();
        Assert.Equal(NetworkListeners.Keys.OrderBy(k => k).ToArray(), keys);
        Assert.Contains("!!turningOn && !!parentalOn && LISTENER_KEYS.includes(key)", js);
        Assert.Contains("'" + NetworkListeners.Action + "'", File.ReadAllText(Path.Combine(Root(), "ui", "views", "settings.js")));
    }

    [Fact]
    public void Neither_the_assistant_nor_the_phone_can_set_a_listener()
    {
        foreach (var key in NetworkListeners.Keys)
        {
            Assert.DoesNotContain(key, ToolCatalog.SettingKeys);
            Assert.DoesNotContain(RemoteSettings.All, s => s.Key == key);
        }
    }

    [Fact]
    public void Every_listener_setting_checks_the_grant_first_and_uses_it_up_only_after_the_listener_came_up()
    {
        var hud = File.ReadAllText(Path.Combine(Root(), "src", "MegaHub.Shell", "Hud.cs"));
        foreach (var key in NetworkListeners.Keys)
        {
            var start = hud.IndexOf($"case \"{key}\":", StringComparison.Ordinal);
            Assert.True(start > 0, $"case \"{key}\" not found in Hud.cs");
            var end = hud.IndexOf("break;", start, StringComparison.Ordinal);
            Assert.True(end > start, $"end of case \"{key}\" not found");
            var body = hud[start..end];
            var check = body.IndexOf("CheckNetworkListenerGrant(key,", StringComparison.Ordinal);
            var change = body.IndexOf("config.UpdateOrThrow", StringComparison.Ordinal);
            var apply = Math.Max(body.IndexOf("ApplyRemoteAsync(", StringComparison.Ordinal), body.IndexOf("ApplyMcpAsync(", StringComparison.Ordinal));
            var use = body.IndexOf("UseListenerGrantAsync(", StringComparison.Ordinal);
            Assert.True(check > 0, $"{key} has no grant check");
            Assert.True(check < change, $"{key}: the check must come before the change");
            Assert.True(apply > change, $"{key}: apply not found");
            Assert.True(use > apply, $"{key}: the grant is used up only after the listener came up");
        }
        Assert.Contains("NetworkListeners.NeedsParentGrant(key, turningOn, config.Current.ParentalEnabled)", hud);
        // Fail-closed: when the grant is gone after the listener came up, the change is undone and the listener goes down again before the refusal.
        // (Hud has no seam for a behavioural test; this pins the source.)
        var helper = hud.IndexOf("private async Task UseListenerGrantAsync(", StringComparison.Ordinal);
        Assert.True(helper > 0, "UseListenerGrantAsync not found");
        var helperBody = hud[helper..Math.Min(hud.Length, helper + 1800)];
        var use1 = helperBody.IndexOf("UseUp(ticket)", StringComparison.Ordinal);
        var revert = helperBody.IndexOf("revert();", StringComparison.Ordinal);
        var fin = helperBody.IndexOf("finally", revert, StringComparison.Ordinal);
        var reapply = helperBody.IndexOf("await reapply();", StringComparison.Ordinal);
        var rethrow = helperBody.LastIndexOf("throw;", StringComparison.Ordinal);
        Assert.True(use1 >= 0 && revert > use1 && fin > revert && reapply > fin && rethrow > reapply, "the grant helper must undo, then re-apply in a finally, then rethrow the refusal");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(helperBody, @"catch \(Exception ex\) when \(ex is not OutOfMemoryException\)").Count);
        Assert.DoesNotContain("catch (InvalidOperationException)", helperBody);
        // mcpLan undoes itself when the listener does not come up, like the other two.
        var lan = hud.IndexOf("case \"mcpLan\":", StringComparison.Ordinal);
        Assert.Contains("c.McpLan = false", hud[lan..hud.IndexOf("break;", lan, StringComparison.Ordinal)]);
    }
}
