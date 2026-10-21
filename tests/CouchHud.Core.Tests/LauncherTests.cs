using CouchHud.Core.Library;

namespace CouchHud.Core.Tests;

public class LauncherTests
{
    [Fact]
    public void Finds_installed_clients_from_registry_and_default_paths()
    {
        var registry = new FakeRegistry();
        registry.Values[@"HKCU\Software\Valve\Steam"] = new() { ["SteamExe"] = "c:/program files (x86)/steam/steam.exe" };
        registry.Values[@"HKLM\SOFTWARE\Classes\com.epicgames.launcher\shell\open\command"] = new() { [""] = "\"D:\\Epic\\EpicGamesLauncher.exe\" %1" };
        registry.Values[@"HKLM\SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths"] = new() { ["client"] = @"D:\GOG Galaxy" };
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"c:\program files (x86)\steam\steam.exe",
            @"D:\Epic\EpicGamesLauncher.exe",
            @"D:\GOG Galaxy\GalaxyClient.exe"
        };

        var found = Launchers.Find(registry, files.Contains);

        Assert.Equal(new[] { "steam", "epic", "gog" }, found.Select(l => l.Id).ToArray());
        Assert.Equal("-silent", found[0].Arguments);
        Assert.Equal(@"D:\Epic\EpicGamesLauncher.exe", found[1].Exe);
        Assert.Equal("GalaxyClient", found[2].Process);
    }

    private static readonly GameLauncher Steam = new("steam", "Steam", "steam.exe", "steam", "-silent");
    private static readonly Dictionary<string, string> NoModes = new();

    [Fact]
    public void Clients_stand_by_unless_chosen_otherwise()
    {
        Assert.Equal(Launchers.Standby, Launchers.ModeOf(Steam, NoModes));
        Assert.Equal(Launchers.OnDemand, Launchers.ModeOf(Steam, new Dictionary<string, string> { ["steam"] = "demand" }));
        Assert.Equal(Launchers.Off, Launchers.ModeOf(Steam, new Dictionary<string, string> { ["steam"] = "off" }));
        Assert.Equal(Launchers.Standby, Launchers.ModeOf(Steam, new Dictionary<string, string> { ["steam"] = "nonsense" }));
    }

    [Fact]
    public void Older_on_off_settings_still_count_until_replaced()
    {
        Assert.Equal(Launchers.Off, Launchers.ModeOf(Steam, NoModes, new Dictionary<string, bool> { ["steam"] = false }));
        Assert.Equal(Launchers.Standby, Launchers.ModeOf(Steam, NoModes, new Dictionary<string, bool> { ["steam"] = true }));
        // The new setting wins.
        Assert.Equal(Launchers.OnDemand, Launchers.ModeOf(Steam, new Dictionary<string, string> { ["steam"] = "demand" }, new Dictionary<string, bool> { ["steam"] = false }));
    }

    [Theory]
    [InlineData("standby", true, true)]
    [InlineData("demand", false, true)]
    [InlineData("off", false, false)]
    public void What_each_mode_starts(string mode, bool withCouch, bool onDemand)
    {
        Assert.Equal(withCouch, Launchers.StartsWithCouch(mode));
        Assert.Equal(onDemand, Launchers.StartsOnDemand(mode));
    }
}
