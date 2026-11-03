using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>Apps in the Guide menu, the known media players (Kodi, Emby, Jellyfin, Plex) and Wand's move to a companion app.</summary>
public class GuideAppsAndPlayersTests
{
    private static LibraryItem Item(string id, ItemType type = ItemType.App, string target = @"C:\Apps\a.exe", string? dir = null) => new()
    {
        Id = id,
        Source = "custom",
        Name = id,
        Type = type,
        InstallDir = dir,
        Launch = new LaunchTarget(LaunchKind.Exe, target)
    };

    // ---- apps in the Guide menu

    [Fact]
    public void The_saved_list_is_cleaned_and_bounded()
    {
        var ids = GuideAppRules.Clean([" a ", "a", "", null, "b\u0007", .. Enumerable.Range(0, 30).Select(i => "x" + i)]);
        Assert.Equal("a", ids[0]);
        Assert.Equal(GuideAppRules.Max, ids.Count);
        Assert.DoesNotContain(ids, id => id.Any(char.IsControl));
    }

    [Fact]
    public void Only_an_app_can_be_put_in_and_the_limit_holds()
    {
        var app = Item("a");
        Assert.Equal(["a"], GuideAppRules.With([], app, "a", true));
        Assert.Null(GuideAppRules.With([], Item("g", ItemType.Game), "g", true));
        Assert.Null(GuideAppRules.With([], null, "gone", true));
        // The id must be the item's own (a forged id for another item is refused).
        Assert.Null(GuideAppRules.With([], app, "other", true));
        var full = Enumerable.Range(0, GuideAppRules.Max).Select(i => "x" + i).ToList();
        Assert.Null(GuideAppRules.With(full, app, "a", true));
        // Taking one out always works, also for an item that is gone.
        Assert.DoesNotContain("x1", GuideAppRules.With(full, null, "x1", false)!);
    }

    [Fact]
    public void The_menu_shows_what_the_start_would_let_through()
    {
        var wand = Item("wand");
        var game = Item("game", ItemType.Game);
        var child = Item("blocked");
        var off = Item("off");
        var library = new[] { wand, game, child, off };
        LibraryItem? Find(string id) => library.FirstOrDefault(i => i.Id == id);
        string[] ids = ["wand", "game", "blocked", "off", "missing"];
        var open = GuideAppRules.Visible(ids, Find, i => i.Id != "off", i => i.Id != "blocked", locked: false);
        Assert.Equal(["wand"], open.Select(i => i.Id));
        // A locked couch starts apps for nobody: the menu offers none (the same rule as the library's start).
        Assert.Empty(GuideAppRules.Visible(ids, Find, _ => true, _ => true, locked: true));
        Assert.False(LockPolicy.MayLaunchLocked(ItemType.App));
    }

    [Fact]
    public void The_menu_start_is_the_librarys_own_start()
    {
        var quick = File.ReadAllText(Path.Combine(Root(), "ui", "components", "quick-menu.js"));
        // The row calls launch (the same gate as the library): never a start of its own.
        Assert.Contains("api.launch(app.id)", quick);
        var hud = File.ReadAllText(Path.Combine(Root(), "src", "CouchHud.Shell", "HudCompanions.cs"));
        Assert.DoesNotContain("Launcher.Launch", hud);
    }

    // ---- known players

    [Theory]
    [InlineData(@"C:\Program Files\Kodi\kodi.exe", "kodi")]
    [InlineData(@"C:\Program Files\Kodi\KODI.EXE", "kodi")]
    [InlineData(@"C:\Users\A\AppData\Local\Programs\Emby Theater\EmbyTheater.exe", "emby")]
    [InlineData(@"C:\Program Files\Jellyfin Media Player\JellyfinMediaPlayer.exe", "jellyfin")]
    [InlineData(@"C:\Program Files\Plex HTPC\Plex HTPC.exe", "plex")]
    [InlineData(@"C:\Users\A\AppData\Local\Plex\Plex.exe", "plex")]
    public void A_known_player_is_found_by_its_program(string path, string id) =>
        Assert.Equal(id, PlayerProfiles.Find(new LaunchTarget(LaunchKind.Exe, path))?.Id);

