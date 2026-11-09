using System.Text.RegularExpressions;
using MegaHub.Core.Remote;

namespace MegaHub.Core.Tests;

/// <summary>
/// The phone channel, pinned from the sources: no plain pairing left on the host, every phone operation ends in the bridge's own dispatch
/// (the couch's gates), the page sets names from the PC as text only, the Host check against DNS rebinding and the page's CSP, and the
/// sleep timer's minutes are one list on the TV, the phone and the host.
/// </summary>
public sealed class RemoteChannelContractTests
{
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MegaHub.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Root(), .. path])).Replace("\r\n", "\n");

    [Fact]
    public void The_host_pairs_only_sealed()
    {
        var host = Read("src", "MegaHub.Shell", "Services", "RemoteHost.cs");
        Assert.DoesNotContain("pairing.TryPair(", host);
        Assert.Contains("channel.Pair(", host);
        // The page and seal.js are the only files; the Host check runs before every endpoint.
        Assert.Contains("RemoteChannel.HostIsAddress(ctx.Request.Host.Host)", host);
        Assert.Contains("\"Content-Security-Policy\"] = RemoteChannel.PageCsp", host);
    }

    [Fact]
    public void Phone_operations_end_in_the_bridge_dispatch()
    {
        var remote = Read("src", "MegaHub.Shell", "HudRemote.cs");
        // Library operations go through UiBridge.InvokeAsync (the couch's gates in their order), never straight to a service.
        Assert.Contains("bridge.InvokeAsync(call.Method, call.Args)", remote);
        Assert.DoesNotContain("_library.", remote);
        Assert.DoesNotContain("ServiceClient", remote);
    }

    [Theory]
    [InlineData("192.168.178.42", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("[fe80::1]", true)]
    [InlineData("fe80::1", true)]
    [InlineData("megahub.local", false)]
    [InlineData("attacker.example", false)]
    [InlineData("192.168.178.42.nip.io", false)]
    [InlineData("10.1", false)]
    [InlineData("3232235818", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_an_address_is_a_valid_host(string? host, bool ok) => Assert.Equal(ok, RemoteChannel.HostIsAddress(host));

    [Fact]
    public void The_page_policy_allows_only_its_own_server()
    {
        var csp = RemoteChannel.PageCsp;
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("form-action 'none'", csp);
        Assert.DoesNotContain("http", csp);
        Assert.DoesNotContain("*", csp);
    }

    [Fact]
    public void The_page_sets_data_as_text()
    {
        var page = Read("ui", "remote", "index.html");
        // The one innerHTML is the page's own fixed text (bold menu path); everything from the PC goes through textContent.
        Assert.Single(Regex.Matches(page, @"innerHTML"));
        Assert.Contains("n.innerHTML = tr(n.dataset.rHtml);", page);
        Assert.DoesNotContain("insertAdjacentHTML", page);
        Assert.DoesNotContain("outerHTML", page);
        Assert.DoesNotContain("document.write", page);
        Assert.DoesNotContain("eval(", page);
        // Answers are opened with the session key and must carry the request's number.
        Assert.Contains("if (!opened || opened.seq !== seq)", page);
        // The secret leaves the address bar at once and is never sent: only sealed bodies go to /api/pair.
        Assert.Contains("history.replaceState(null, '', location.pathname);", page);
        Assert.Contains("S.seal(pk, S.DIR.request, 0, 'pair'", page);
    }

    [Fact]
    public void The_sleep_timer_minutes_are_one_list()
    {
        var rules = Read("ui", "core", "sleep-timer-rules.js");
        var m = Regex.Match(rules, @"export const TIMER_MINUTES = \[([0-9, ]+)\];");
        Assert.True(m.Success);
        var js = m.Groups[1].Value.Split(',').Select(s => int.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(RemoteCalls.SleepTimerMinutes, js);
    }

    [Fact]
    public void A_request_fits_the_body_cap()
    {
        // Kestrel cuts every body at 32 KiB (RemoteHost.MaxBodyBytes); a sealed request is text, so its limit in characters is the same.
        var host = Read("src", "MegaHub.Shell", "Services", "RemoteHost.cs");
        Assert.Contains("MaxBodyBytes = 32 * 1024", host);
        Assert.Equal(32 * 1024, RemoteChannel.MaxRequestLength);
    }
}
