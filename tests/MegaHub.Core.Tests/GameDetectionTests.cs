using MegaHub.Core.Files;
using MegaHub.Core.Library;

namespace MegaHub.Core.Tests;

/// <summary>
/// Which exe in a game folder is the game and what it is called. The fixtures are modelled on folders found on a real machine (names, sizes
/// and version resources as they were, no files copied): repacks with RapidCRC next to the game, Unreal stubs with the real binary under
/// Binaries, a Steam client folder below the games folder, a game folder full of helpers.
/// </summary>
public class GameDetectionTests
{
    private const long MB = 1024 * 1024;

    private static ExeFacts Exe(string path, long size, string? product = null, string? description = null) => new(path, size, product, description);

    [Fact]
    public void A_checksum_tool_of_a_repack_never_wins_over_the_unreal_stub()
    {
        // Was: RapidCRC.exe ("RapidCRC Unicode"), the biggest exe in the folder itself; the game is a 243 KB stub that starts MK12\Binaries\Win64\MK12.exe.
        var pick = GameDetection.Pick("Mortal Kombat 1", [
            Exe("MK12.exe", 243_000),
            Exe(@"MK12\Binaries\Win64\MK12.exe", 389 * MB),
            Exe("RapidCRC.exe", 1_100_000, "RapidCRC Unicode", "RapidCRC Unicode"),
            Exe(@"MK12\Binaries\Win64\EOSBootstrapper.exe", 900_000, "EOS Bootstrapper")
        ])!;

        Assert.Equal("MK12.exe", pick.Exe.RelativePath);
        Assert.Equal(@"MK12\Binaries\Win64\MK12.exe", pick.NameSource.RelativePath);
        Assert.Equal("Mortal Kombat 1", pick.Name);
    }

    [Fact]
    public void The_bootstrap_stubs_product_name_is_not_the_games_name()
    {
        // Was: "BootstrapPackagedGame" (the product name of Unreal's stub); the binary only says "COTEG", the folder says it better.
        var pick = GameDetection.Pick("Call of the Elder Gods", [
            Exe("COTEG.exe", 270_000, "BootstrapPackagedGame", "BootstrapPackagedGame"),
            Exe(@"COTEG\Binaries\WinGDK\COTEG-WinGDK-Shipping.exe", 140 * MB, "COTEG"),
            Exe(@"Engine\Binaries\ThirdParty\DotNet\x64\dotnet.exe", 200_000)
        ])!;

        Assert.Equal("COTEG.exe", pick.Exe.RelativePath);
        Assert.Equal("Call of the Elder Gods", pick.Name);
    }

    [Fact]
    public void A_folder_full_of_helpers_gives_the_game_and_the_folder_names_which_one()
    {
        var pick = GameDetection.Pick("Call of Duty Modern Warfare 4 2026", [
            Exe("cod.exe", 25 * MB, "Call of Duty®"),
            Exe("bootstrapper.exe", 4 * MB, "Call of Duty® Bootstrapper"),
            Exe("CODBrokerService.exe", 3 * MB),
            Exe("CODSecureAttestationWizard.exe", 67 * MB),
            Exe("ccsdownloader.exe", 2 * MB),
            Exe("codinstallcleaner.exe", 1 * MB),
            Exe("crashpad_handler.exe", 1 * MB),
            Exe(@"sp26\sp26-cod.exe", 20 * MB, "Call of Duty®: Modern Warfare® 4 SP")
        ])!;

        Assert.Equal("cod.exe", pick.Exe.RelativePath);
        // "Call of Duty" is the series; the folder (without the year behind the number) says which game.
        Assert.Equal("Call of Duty Modern Warfare 4", pick.Name);
    }

    [Fact]
    public void A_steam_client_folder_below_the_games_folder_is_no_game()
    {
        Assert.Null(GameDetection.Pick("Steam", [Exe("steam.exe", 4 * MB, "Steam"), Exe(@"bin\cef\cef.win7x64\steamwebhelper.exe", 5 * MB)], hasSteamApps: true));
        // Also without its library folder: the client exe says it.
        Assert.Null(GameDetection.Pick("Steam", [Exe("steam.exe", 4 * MB, "Steam"), Exe("uninstall.exe", 200_000)]));
    }