    [Fact]
    public void Kodi_from_the_store_is_found_by_its_package_and_other_apps_are_not_players()
    {
        Assert.Equal("kodi", PlayerProfiles.Find(new LaunchTarget(LaunchKind.Url, @"shell:AppsFolder\XBMCFoundation.Kodi_4n2hpmxwrvr6p!Kodi"))?.Id);
        Assert.Null(PlayerProfiles.Find(new LaunchTarget(LaunchKind.Url, @"shell:AppsFolder\Contoso.Notes_8wekyb3d8bbwe!App")));
        Assert.Null(PlayerProfiles.Find(new LaunchTarget(LaunchKind.Url, "https://kodi.tv")));
        Assert.Null(PlayerProfiles.Find(new LaunchTarget(LaunchKind.Exe, @"C:\Tools\kodi-helper.exe")));
        Assert.Null(PlayerProfiles.Find(null));
        // A game that happens to be called Kodi is no player.
        Assert.Null(PlayerProfiles.ForItem(Item("g", ItemType.Game, @"C:\Games\kodi.exe")));
    }

    [Fact]
    public void A_new_kodi_entry_starts_in_full_screen_and_other_entries_keep_their_own_parameters()
    {
        var kodi = new CustomEntry { Name = "Kodi", Type = "app", Path = @"C:\Program Files\Kodi\kodi.exe" };
        PlayerProfiles.ApplyDefaults(kodi);
        Assert.Equal(["-fs"], kodi.Args);
        var own = new CustomEntry { Name = "Kodi", Type = "app", Path = @"C:\Program Files\Kodi\kodi.exe", Args = ["--portable"] };
        PlayerProfiles.ApplyDefaults(own);
        Assert.Equal(["--portable"], own.Args);
        var plex = new CustomEntry { Name = "Plex", Type = "app", Path = @"C:\Program Files\Plex HTPC\Plex HTPC.exe" };
        PlayerProfiles.ApplyDefaults(plex);
        Assert.Empty(plex.Args);
        var store = new CustomEntry { Name = "Kodi", Type = "app", Url = @"shell:AppsFolder\XBMCFoundation.Kodi_4n2hpmxwrvr6p!Kodi" };
        PlayerProfiles.ApplyDefaults(store);
        Assert.Empty(store.Args);
        var game = new CustomEntry { Name = "Kodi", Type = "game", Path = @"C:\Program Files\Kodi\kodi.exe" };
        PlayerProfiles.ApplyDefaults(game);
        Assert.Empty(game.Args);
    }

