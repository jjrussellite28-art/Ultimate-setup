using CouchHud.Core.Updates;

namespace CouchHud.Core.Tests;

public class WingetTests
{
    private const string English =
        "   - \r   \\ \r   | \r" +
        "Name                     Id                         Version      Available    Source\n" +
        "--------------------------------------------------------------------------------------\n" +
        "Mozilla Firefox (x64 de) Mozilla.Firefox.de         130.0        131.0.2      winget\n" +
        "Discord                  Discord.Discord            1.0.9163     1.0.9164     winget\n" +
        "Some Very Long Product … Vendor.LongProduct         Unknown      2.0          winget\n" +
        "3 upgrades available.\n" +
        "\n" +
        "The following packages have an upgrade available, but require explicit targeting for upgrade:\n" +
        "Name   Id          Version Available Source\n" +
        "-------------------------------------------\n" +
        "Pinned Pinned.App  1.0     2.0       winget\n";

    private const string German =
        "Name                Name-ID                 Version   Verfügbar  Quelle\n" +
        "------------------------------------------------------------------------\n" +
        "7-Zip 23.01 (x64)   7zip.7zip               23.01     24.08      winget\n" +
        "\n" +
        "1 Aktualisierungen verfügbar.\n";

    [Fact]
    public void Parses_the_upgrade_table_and_ignores_spinner_and_pinned_packages()
    {
        var items = Winget.ParseUpgrades(English);

        Assert.Equal(new[] { "Mozilla.Firefox.de", "Discord.Discord", "Vendor.LongProduct" }, items.Select(i => i.Id).ToArray());
        var firefox = items[0];
        Assert.Equal("Mozilla Firefox (x64 de)", firefox.Name);
        Assert.Equal("130.0", firefox.Version);
        Assert.Equal("131.0.2", firefox.Available);
        Assert.Equal("winget", firefox.Source);
        Assert.Null(items[2].Version);
        Assert.Equal("Some Very Long Product", items[2].Name);
    }

    [Fact]
    public void Works_with_localized_headers()
    {
        var item = Assert.Single(Winget.ParseUpgrades(German));
        Assert.Equal("7zip.7zip", item.Id);
        Assert.Equal("24.08", item.Available);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No installed package found matching input criteria.")]
    [InlineData("Keine verfügbaren Aktualisierungen gefunden.\r\n")]
    public void Nothing_to_upgrade_gives_an_empty_list(string output)
    {
        Assert.Empty(Winget.ParseUpgrades(output));
    }

    [Fact]
    public void Upgrade_arguments_never_prompt()
    {
        var one = Winget.UpgradeArguments("Discord.Discord");
        Assert.Equal(new[] { "upgrade", "--id", "Discord.Discord", "--exact" }, one.Take(4).ToArray());
        Assert.Contains("--silent", one);
        Assert.Contains("--accept-package-agreements", one);
        Assert.Contains("--all", Winget.UpgradeArguments(null));
    }
}
