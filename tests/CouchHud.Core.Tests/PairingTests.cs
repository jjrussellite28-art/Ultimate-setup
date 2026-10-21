using CouchHud.Core.Remote;

namespace CouchHud.Core.Tests;

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

        var token = pairing.TryPair(code.ToLowerInvariant(), "Living room phone", Now.AddMinutes(1));

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