    [Fact]
    public void Nothing_ships_with_a_profile_but_names_and_parameters()
    {
        // Compatibility only: a profile is data (names, a package prefix, full-screen parameters), with no path, address or download.
        foreach (var p in PlayerProfiles.All)
        {
            Assert.All(p.ProcessNames, n => Assert.DoesNotContain(@"\", n));
            Assert.All(p.FullscreenArgs, a => Assert.StartsWith("-", a));
        }
        Assert.Equal(["emby", "jellyfin", "kodi", "plex"], PlayerProfiles.All.Select(p => p.Id).Order());
    }

    private sealed class FakeProbe : IProcessProbe
    {
        public List<(int, string?)> Processes { get; } = [];
        public Dictionary<int, int> Parents { get; } = [];
        public IReadOnlyList<(int Pid, string? Path)> Snapshot() => Processes;
        public int SteamRunningAppId() => 0;
        public IReadOnlyDictionary<int, int> ParentMap() => Parents;
        public IReadOnlyDictionary<int, long> StartTimes() => Processes.ToDictionary(p => p.Item1, p => (long)p.Item1 * 10);
    }

    private static LibraryItem StoreKodi() => new()
    {
        Id = "custom:kodi",
        Source = "custom",
        Name = "Kodi",
        Type = ItemType.App,
        Launch = new LaunchTarget(LaunchKind.Url, @"shell:AppsFolder\XBMCFoundation.Kodi_4n2hpmxwrvr6p!Kodi")
    };

    [Fact]
    public void A_store_player_without_an_install_folder_is_still_seen_running_and_leaving()
    {
        var probe = new FakeProbe();
        var tracker = new GameSessionTracker(probe);
        var t0 = DateTimeOffset.Parse("2026-11-02T20:00:00Z");
        var kodi = StoreKodi();
        Assert.True(GameSessionTracker.CanDetect(kodi));
        tracker.Launched(kodi, t0);
        tracker.Tick(t0.AddSeconds(3));
        Assert.Null(tracker.Current);
        // The package's program starts under WindowsApps: matched by its name, no folder needed.
        probe.Processes.Add((42, @"C:\Program Files\WindowsApps\XBMCFoundation.Kodi_21.0_x64__4n2hpmxwrvr6p\kodi.exe"));
        tracker.Tick(t0.AddSeconds(8));
        Assert.Equal([42], tracker.Current!.Pids);
        probe.Processes.Clear();
        tracker.Tick(t0.AddMinutes(30));
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void A_launcher_that_hands_over_to_the_player_is_one_session_and_a_stranger_is_not_part_of_it()
    {
        var probe = new FakeProbe();
        var tracker = new GameSessionTracker(probe);
        var t0 = DateTimeOffset.Parse("2026-11-02T20:00:00Z");
        var plex = Item("custom:plex", ItemType.App, @"C:\Users\A\AppData\Local\Plex\Plex.exe", @"C:\Users\A\AppData\Local\Plex");
        tracker.Launched(plex, t0);
        // The launcher stub runs for a moment...
        probe.Processes.Add((10, @"C:\Users\A\AppData\Local\Plex\Plex.exe"));
        tracker.Tick(t0.AddSeconds(2));
        Assert.NotNull(tracker.Current);
        // ... and the player lives on under another name, elsewhere: still the same session. Something unrelated is not counted.
        probe.Processes.Clear();
        probe.Processes.Add((11, @"C:\Users\A\AppData\Local\Plex\app-1.0\Plex HTPC.exe"));
        probe.Processes.Add((12, @"C:\Tools\notplex.exe"));
        tracker.Tick(t0.AddSeconds(6));
        Assert.Equal([11], tracker.Current!.Pids);
        probe.Processes.RemoveAll(p => p.Item1 == 11);
        tracker.Tick(t0.AddSeconds(10));
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void A_program_that_only_carries_the_players_name_is_not_the_player()
    {
        var probe = new FakeProbe();
        var tracker = new GameSessionTracker(probe);
        var t0 = DateTimeOffset.Parse("2026-11-02T20:00:00Z");
        var kodi = Item("custom:kodi", ItemType.App, @"C:\Program Files\Kodi\kodi.exe", @"C:\Program Files\Kodi");
        tracker.Launched(kodi, t0);
        // Same name, other places: a download folder, another package family.
        probe.Processes.Add((60, @"C:\Users\A\Downloads\kodi.exe"));
        probe.Processes.Add((61, @"C:\Program Files\WindowsApps\Other.Kodi_1.0_x64__abcdefghijklm\kodi.exe"));
        tracker.Tick(t0.AddSeconds(4));
        Assert.Null(tracker.Current);
        // The entry's own program file does count.
        probe.Processes.Add((62, @"C:\Program Files\Kodi\kodi.exe"));
        tracker.Tick(t0.AddSeconds(8));
        Assert.Equal([62], tracker.Current!.Pids);
        // A Store entry only counts a process from its own package folder (family name and publisher id), not any kodi.exe.
        var store = new GameSessionTracker(probe);
        probe.Processes.Clear();
        store.Launched(StoreKodi(), t0);
        probe.Processes.Add((70, @"C:\Program Files\WindowsApps\XBMCFoundation.Kodi_21.0_x64__ffffffffffff\kodi.exe"));
        probe.Processes.Add((71, @"C:\Tools\kodi.exe"));
        store.Tick(t0.AddSeconds(4));
        Assert.Null(store.Current);
        probe.Processes.Add((72, @"C:\Program Files\WindowsApps\XBMCFoundation.Kodi_21.0_x64__4n2hpmxwrvr6p\kodi.exe"));
        store.Tick(t0.AddSeconds(8));
        Assert.Equal([72], store.Current!.Pids);
    }

    [Fact]
    public void A_profile_does_not_make_an_untrackable_app_trackable_so_the_parental_code_rule_stays()
    {
        // CanDetect decides "cannot be tracked, the parents' code is needed" (CodeReason "undetectable"). Only a real way to watch the app lifts
        // it: an install folder, or a Store entry of a known player with its package. A player's name alone does not.
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "kodi.exe");
        var aliasEntry = CustomEntries.From([new CustomEntry { Name = "Kodi", Type = "app", Path = alias }]).Single();
        Assert.Null(aliasEntry.InstallDir);
        Assert.False(GameSessionTracker.CanDetect(aliasEntry));
        var unknownStore = CustomEntries.From([new CustomEntry { Name = "Notes", Type = "app", Url = @"shell:AppsFolder\Contoso.Notes_8wekyb3d8bbwe!App" }]).Single();
        Assert.False(GameSessionTracker.CanDetect(unknownStore));
        Assert.True(GameSessionTracker.CanDetect(StoreKodi()));
        // The rule itself: an app with counting on and no way to watch it needs the code; a watchable Store Kodi does not.
        var rules = new CouchHud.Core.Parental.ParentalConfig { Enabled = true, CountApps = true };
        Assert.Equal("undetectable", CouchHud.Core.Parental.ParentalService.CodeReason(rules, aliasEntry, [aliasEntry], null));
        Assert.Null(CouchHud.Core.Parental.ParentalService.CodeReason(rules, StoreKodi(), [StoreKodi()], null));
    }

    [Fact]
    public void A_players_name_inside_the_windows_folder_does_not_count()
    {
        var probe = new FakeProbe();
        var tracker = new GameSessionTracker(probe);
        var t0 = DateTimeOffset.UtcNow;
        tracker.Launched(StoreKodi(), t0);
        probe.Processes.Add((50, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "kodi.exe")));
        tracker.Tick(t0.AddSeconds(5));
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void A_player_gets_no_special_right_on_the_locked_couch()
    {
        // An app is an app: the unlock is needed to start it, whatever its profile says.
        Assert.False(LockPolicy.MayLaunchLocked(PlayerProfiles.ForItem(Item("k", ItemType.App, @"C:\Kodi\kodi.exe")) is null ? ItemType.Game : ItemType.App));
        // And as a companion it is judged like any other app.
        Assert.Equal(CompanionVerdict.Ok, CompanionRules.Eligible(Item("k", ItemType.App, @"C:\Kodi\kodi.exe"), new LaunchTarget(LaunchKind.Exe, @"C:\Kodi\kodi.exe"), @"C:\Windows", p => p));
    }

    // ---- Wand

    [Fact]
    public void Wand_is_looked_for_at_its_usual_places_only()
    {
        const string local = @"C:\Users\Anna\AppData\Local";
        Assert.Equal(Path.Combine(local, "Wand", "Wand.exe"), WandSetup.FindExe(local, p => p.EndsWith(@"Wand\Wand.exe")));
        Assert.Equal(Path.Combine(local, "WeMod", "WeMod.exe"), WandSetup.FindExe(local, p => p.EndsWith("WeMod.exe")));
        Assert.Null(WandSetup.FindExe(local, _ => false));
        Assert.Null(WandSetup.FindExe("", _ => true));
        // Whatever it finds is a plain program a companion may be.
        foreach (var path in WandSetup.Candidates(local))
            Assert.Equal(CompanionVerdict.Ok, CompanionRules.Eligible(Item("w", ItemType.App, path), new LaunchTarget(LaunchKind.Exe, path), @"C:\Windows", p => p));
    }

    [Fact]
    public void The_wand_hint_comes_once_and_only_to_a_household_with_the_plugin()
    {
        Assert.True(WandSetup.HintDue(alreadyShown: false, pluginInstalled: true));
        Assert.False(WandSetup.HintDue(alreadyShown: true, pluginInstalled: true));
        Assert.False(WandSetup.HintDue(alreadyShown: false, pluginInstalled: false));
        Assert.False(new AppConfig().WandCompanionHintShown);
    }

    [Fact]
    public void The_wand_plugin_is_only_an_sdk_example_without_icons_or_a_suggestion()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "plugins", "CouchHud.Plugin.Wand", "WandPlugin.cs"));
        Assert.Contains("SDK EXAMPLE ONLY", source);
        // No emoji or symbol icons in the plugin's names (the rule for plugins).
        Assert.DoesNotContain("Icon =", source);
        Assert.All(source.EnumerateRunes(), r => Assert.True(r.Value < 0x2190, $"symbol U+{r.Value:X} in WandPlugin.cs"));
    }

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CouchHud.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }
}
