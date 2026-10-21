using System.Net;
using CouchHud.Core.Net;
using CouchHud.Core.Remote;

namespace CouchHud.Core.Tests;

public class NetworkHardeningTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.0.0.7", true)]
    [InlineData("172.16.5.5", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("192.168.178.42", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("fe80::1ff:fe23:4567:890a", true)]
    [InlineData("fd12:3456:789a::1", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("0.0.0.0", false)]
    public void Only_private_link_local_and_loopback_peers_are_allowed(string address, bool allowed)
    {
        Assert.Equal(allowed, LanOnly.IsAllowed(IPAddress.Parse(address)));
    }

    [Fact]
    public void No_peer_address_means_no()
    {
        Assert.False(LanOnly.IsAllowed(null));
        Assert.False(LanOnly.IsLoopback(null));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.1.5", false)]
    public void Without_the_lan_option_only_this_machine_may_ask(string address, bool allowed)
    {
        Assert.Equal(allowed, LanOnly.IsLoopback(IPAddress.Parse(address)));
    }

    [Fact]
    public void The_firewall_rule_names_the_ports_and_the_local_subnet()
    {
        var args = FirewallRule.AddArgs(@"C:\Program Files\CouchHud\CouchHud.exe", [8766, 8765]);

        Assert.Contains("localport=8766,8765", args);
        Assert.Contains("remoteip=localsubnet", args);
        Assert.Contains("protocol=TCP", args);
        Assert.Contains("dir=in", args);
        Assert.Contains(@"program=C:\Program Files\CouchHud\CouchHud.exe", args);
        Assert.DoesNotContain(args, a => a.StartsWith("remoteip=any", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["advfirewall", "firewall", "delete", "rule", "name=CouchHUD Remote (CouchHUD)"], FirewallRule.DeleteArgs());
    }

    [Fact]
    public void An_existing_rule_is_changed_in_one_netsh_call_and_a_missing_one_added()
    {
        var set = FirewallRule.AllowArgs(true, @"C:\a\CouchHud.exe", [8766]);
        var add = FirewallRule.AllowArgs(false, @"C:\a\CouchHud.exe", [8766]);

        Assert.Equal(["advfirewall", "firewall", "set", "rule"], set[..4]);
        Assert.Contains("new", set);
        Assert.Contains("localport=8766", set);
        Assert.Contains("remoteip=localsubnet", set);
        Assert.Contains("protocol=TCP", set);
        Assert.Equal(["advfirewall", "firewall", "add", "rule"], add[..4]);
        Assert.Contains("remoteip=localsubnet", add);
        Assert.DoesNotContain(set.Concat(add), a => a.Contains("--"));
    }

    [Theory]
    [InlineData("8766", new[] { 8766 })]
    [InlineData("8766,8765", new[] { 8766, 8765 })]
    [InlineData("8766,8766", new[] { 8766 })]
    public void Ports_for_the_elevated_helper_are_parsed(string text, int[] expected)
    {
        Assert.Equal(expected, FirewallRule.ParsePorts(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("80")]
    [InlineData("8766,abc")]
    [InlineData("8766 remoteip=any")]
    [InlineData("0-65535")]
    [InlineData("70000")]
    [InlineData("-1")]
    [InlineData("1024,1025,1026,1027,1028")]
    public void The_elevated_helper_refuses_anything_that_is_not_a_short_list_of_ports(string? text)
    {
        Assert.Null(FirewallRule.ParsePorts(text));
    }

    private static (Pairing Pairing, List<RemoteDevice> Devices) Create()
    {
        var devices = new List<RemoteDevice>();
        return (new Pairing(() => devices, change => change(devices)), devices);
    }

    [Fact]
    public void Wrong_tries_are_counted_per_source_address()
    {
        var (pairing, devices) = Create();
        var code = pairing.CurrentCode(Now);

        // One device on the LAN hammers the code ...
        for (var i = 0; i < 5; i++) Assert.Null(pairing.TryPair("WRONG123", "x", Now, "192.168.1.66"));
        Assert.Null(pairing.TryPair(code, "x", Now.AddSeconds(5), "192.168.1.66"));

        // ... and the phone of the household can still pair.
        var token = pairing.TryPair(code, "Sofa", Now.AddSeconds(5), "192.168.1.20");
        Assert.NotNull(token);
        Assert.Single(devices);
    }

    [Fact]
    public void A_blocked_source_can_try_again_after_the_pause()
    {
        var (pairing, _) = Create();
        var code = pairing.CurrentCode(Now);
        for (var i = 0; i < 5; i++) pairing.TryPair("WRONG123", "x", Now, "192.168.1.66");

        Assert.Null(pairing.TryPair(code, "x", Now.AddSeconds(29), "192.168.1.66"));
        Assert.NotNull(pairing.TryPair(code, "x", Now.AddSeconds(31), "192.168.1.66"));
    }

    [Fact]
    public void A_device_token_runs_out_after_180_days_without_use_and_use_renews_it()
    {
        var (pairing, devices) = Create();
        var token = pairing.TryPair(pairing.CurrentCode(Now), "Sofa", Now)!;

        Assert.Equal(TimeSpan.FromDays(180), Pairing.TokenLifetime);
        // Used every few months: stays valid, and the use is remembered.
        Assert.True(pairing.IsValid(token, Now.AddDays(100)));
        Assert.Equal(Now.AddDays(100), devices[0].LastUsed);
        Assert.True(pairing.IsValid(token, Now.AddDays(250)));
        // Not used for more than 180 days: gone for good.
        Assert.False(pairing.IsValid(token, Now.AddDays(250 + 181)));
        Assert.Empty(devices);
        Assert.False(pairing.IsValid(token, Now.AddDays(250 + 182)));
    }

    [Fact]
    public void An_old_device_without_last_use_counts_from_its_pairing_day()
    {
        var devices = new List<RemoteDevice>();
        var pairing = new Pairing(() => devices, change => change(devices));
        var token = pairing.TryPair(pairing.CurrentCode(Now), "Sofa", Now)!;
        devices[0].LastUsed = null;

        Assert.True(pairing.IsValid(token, Now.AddDays(179)));
        devices[0].LastUsed = null;
        Assert.False(pairing.IsValid(token, Now.AddDays(181)));
    }

    [Fact]
    public void One_phone_can_be_unpaired_without_the_others()
    {
        var (pairing, devices) = Create();
        var first = pairing.TryPair(pairing.CurrentCode(Now), "Sofa", Now)!;
        var second = pairing.TryPair(pairing.NewCode(Now), "Tablet", Now)!;
        Assert.Equal(2, devices.Count);
        Assert.NotEqual(devices[0].Id, devices[1].Id);

        Assert.True(pairing.Revoke(devices.Single(d => d.Name == "Sofa").Id));

        Assert.False(pairing.IsValid(first, Now));
        Assert.True(pairing.IsValid(second, Now));
        Assert.False(pairing.Revoke("does-not-exist"));
        Assert.False(pairing.Revoke(""));
    }
}