    [Theory]
    [InlineData("007 First Light", @"Retail\007FirstLight.exe")]
    [InlineData("The Midnight Walk", "TheMidnightWalk.exe")]
    [InlineData("Control Resonant", "CONTROLResonant.exe")]
    [InlineData("God of War III", "God of War III.exe")]
    [InlineData("The Sinking City 2", @"PlayGround\Binaries\Win64\TheSinkingCity2.exe")]
    public void Entries_that_were_right_stay_right(string folder, string expected)
    {
        ExeFacts[] exes = folder switch
        {
            "007 First Light" => [
                Exe(@"Retail\007FirstLight.exe", 310 * MB, "007 First Light"),
                Exe("RapidCRC.exe", 1_100_000, "RapidCRC Unicode"),
                Exe("007 First Light update 1.0.0 - 1.1.0.exe", 40 * MB, "007 First Light update")
            ],
            "The Midnight Walk" => [
                Exe("TheMidnightWalk.exe", 300_000),
                Exe(@"TheMidnightWalk\Binaries\Win64\TheMidnightWalk-Win64-Shipping.exe", 120 * MB),
                Exe("RapidCRC.exe", 1_100_000, "RapidCRC Unicode")
            ],
            "Control Resonant" => [
                Exe("CONTROLResonant.exe", 30 * MB, "Game"),
                Exe("RapidCRC.exe", 1_100_000, "RapidCRC Unicode")
            ],
            "God of War III" => [
                Exe("God of War III.exe", 9 * MB),
                Exe("Play God of War III.exe", 300_000),
                Exe(@"bin\gow3-probe.exe", 2 * MB)
            ],
            _ => [
                Exe(@"PlayGround\Binaries\Win64\TheSinkingCity2.exe", 150 * MB),
                Exe("RapidCRC.exe", 1_100_000, "RapidCRC Unicode")
            ]
        };

        var pick = GameDetection.Pick(folder, exes)!;

        Assert.Equal(expected, pick.Exe.RelativePath);
        Assert.Equal(folder, pick.Name);
    }

    [Fact]
    public void A_telling_product_name_beats_a_scene_folder_name()
    {
        var pick = GameDetection.Pick("Hollow.Knight.v1.5.78-GOG", [Exe("hollow_knight.exe", 600_000, "Hollow Knight"), Exe("UnityCrashHandler64.exe", 1 * MB)])!;

        Assert.Equal("hollow_knight.exe", pick.Exe.RelativePath);
        Assert.Equal("Hollow Knight", pick.Name);
    }

    [Fact]
    public void A_launcher_only_wins_when_there_is_nothing_else()
    {
        var both = GameDetection.Pick("Some Game", [Exe("SomeGameLauncher.exe", 50 * MB), Exe("SomeGame.exe", 20 * MB)])!;
        Assert.Equal("SomeGame.exe", both.Exe.RelativePath);
        var alone = GameDetection.Pick("Some Game", [Exe("SomeGameLauncher.exe", 50 * MB)])!;
        Assert.Equal("SomeGameLauncher.exe", alone.Exe.RelativePath);
    }

    [Fact]
    public void Only_helpers_is_no_game_but_a_game_with_a_helper_word_in_its_name_is_one()
    {
        Assert.Null(GameDetection.Pick("Tools", [Exe("unins000.exe", 1 * MB), Exe("vc_redist.x64.exe", 14 * MB), Exe("RapidCRC.exe", 1 * MB)]));
        var toolbox = GameDetection.Pick("Toolbox Tales", [Exe("ToolboxTales.exe", 30 * MB), Exe("unins000.exe", 1 * MB)])!;
        Assert.Equal("ToolboxTales.exe", toolbox.Exe.RelativePath);
    }

    [Theory]
    [InlineData("unins000.exe", true)]
    [InlineData("UnityCrashHandler64.exe", true)]
    [InlineData("RapidCRC.exe", true)]
    [InlineData("QuickSFV.exe", true)]
    [InlineData("UE4PrereqSetup_x64.exe", true)]
    [InlineData("EasyAntiCheat_EOS_Setup.exe", true)]
    [InlineData("BEService_x64.exe", true)]
    [InlineData("CODSecureAttestationWizard.exe", true)]
    [InlineData("MK12.exe", false)]
    [InlineData("God of War III.exe", false)]
    [InlineData("007FirstLight.exe", false)]
    public void Helpers_are_known_by_name(string file, bool junk) => Assert.Equal(junk, GameDetection.IsJunkName(file));

    [Fact]
    public void Helpers_are_known_by_folder_and_version_resource()
    {
        Assert.True(GameDetection.IsJunk(Exe(@"_CommonRedist\DirectX\Jun2010\DXSETUP.exe", 500_000)));
        Assert.True(GameDetection.IsJunk(Exe(@"EasyAntiCheat\start.exe", 500_000)));
        Assert.True(GameDetection.IsJunk(Exe("x.exe", 500_000, "RapidCRC Unicode")));
        Assert.True(GameDetection.IsJunk(Exe("x.exe", 500_000, null, "Microsoft Visual C++ 2015 Redistributable (x64)")));
        Assert.False(GameDetection.IsJunk(Exe(@"Game\Binaries\Win64\Game-Win64-Shipping.exe", 100 * MB)));
    }

