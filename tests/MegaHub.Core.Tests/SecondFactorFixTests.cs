using MegaHub.Core.Plugins;
using MegaHub.Core.Remote;
using CouchHud.Sdk;

namespace MegaHub.Core.Tests;

/// <summary>Findings of the second-factor review: enabling a plugin, locked and all-game companions, function levels across uninstall, the tray dispose.</summary>
public class SecondFactorFixTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MegaHub.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Shell(string file) => File.ReadAllText(Path.Combine(RepoRoot(), "src", "MegaHub.Shell", file)).Replace("\r\n", "\n");

    private static string Body(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, signature);
        var rest = source[at..];
        var end = rest.IndexOf("\n    }\n", StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }

    [Theory]
    [InlineData(PluginOrigin.ThirdParty, null, true)]
    [InlineData(PluginOrigin.Local, null, true)]
    [InlineData(PluginOrigin.FirstParty, null, false)]
    [InlineData(PluginOrigin.FirstParty, new[] { "library", "pages" }, false)]
    [InlineData(PluginOrigin.FirstParty, new[] { "library", "network:lan" }, true)]
    [InlineData(PluginOrigin.FirstParty, new[] { "network:internet" }, true)]
    [InlineData(PluginOrigin.FirstParty, new[] { "network:loopback" }, true)]
    [InlineData(PluginOrigin.FirstParty, new[] { "assistant" }, true)]
    [InlineData(PluginOrigin.FirstParty, new[] { "process" }, true)]
    public void Enabling_needs_the_second_factor_unless_it_is_a_plain_first_party_plugin(PluginOrigin origin, string[]? caps, bool needed) =>
        Assert.Equal(needed, PluginPolicy.EnableNeedsSecondFactor(origin, caps));

    [Fact]
    public void Enable_asks_before_the_write_and_disable_stays_free()
    {
        var body = Body(Shell("Hud.cs"), "public async Task<object> SetPluginEnabledAsync(");
        Assert.Contains("if (enabled && PluginPolicy.EnableNeedsSecondFactor(", body);
        Assert.Contains("RequireSecondFactorFor(SecondFactorActions.PluginRights", body);
        Assert.True(body.IndexOf("RequireSecondFactorFor", StringComparison.Ordinal) < body.IndexOf("config.UpdateOrThrow", StringComparison.Ordinal));
        Assert.Equal(SecondFactorActions.PluginRights, SecondFactorActions.GrantActions["pluginEnable"].Action);
        Assert.Null(SecondFactorActions.GrantActions["pluginDisable"].Action);
    }

    [Fact]
    public void Uninstall_reason_names_the_kept_levels()
    {
        var cover = SecondFactorActions.GrantActions["pluginUninstall"];
        Assert.Null(cover.Action);
        Assert.Contains("function levels stay", cover.Reason);
    }

    [Fact]
    public void Companions_ask_only_when_a_rule_newly_starts_on_the_locked_couch_or_beside_every_game()
    {
        var hud = Shell("HudCompanions.cs");
        var set = Body(hud, "public object SetCompanion(");
        Assert.Contains("RequireSecondFactorFor(SecondFactorActions.CompanionLocked", set);
        Assert.Contains("nowLocked && before?.WhenLocked != true", set);
        Assert.Contains("nowAll && before?.Mode != CompanionRule.ModeAll", set);
        Assert.True(set.IndexOf("RequireSecondFactorFor", StringComparison.Ordinal) < set.IndexOf("config.UpdateOrThrow", StringComparison.Ordinal));
        Assert.DoesNotContain("RequireSecondFactor", Body(hud, "public object RemoveCompanion("));
        var wand = Body(hud, "public async Task<object> SetupWandAsync(");
        Assert.Contains("if (withEveryGame && verified)", wand);
        Assert.True(wand.IndexOf("RequireSecondFactorFor", StringComparison.Ordinal) < wand.IndexOf("AddEntriesAsync", StringComparison.Ordinal));
        Assert.Contains(SecondFactorActions.Critical, a => a.Id == SecondFactorActions.CompanionLocked);
        Assert.Equal(SecondFactorActions.CompanionLocked, SecondFactorActions.GrantActions["companions"].Action);
    }

    [Fact]
    public void Leaving_pc_mode_disposes_the_tray_at_once_on_the_ui_thread()
    {
        var hud = Shell("Hud.cs");
        var leave = Body(hud, "public Task LeavePcModeAsync()");
        Assert.Contains("DisposeTrayNow();", leave);
        Assert.DoesNotContain("_tray?.Dispose()", leave);
        var dispose = Body(hud, "private void DisposeTrayNow()");
        Assert.Contains("dispatcher.CheckAccess()", dispose);
        Assert.Contains("dispatcher.Invoke(", dispose);
        Assert.DoesNotContain("BeginInvoke", dispose);
    }
}
