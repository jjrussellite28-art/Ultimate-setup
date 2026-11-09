using MegaHub.Core.Plugins;
using CouchHud.Sdk;

namespace MegaHub.Core.Tests;

/// <summary>SDK 5, "tv.control": who may hold it, what the install lint says, and the names kept for Mega Hub's own TV plugins.</summary>
public class TvPluginPolicyTests
{
    [Fact]
    public void Tv_control_is_a_known_capability_of_its_own_context_member()
    {
        Assert.Contains(PluginCapabilities.TvControl, PluginCapabilities.All);
        Assert.Equal(PluginCapabilities.TvControl, PluginPolicy.ContextMembers["RegisterTvProvider"]);
        Assert.Equal(5, TvSdk.MinSdk);
        Assert.True(SdkVersion.Current >= TvSdk.MinSdk);
    }

    [Fact]
    public void A_third_party_tv_plugin_holds_no_network_native_code_or_programs_of_its_own()
    {
        Assert.Null(PluginPolicy.CheckDeclared(PluginOrigin.ThirdParty, [PluginCapabilities.TvControl, PluginCapabilities.Pages]));
        foreach (var other in new[] { PluginCapabilities.NetworkLan, PluginCapabilities.NetworkInternet, PluginCapabilities.NetworkLoopback, PluginCapabilities.Native, PluginCapabilities.Process })
        {
            Assert.Equal("plugin.cap.tvAlone", PluginPolicy.CheckDeclared(PluginOrigin.ThirdParty, [PluginCapabilities.TvControl, other]));
            Assert.Equal("plugin.cap.tvAlone", PluginPolicy.CheckDeclared(PluginOrigin.Local, [PluginCapabilities.TvControl, other]));
            // Fail closed even when the check was skipped somewhere: the grant drops the TV, never the other way round.
            var granted = PluginPolicy.Granted(PluginOrigin.ThirdParty, [PluginCapabilities.TvControl, other], 5, [PluginCapabilities.TvControl, other]);
            Assert.DoesNotContain(PluginCapabilities.TvControl, granted);
        }
        Assert.Contains(PluginCapabilities.TvControl, PluginPolicy.Granted(PluginOrigin.ThirdParty, [PluginCapabilities.TvControl], 5, [PluginCapabilities.TvControl]));
        // Recorded at install without it: an update cannot add it.
        Assert.DoesNotContain(PluginCapabilities.TvControl, PluginPolicy.Granted(PluginOrigin.ThirdParty, [PluginCapabilities.TvControl], 5, [PluginCapabilities.Pages]));
        // Mega Hub's own plugins are reviewed with their network use.
        Assert.Null(PluginPolicy.CheckDeclared(PluginOrigin.FirstParty, [PluginCapabilities.TvControl, PluginCapabilities.NetworkLan]));
    }

    [Fact]
    public void The_lint_flags_tv_types_without_the_capability_or_below_sdk_5()
    {
        // This test assembly implements ITvProvider and uses ITvLink, like a TV plugin.
        var image = File.ReadAllBytes(typeof(TvPluginPolicyTests).Assembly.Location);
        var found = PluginLint.Scan(image, "t.dll");
        Assert.Contains(found, f => f.Capability == PluginCapabilities.TvControl);
        Assert.Contains(found, f => f.Capability == PluginLint.SdkTv);
        Assert.Contains(PluginLint.Evaluate(found, PluginOrigin.ThirdParty, [], 5), f => f.Capability == PluginCapabilities.TvControl);
        Assert.Contains(PluginLint.Evaluate(found, PluginOrigin.ThirdParty, [PluginCapabilities.TvControl], 4), f => f.Capability == PluginLint.SdkTv);
        var ok = PluginLint.Evaluate(found, PluginOrigin.ThirdParty, [PluginCapabilities.TvControl], 5);
        Assert.DoesNotContain(ok, f => f.Capability == PluginCapabilities.TvControl || f.Capability == PluginLint.SdkTv);
    }

    [Fact]
    public void The_names_of_the_official_tv_plugins_are_kept()
    {
        Assert.Contains("webostv", PluginNames.Reserved);
        Assert.Contains("samsungtv", PluginNames.Reserved);
    }
}