    [Theory]
    [InlineData("Some_Indie-Game", "Some Indie Game")]
    [InlineData("Hades.II.v1.2.3-CODEX", "Hades II")]
    [InlineData("Cyberpunk 2077 [FitGirl Repack]", "Cyberpunk 2077")]
    [InlineData("Doom (2016)", "Doom (2016)")]
    [InlineData("Elden Ring - Crack by Razor", "Elden Ring")]
    [InlineData("Call of Duty Modern Warfare 4 2026", "Call of Duty Modern Warfare 4")]
    [InlineData("Red Dead Redemption BLUS30418", "Red Dead Redemption")]
    [InlineData("Stray v1.5.2 MULTi12 x64", "Stray")]
    [InlineData("The Witcher 3 + 2 DLCs", "The Witcher 3")]
    [InlineData("Assassins Creed Black Flag Resynced", "Assassins Creed Black Flag Resynced")]
    [InlineData("Mortal Kombat 1", "Mortal Kombat 1")]
    [InlineData("2064", "2064")]
    public void Folder_names_lose_their_noise(string folder, string expected) => Assert.Equal(expected, GameDetection.CleanFolderName(folder));

    [Fact]
    public void The_folder_scan_picks_the_stub_and_skips_the_steam_folder()
    {
        var root = Directory.CreateTempSubdirectory("megahub-detect-").FullName;
        void File(string path, int size)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllBytes(full, new byte[size]);
        }
        File(@"Mortal Kombat 1\MK12.exe", 2_000);
        File(@"Mortal Kombat 1\MK12\Binaries\Win64\MK12.exe", 50_000);
        File(@"Mortal Kombat 1\RapidCRC.exe", 20_000);
        File(@"Steam\steam.exe", 30_000);
        Directory.CreateDirectory(Path.Combine(root, "Steam", "steamapps"));
        File(@"Hades II\Hades2.exe", 10_000);
        File(@"Hades II\unins000.exe", 40_000);

        var found = ExeScanner.Scan(root);

        Assert.Equal(["Hades II", "Mortal Kombat 1"], found.Select(c => c.Name));
        Assert.Equal(Path.Combine(root, "Mortal Kombat 1", "MK12.exe"), found[1].Path);
        Assert.Equal(Path.Combine(root, "Hades II", "Hades2.exe"), found[0].Path);
    }
}

/// <summary>When a search hit is the game: sure ones only, sequels, numbers and look-alikes are not.</summary>
public class NameConfidenceTests
{
    [Theory]
    [InlineData("Mortal Kombat 1", "Mortal Kombat 1", 1.0)]
    [InlineData("Mortal Kombat 1", "Mortal Kombat Arcade Kollection", 0.0)]
    [InlineData("Mortal Kombat 1", "Mortal Kombat 11", 0.0)]
    [InlineData("Mortal Combat 1", "Mortal Kombat 1", 0.8)]
    [InlineData("Doom 3", "Doom 2", 0.0)]
    [InlineData("Final Fantasy VII", "Final Fantasy VIII", 0.0)]
    [InlineData("Final Fantasy VII", "Final Fantasy 7", 1.0)]
    [InlineData("Halflife", "Half-Life", 0.9)]
    [InlineData("Assassin's Creed IV Black Flag", "Assassins Creed IV Black Flag", 1.0)]
    [InlineData("Doom (1993)", "Doom (2016)", 0.0)]
    [InlineData("Call of the Elder Gods", "Call of the Sea", 0.0)]
    [InlineData("Hades", "Hades II", 0.0)]
    public void Confidence(string name, string hit, double expected) => Assert.Equal(expected, NameMatch.Confidence(name, hit), 3);

    [Fact]
    public void Best_takes_the_surest_hit_or_none()
    {
        Assert.Null(NameMatch.Best("Mortal Kombat 1", ["Mortal Kombat Arcade Kollection", "Mortal Kombat 11"], s => s));
        Assert.Equal("Mortal Kombat 1", NameMatch.Best("Mortal Kombat 1", ["Mortal Kombat Arcade Kollection", "Mortal Kombat 1"], s => s));
        // A typo in the user's name still finds the game.
        Assert.Equal("Mortal Kombat 1", NameMatch.Best("Mortal Combat 1", ["Mortal Kombat Arcade Kollection", "Mortal Kombat 1"], s => s));
        // Of two sure ones the very name wins.
        Assert.Equal("Hades", NameMatch.Best("Hades", ["HADES Deluxe Edition", "Hades"], s => s));
    }
}
