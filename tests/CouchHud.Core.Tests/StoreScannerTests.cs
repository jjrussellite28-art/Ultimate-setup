using CouchHud.Core.Library;
using CouchHud.Core.Platform;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public class StoreScannerTests
{
    [Fact]
    public void Gog_games_launch_their_exe_and_skip_dlc()
    {
        var registry = new FakeRegistry();
        registry.Values[@"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\1207658924"] = new()
        {
            ["gameName"] = "The Witcher 3: Wild Hunt",
            ["exe"] = @"D:\GOG Games\The Witcher 3\bin\x64\witcher3.exe",
            ["path"] = @"D:\GOG Games\The Witcher 3",
            ["workingDir"] = @"D:\GOG Games\The Witcher 3\bin\x64",
            ["launchParam"] = "-skipLauncher \"-profile=My Save\""
        };
        registry.Values[@"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\1640424747"] = new()
        {
            ["gameName"] = "Blood and Wine",
            ["exe"] = @"D:\GOG Games\The Witcher 3\bin\x64\witcher3.exe",
            ["dependsOn"] = "1207658924"
        };

        var item = Assert.Single(new GogScanner(registry).Scan());

        Assert.Equal("gog:1207658924", item.Id);
        Assert.Equal(LaunchKind.Exe, item.Launch.Kind);
        Assert.Equal(new[] { "-skipLauncher", "-profile=My Save" }, item.Launch.Args.ToArray());
        Assert.Equal(@"D:\GOG Games\The Witcher 3\bin\x64", item.Launch.WorkingDirectory);
        Assert.Equal(@"D:\GOG Games\The Witcher 3", item.InstallDir);
    }

    [Fact]
    public void Ubisoft_games_launch_through_the_uplay_protocol()
    {
        var registry = new FakeRegistry();
        registry.Values[@"HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\5059"] = new() { ["InstallDir"] = "C:/Ubisoft/Assassin's Creed Valhalla/" };
        registry.Values[@"HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\720"] = new() { ["InstallDir"] = "" };

        var item = Assert.Single(new UbisoftScanner(registry).Scan());

        Assert.Equal("Assassin's Creed Valhalla", item.Name);
        Assert.Equal("uplay://launch/5059/0", item.Launch.Target);
    }

    [Fact]
    public void Folder_items_have_stable_ids_from_the_exe_path()
    {
        var a = FolderGamesProvider.ToItem(new Files.ExeCandidate("Hades", @"D:\Games\Hades\Hades.exe", 1));
        var b = FolderGamesProvider.ToItem(new Files.ExeCandidate("Hades II", @"d:\games\hades\HADES.exe", 1));
        Assert.Equal(a.Id, b.Id);
        Assert.StartsWith("folder:", a.Id);
        Assert.Equal(@"D:\Games\Hades", a.Launch.WorkingDirectory);
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("-fast", new[] { "-fast" })]
    [InlineData("-w \"C:\\My Saves\" -x", new[] { "-w", "C:\\My Saves", "-x" })]
    [InlineData("\"\" a", new[] { "", "a" })]
    public void Arguments_split_like_windows(string input, string[] expected)
    {
        Assert.Equal(expected, CommandLine.SplitArguments(input).ToArray());
    }

    [Fact]
    public void Discovery_finds_typical_game_folders_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "couchhud-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Games", "Hades"));
            Directory.CreateDirectory(Path.Combine(root, "Spiele"));
            Directory.CreateDirectory(Path.Combine(root, "GOG Games", "Witcher"));

            var found = GameFolderDiscovery.Candidates([root], [Path.Combine(root, "GOG Games")]);

            // "Spiele" is empty, "GOG Games" is watched already.
            Assert.Equal(new[] { Path.Combine(root, "Games") }, found.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class FolderDuplicateTests
{
    private static LibraryItem Item(string id, string source, string dir) =>
        new() { Id = id, Source = source, Name = id, InstallDir = dir, Launch = new LaunchTarget(LaunchKind.Exe, dir + @"\x.exe") };

    [Fact]
    public void Store_entries_win_over_the_same_game_from_a_watched_folder()
    {
        var items = new List<LibraryItem>
        {
            Item("steam:1", "steam", @"D:\Games\SteamLibrary\steamapps\common\Hades"),
            Item("folder:a", "folder", @"D:\Games\SteamLibrary\steamapps\common\Hades"),
            Item("folder:b", "folder", @"D:\Games\Celeste")
        };

        var result = LibraryService.WithoutFolderDuplicates(items);

        Assert.Equal(new[] { "steam:1", "folder:b" }, result.Select(i => i.Id).ToArray());
    }
}
