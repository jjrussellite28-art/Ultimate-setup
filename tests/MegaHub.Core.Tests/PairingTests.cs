using MegaHub.Core.Remote;

namespace MegaHub.Core.Tests;

public class PairingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 20, 0, 0, TimeSpan.Zero);

    private static (Pairing Pairing, List<RemoteDevice> Devices, int[] Saves) Create()
    {
        var devices = new List<RemoteDevice>();
        var saves = new int[1];
        return (new Pairing(() => devices, change =>
        {
            change(devices);
            saves[0]++;
        }), devices, saves);
    }

    [Fact]
    public void A_code_pairs_exactly_one_phone()
    {
        var (pairing, devices, saves) = Create();
        var code = pairing.CurrentCode(Now);

        var token = pairing.TryPair(code, "Living room phone", Now.AddMinutes(1));
        // The secret is base64url: exact, other case is another secret.
        Assert.Null(pairing.TryPair(code.ToLowerInvariant() == code ? code.ToUpperInvariant() : code.ToLowerInvariant(), "x", Now.AddMinutes(1)));

        Assert.NotNull(token);
        Assert.True(pairing.IsValid(token, Now.AddMinutes(1)));
        Assert.Equal("Living room phone", Assert.Single(devices).Name);
        Assert.NotEqual(token, devices[0].TokenHash);
        Assert.Equal(1, saves[0]);
        // The code is used up.
        Assert.Null(pairing.TryPair(code, "Zweites Handy", Now.AddMinutes(2)));
    }

    [Fact]
    public void Expired_or_wrong_codes_fail_and_too_many_tries_pause_pairing()
    {
        var (pairing, devices, _) = Create();
        var code = pairing.CurrentCode(Now);

        Assert.Null(pairing.TryPair(code, "x", Now + Pairing.CodeLifetime));
        var fresh = pairing.CurrentCode(Now + Pairing.CodeLifetime);
        Assert.NotEqual(code, fresh);

        var later = Now + Pairing.CodeLifetime;
        for (var i = 0; i < 5; i++) Assert.Null(pairing.TryPair("WRONG123", "x", later));
        Assert.Null(pairing.TryPair(fresh, "x", later.AddSeconds(10)));
        Assert.Empty(devices);
        // The same code works again after the pause.
        Assert.NotNull(pairing.TryPair(fresh, "x", later.AddSeconds(31)));
        Assert.NotEqual(fresh, pairing.NewCode(later.AddSeconds(32)));
    }

    [Fact]
    public void Pairing_pauses_one_source_and_leaves_others_alone()
    {
        var (pairing, _, _) = Create();
        var code = pairing.CurrentCode(Now);
        for (var i = 0; i < 5; i++) Assert.Null(pairing.TryPair("WRONG", "x", Now, "192.168.1.9"));
        Assert.Null(pairing.TryPair(code, "x", Now.AddSeconds(5), "192.168.1.9"));
        Assert.NotNull(pairing.TryPair(code, "x", Now.AddSeconds(6), "192.168.1.10"));
    }

    [Fact]
    public void Pairing_counts_ipv6_by_its_64_and_never_pauses_loopback()
    {
        var (pairing, _, _) = Create();
        var code = pairing.CurrentCode(Now);
        for (var i = 0; i < 5; i++) Assert.Null(pairing.TryPair("WRONG", "x", Now, "fd00:1:2:3::" + (i + 1)));
        Assert.Null(pairing.TryPair(code, "x", Now.AddSeconds(1), "fd00:1:2:3:9::7"));
        foreach (var lo in new[] { "127.0.0.1", "::1" })
            for (var i = 0; i < 20; i++) Assert.Null(pairing.TryPair("WRONG", "x", Now, lo));
        // Loopback was never paused: the right code still works from there.
        Assert.NotNull(pairing.TryPair(code, "x", Now.AddSeconds(2), "127.0.0.1"));
    }

    [Fact]
    public void Pairing_flooded_with_sources_keeps_a_pause_and_a_new_code_does_not_lift_it()
    {
        var (pairing, _, _) = Create();
        var code = pairing.CurrentCode(Now);
        for (var i = 0; i < 5; i++) Assert.Null(pairing.TryPair("WRONG", "x", Now, "10.0.0.1"));
        for (var i = 0; i < 1000; i++) pairing.TryPair("WRONG", "x", Now, "10.1." + (i / 250) + "." + (i % 250));
        var fresh = pairing.NewCode(Now.AddSeconds(1));
        Assert.NotEqual(code, fresh);
        Assert.Null(pairing.TryPair(fresh, "x", Now.AddSeconds(2), "10.0.0.1"));
        Assert.NotNull(pairing.TryPair(fresh, "x", Now.AddSeconds(31), "10.0.0.1"));
    }

    [Fact]
    public void Tokens_are_checked_and_can_be_revoked()
    {
        var (pairing, devices, _) = Create();
        var token = pairing.TryPair(pairing.CurrentCode(Now), "", Now)!;

        Assert.Equal("Handy", devices[0].Name);
        Assert.False(pairing.IsValid("nope"));
        Assert.False(pairing.IsValid(null));
        pairing.RevokeAll();
        Assert.False(pairing.IsValid(token));
    }
}
