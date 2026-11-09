using MegaHub.Core.Files;
using MegaHub.Core.Library;

namespace MegaHub.Core.Tests;

/// <summary>Review fixes of the folder scan and the artwork match: store clients by their main exe, "V2" is no version, typo hits must be unique, searches need a letter or digit.</summary>
public class GameDetectionHardeningTests
{
    private static ExeFacts Exe(string path, long size) => new(path, size);

    [Fact]
    public void A_source_game_with_a_steam_error_reporter_in_bin_stays_a_game()
    {
        var pick = GameDetection.Pick("Half-Life 2", [Exe("hl2.exe", 300_000), Exe(@"bin\steamerrorreporter.exe", 200_000), Exe(@"bin\steamwebhelper.exe", 200_000)]);
        Assert.NotNull(pick);
        Assert.Equal("hl2.exe", pick!.Exe.RelativePath);
        Assert.True(GameDetection.IsStoreClientFolder([Exe("steam.exe", 1)], false));
        Assert.True(GameDetection.IsStoreClientFolder([Exe(@"Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", 1)], false));
        Assert.False(GameDetection.IsStoreClientFolder([Exe(@"bin\steam.exe", 1), Exe("steamerrorreporter.exe", 1)], false));
        Assert.False(GameDetection.IsStoreClientFolder([Exe("hl2.exe", 1), Exe(@"bin\steamerrorreporter.exe", 1)], false));
    }

    [Theory]
    [InlineData("Sniper Elite V2", "Sniper Elite V2")]
    [InlineData("Sniper Elite V2 Remastered", "Sniper Elite V2 Remastered")]
    [InlineData("Stray v1.5.2", "Stray")]
    [InlineData("Game V1.2.3", "Game")]
    [InlineData("Game v2", "Game")]
    public void A_sequel_number_with_a_capital_V_is_no_version(string folder, string expected) => Assert.Equal(expected, GameDetection.CleanFolderName(folder));

    [Fact]
    public void A_typo_hit_must_be_unique_and_a_plural_is_another_title()
    {
        Assert.Equal(0, NameMatch.Confidence("Shadow Warrior", "Shadow Warriors"));
        Assert.Null(NameMatch.Best("Shadow Warrior", new[] { "Shadow Warriors", "Shadow Warrior 2" }, s => s));
        Assert.Equal("Shadow Warrior", NameMatch.Best("Shadow Warrior", new[] { "Shadow Warriors", "Shadow Warrior" }, s => s));
        // Two different readings of one misspelling: a guess, so none.
        Assert.Null(NameMatch.Best("Mortal Combat Kolection", new[] { "Mortal Kombat Kolection", "Mortal Kombat Kollection" }, s => s));
        Assert.Equal("Mortal Kombat 1", NameMatch.Best("Mortal Combat 1", new[] { "Mortal Kombat 1" }, s => s));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("...-")]
    public void A_search_without_letter_or_digit_is_refused(string query) => Assert.Throws<ArgumentException>(() => StoreMatches.CheckQuery(query));

    [Fact]
    public void A_search_with_a_digit_is_fine() => Assert.Equal("2064", StoreMatches.CheckQuery(" 2064 "));
}
