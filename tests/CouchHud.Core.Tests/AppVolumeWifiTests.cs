using CouchHud.Core.Platform;

namespace CouchHud.Core.Tests;

public class AppVolumeWifiTests
{
    private static RawAudioSession S(int pid, string proc, float vol = 0.5f, bool muted = false, string? title = null, bool system = false) =>
        new(pid, proc, title, system, vol, muted);

    [Fact]
    public void Game_comes_first_system_sounds_last_rest_by_name()
    {
        var list = AppVolume.Arrange(
            [S(0, "", system: true), S(10, "spotify"), S(20, "witcher3"), S(30, "chrome")],
            new HashSet<int> { 20 });

        Assert.Equal(new[] { "Witcher3", "Chrome", "Spotify", "Systemklänge" }, list.Select(e => e.Name));
        Assert.True(list[0].IsGame);
        Assert.Equal(AppVolume.SystemId, list[3].Id);
    }

    [Fact]
    public void Sessions_of_one_process_are_folded()
    {
        var list = AppVolume.Arrange([S(5, "msedge", 0.2f, true), S(5, "msedge", 0.8f, true)], new HashSet<int>());

        var edge = Assert.Single(list);
        Assert.Equal(80, edge.Volume);
        Assert.True(edge.Muted);
        Assert.Equal("5", edge.Id);
    }

    [Fact]
    public void Not_muted_if_any_stream_is_audible_and_own_hosts_are_hidden()
    {
        var list = AppVolume.Arrange([S(5, "a", muted: true), S(5, "a", muted: false), S(9, "couchhud")], new HashSet<int>());

        Assert.False(Assert.Single(list).Muted);
    }

    [Fact]
    public void Resource_string_titles_fall_back_to_the_process_name()
    {
        Assert.Equal("Discord", AppVolume.NameOf(S(1, "discord", title: "@%SystemRoot%\\x.dll,-1")));
        Assert.Equal("Mein Titel", AppVolume.NameOf(S(1, "x", title: " Mein Titel ")));
    }

    [Fact]
    public void Wifi_list_has_one_entry_per_ssid_connected_first()
    {
        var list = Wifi.Arrange(
        [
            new("Home", 40, "RsnaPsk", false), new("Home", 80, "RsnaPsk", false),
            new("Cafe", 90, "Open80211", false), new("Neighbour", 60, "RsnaPsk", true),
            new("Firma", 20, "Rsna", false),
            new("", 99, "None", false), new("  ", 99, "None", false)
        ]);

        Assert.Equal(new[] { "Neighbour", "Cafe", "Home", "Firma" }, list.Select(n => n.Ssid));
        Assert.False(list[3].Supported);
        Assert.Equal("enterprise", list[3].Security);
        Assert.Equal(80, list[2].Signal);
        Assert.True(list[0].Connected);
        Assert.False(list[1].Secured);
    }

    [Theory]
    [InlineData(-30, 100)]
    [InlineData(-50, 100)]
    [InlineData(-75, 50)]
    [InlineData(-100, 0)]
    [InlineData(-120, 0)]
    public void Signal_percent_follows_the_windows_mapping(int dbm, int percent) => Assert.Equal(percent, Wifi.SignalPercent(dbm));

    [Theory]
    [InlineData("None", false)]
    [InlineData("Open80211", false)]
    [InlineData("Rsna", true)]
    [InlineData("SharedKey80211", true)]
    public void Security_is_derived_from_the_authentication_type(string type, bool secured) => Assert.Equal(secured, Wifi.IsSecured(type));

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(63, true)]
    [InlineData(65, false)]
    public void Wpa_password_length_limits(int length, bool ok) =>
        Assert.Equal(ok, Wifi.CheckPassword(new string('a', length)) is null);

    [Fact]
    public void Wpa_64_characters_must_be_hex()
    {
        Assert.Null(Wifi.CheckPassword(new string('F', 64)));
        Assert.NotNull(Wifi.CheckPassword(new string('g', 64)));
        Assert.Null(Wifi.CheckPassword(""));
        Assert.Null(Wifi.CheckPassword(null));
    }

    [Theory]
    [InlineData("abcde", true)]
    [InlineData("abcdefghijklm", true)]
    [InlineData("0123456789", true)]
    [InlineData("abcdefghij", false)]
    [InlineData("abcd", false)]
    [InlineData("12345678", false)]
    public void Wep_keys_have_their_own_lengths(string key, bool ok) =>
        Assert.Equal(ok, Wifi.CheckPassword(key, WifiSecurity.Wep) is null);

    [Fact]
    public void Enterprise_and_unknown_networks_are_not_supported()
    {
        Assert.NotNull(Wifi.CheckPassword("", WifiSecurity.Enterprise));
        Assert.NotNull(Wifi.CheckPassword("x", WifiSecurity.Unsupported));
        Assert.Equal(WifiSecurity.Enterprise, Wifi.Classify("Rsna"));
        Assert.Equal(WifiSecurity.Psk, Wifi.Classify("RsnaPsk"));
        Assert.Equal(WifiSecurity.Wep, Wifi.Classify("SharedKey80211"));
        Assert.Equal(WifiSecurity.Unsupported, Wifi.Classify("Ihv"));
    }

    [Fact]
    public void Empty_process_name_falls_back_and_own_processes_are_hidden()
    {
        Assert.Equal("Unbekannte App", AppVolume.NameOf(S(1, "")));
        var list = AppVolume.Arrange([S(1, "msedgewebview2"), S(2, "msedgewebview2"), S(3, "")], new HashSet<int>(), new HashSet<int> { 1 });
        Assert.Equal(new[] { "Msedgewebview2", "Unbekannte App" }, list.Select(e => e.Name).Order().ToArray());
        Assert.DoesNotContain(list, e => e.Id == "1");
    }
}
