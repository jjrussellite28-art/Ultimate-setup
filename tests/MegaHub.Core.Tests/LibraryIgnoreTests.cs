using MegaHub.Core.Config;
using MegaHub.Core.Files;
using MegaHub.Core.Guard;
using MegaHub.Core.Library;
using MegaHub.Core.Security;
using CouchHud.Sdk;
using System.Text.RegularExpressions;

namespace MegaHub.Core.Tests;

/// <summary>The ignore list behind "Hide": stable keys, rescans, re-detection, legacy flags and the gates around it.</summary>
public class LibraryIgnoreTests
{
    private static LibraryItem Store(string id, string name = "Game") =>
        new() { Id = id, Source = id.Split(':')[0], Name = name, Launch = new LaunchTarget(LaunchKind.Url, $"{id}://run") };

    private static LibraryItem Folder(string exe, string name = "Guess") => FolderGamesProvider.ToItem(new ExeCandidate(name, exe, 1));

    [Fact]
    public void Store_games_are_known_by_their_id_only()
    {
        Assert.Equal(["id:steam:1245620"], LibraryIgnore.KeysOf(Store("steam:1245620"), [@"D:\Games"], folderGuess: false));
        Assert.Equal(["id:custom:kodi"], LibraryIgnore.KeysOf(new LibraryItem
        {
            Id = "custom:kodi", Source = "custom", Name = "Kodi", Launch = new LaunchTarget(LaunchKind.Exe, @"D:\Games\Kodi\kodi.exe")
        }, [@"D:\Games"], folderGuess: true));
    }

    [Fact]
    public void A_folder_guess_is_also_known_by_its_exe_and_its_game_folder()
    {
        var item = Folder(@"D:\Games\Some Tool\bin\x64\tool.exe");
        var keys = LibraryIgnore.KeysOf(item, [@"d:/games/"], folderGuess: true);
        Assert.Equal([$"id:{item.Id}", @"exe:d:\games\some tool\bin\x64\tool.exe", @"dir:d:\games\some tool"], keys);

        // The exe directly in the watched folder: that folder is the game.
        Assert.Contains(@"dir:d:\games\hades", LibraryIgnore.KeysOf(Folder(@"D:\Games\Hades\Hades.exe"), [@"D:\Games\Hades"], folderGuess: true));
        // Nested watched folders: the innermost decides.
        Assert.Contains(@"dir:d:\games\emu\snes", LibraryIgnore.KeysOf(Folder(@"D:\Games\Emu\SNES\snes9x.exe"), [@"D:\Games", @"D:\Games\Emu"], folderGuess: true));
        // Outside every watched folder (the folder was removed meanwhile): id and exe only.
        Assert.DoesNotContain(LibraryIgnore.KeysOf(Folder(@"E:\Other\x.exe"), [@"D:\Games"], folderGuess: true), k => k.StartsWith("dir:"));
        // "D:\Games2" is not inside "D:\Games".
        Assert.DoesNotContain(LibraryIgnore.KeysOf(Folder(@"D:\Games2\X\x.exe"), [@"D:\Games"], folderGuess: true), k => k.StartsWith("dir:"));
    }

    [Theory]
    [InlineData(@"D:\Games\Hades\", @"d:\games\hades")]
    [InlineData(@"d:/GAMES//Hades/./", @"d:\games\hades")]
    [InlineData(@"\\nas\games\Hades", @"\\nas\games\hades")]
    [InlineData("  ", "")]
    [InlineData("D:\\Ga\u0001mes", "")]
    public void Paths_are_normalized(string path, string expected) => Assert.Equal(expected, LibraryIgnore.NormalizePath(path));

    [Fact]
    public void Hide_merges_keys_show_removes_by_any_key_and_a_full_list_refuses()
    {
        var list = new List<IgnoredEntry>();
        var first = Folder(@"D:\Games\Junk\junk.exe");
        LibraryIgnore.Hide(list, first, LibraryIgnore.KeysOf(first, [@"D:\Games"], folderGuess: true), 1);
        // The next scan picked another exe in the same game folder: same record, its keys added.
        var second = Folder(@"D:\Games\Junk\bigger.exe");
        LibraryIgnore.Hide(list, second, LibraryIgnore.KeysOf(second, [@"D:\Games"], folderGuess: true), 2);
        var entry = Assert.Single(list);
        Assert.Contains($"id:{first.Id}", entry.Keys);
        Assert.Contains($"id:{second.Id}", entry.Keys);
        Assert.Equal(1, entry.HiddenAt);

        Assert.Equal(1, LibraryIgnore.Show(list, [@"dir:d:\games\junk"]));
        Assert.Empty(list);

        for (var i = 0; i < LibraryIgnore.MaxEntries; i++) list.Add(new IgnoredEntry { Keys = [$"id:steam:{i}"] });
        var full = Assert.Throws<InvalidOperationException>(() => LibraryIgnore.Hide(list, Store("steam:x"), ["id:steam:x"], 3, "full"));
        Assert.Equal("full", full.Message);
        // An entry that is already on the full list can still be hidden again (nothing new is added).
        LibraryIgnore.Hide(list, Store("steam:5"), ["id:steam:5"], 3);
        Assert.Equal(LibraryIgnore.MaxEntries, list.Count);
    }

    [Fact]
    public void Names_are_cleaned_and_cut()
    {
        var list = new List<IgnoredEntry>();
        LibraryIgnore.Hide(list, Store("steam:1", "Bad\u0007Name" + new string('x', 400)), ["id:steam:1"], 1);
        Assert.Equal(LibraryIgnore.MaxNameLength, list[0].Name.Length);
        Assert.DoesNotContain('\u0007', list[0].Name);
    }

    [Fact]
    public async Task A_hidden_folder_guess_stays_hidden_after_rescans_and_a_new_exe_pick()
    {
        var provider = new Switchable(Folder(@"D:\Games\Tools\tool.exe"), Store("steam:10", "Real Game"));
        var (library, config, dir) = await NewLibrary(provider, [@"D:\Games"]);
        var guess = library.Items.Single(i => i.Source == "folder");

        var hidden = library.SetHidden(guess.Id, true);
        Assert.True(hidden.Hidden);
        // Still in the library: parental controls see it (play time, limits, item lock); only the lists leave it out.
        Assert.Contains(library.Items, i => i.Id == guess.Id && i.Hidden);

        // A plain rescan keeps it hidden.
        Assert.True((await library.ScanAsync()).Single(i => i.Id == guess.Id).Hidden);

        // The scanner picks another exe in the same game folder: new id, still hidden (re-detection does not bring it back).
        provider.Items = [Folder(@"D:\Games\Tools\bin\tool64.exe"), Store("steam:10", "Real Game")];
        var again = (await library.ScanAsync()).Single(i => i.Source == "folder");
        Assert.NotEqual(guess.Id, again.Id);
        Assert.True(again.Hidden);
        Assert.False(library.Find("steam:10")!.Hidden);

        // Stored in config.json, with the stable keys; a fresh start reads it the same way.
        var stored = Assert.Single(new ConfigStore(dir).Current.Ignored);
        Assert.Contains(@"dir:d:\games\tools", stored.Keys);
        Assert.Equal("Guess", stored.Name);

        // Shown again from the new entry: every record of it goes, and it stays visible after the next scan.
        Assert.False(library.SetHidden(again.Id, false).Hidden);
        Assert.Empty(config.Current.Ignored);
        Assert.False((await library.ScanAsync()).Single(i => i.Source == "folder").Hidden);
    }

    [Fact]
    public async Task Store_games_stay_hidden_after_a_rescan_and_EditItem_hides_through_the_list()
    {
        var provider = new Switchable(Store("epic:Fortnite", "Fortnite"));
        var (library, config, _) = await NewLibrary(provider, []);
        library.EditItem("epic:Fortnite", null, null, hidden: true);
        Assert.True((await library.ScanAsync()).Single(i => i.Source != "custom").Hidden);
        Assert.Empty(config.Current.Overrides);
        Assert.Equal(["id:epic:Fortnite"], Assert.Single(config.Current.Ignored).Keys);

        library.EditItem("epic:Fortnite", null, null, hidden: false);
        Assert.False((await library.ScanAsync()).Single(i => i.Source != "custom").Hidden);
    }

    [Fact]
    public async Task Hidden_flags_from_before_the_list_move_onto_it()
    {
        var provider = new Switchable(Folder(@"D:\Games\Old\old.exe"));
        var (library, config, _) = await NewLibrary(provider, [@"D:\Games"]);
        var id = library.Items.Single(i => i.Source == "folder").Id;
        config.Update(c =>
        {
            c.Overrides[id] = new ItemOverride { Hidden = true };
            c.Overrides["steam:gone"] = new ItemOverride { Hidden = true, Name = "Kept name" };
        });

        Assert.True((await library.ScanAsync()).Single(i => i.Source != "custom").Hidden);
        Assert.False(config.Current.Overrides.ContainsKey(id));
        // An entry this scan did not find (partial scan, drive offline): its flag stays where it is and keeps hiding it.
        Assert.True(config.Current.Overrides["steam:gone"].Hidden);
        Assert.Equal("Kept name", config.Current.Overrides["steam:gone"].Name);
        Assert.Single(config.Current.Ignored);
        Assert.Contains(config.Current.Ignored, e => e.Keys.Contains(@"dir:d:\games\old"));

        // The migrated guess stays hidden when the scanner picks another exe.
        provider.Items = [Folder(@"D:\Games\Old\new.exe")];
        Assert.True((await library.ScanAsync()).Single(i => i.Source != "custom").Hidden);

        // The store comes back: now its flag moves, with its id, and it stays hidden.
        provider.Items = [Folder(@"D:\Games\Old\new.exe"), Store("steam:gone")];
        Assert.True((await library.ScanAsync()).Single(i => i.Id == "steam:gone").Hidden);
        Assert.False(config.Current.Overrides["steam:gone"].Hidden);
        Assert.Contains(config.Current.Ignored, e => e.Keys.Contains("id:steam:gone"));
    }

    [Fact]
    public void A_legacy_flag_of_an_entry_the_scan_did_not_find_stays_and_still_hides()
    {
        var config = new AppConfig();
        config.Overrides["epic:Offline"] = new ItemOverride { Hidden = true };
        Assert.False(LibraryIgnore.MigrateLegacy(config, [], 1));
        Assert.True(config.Overrides["epic:Offline"].Hidden);
        Assert.Empty(config.Ignored);
    }

    [Fact]
    public async Task Removing_an_own_entry_takes_it_off_the_list()
    {
        var (library, config, _) = await NewLibrary(new Switchable(), []);
        library.AddCustomEntries([new CustomEntry { Name = "RetroArch", Type = "game", Path = @"D:\RetroArch\retroarch.exe" }]);
        await library.ScanAsync();
        library.SetHidden("custom:retroarch", true);
        library.RemoveCustomEntry("custom:retroarch");
        Assert.Empty(config.Current.Ignored);

        // A new entry with the same name (the same id) is not hidden.
        library.AddCustomEntries([new CustomEntry { Name = "RetroArch", Type = "game", Path = @"E:\RetroArch\retroarch.exe" }]);
        Assert.False((await library.ScanAsync()).Single(i => i.Id == "custom:retroarch").Hidden);
    }

    [Fact]
    public void Hiding_is_a_gated_library_change()
    {
        // Not open on the locked couch; an entry call (refused for entries of a switched off area); a parents' grant of its own.
        Assert.DoesNotContain("hideEntry", MegaHub.Core.Guard.LockPolicy.OpenWhileLocked);
        Assert.DoesNotContain("showEntry", MegaHub.Core.Guard.LockPolicy.OpenWhileLocked);
        Assert.DoesNotContain("editItem", MegaHub.Core.Guard.LockPolicy.OpenWhileLocked);
        Assert.Contains("hideEntry", FeatureAreas.ItemMethods);
        Assert.DoesNotContain("removeIgnoredOrphan", MegaHub.Core.Guard.LockPolicy.OpenWhileLocked);
        Assert.Contains("removeIgnoredOrphan", FeatureAreas.AlwaysMethods);
        Assert.Contains("showEntry", FeatureAreas.ItemMethods);
        // With parental controls on (or a parents' code in the service) the couch code never stands in for the parents' code.
        Assert.Equal(ParentCheckKind.Parent, ParentCodeKind.For(parentalEnabled: true, "editHidden", serviceParentKnownAbsent: true));
        Assert.Equal(ParentCheckKind.Couch, ParentCodeKind.For(parentalEnabled: false, "editHidden", serviceParentKnownAbsent: true));
    }

    private static async Task<(LibraryService, ConfigStore, string)> NewLibrary(ILibraryProvider provider, List<string> folders, params ILibraryProvider[] more)
    {
        var dir = Directory.CreateTempSubdirectory("megahub-ignore-").FullName;
        var config = new ConfigStore(dir);
        config.Update(c => c.GameFolders = folders);
        var artwork = new ArtworkResolver(new HttpClient(new Offline()), Path.Combine(dir, "art.json"), null);
        // The test provider stands in for the core's folder scan (that one reads the disk); any other provider is a plugin.
        var library = new LibraryService(config, artwork, [provider, .. more]) { IsFolderScan = p => p == provider };
        await library.ScanAsync();
        return (library, config, dir);
    }

    [Fact]
    public async Task A_plugin_entry_that_calls_itself_folder_is_known_by_its_id_only()
    {
        var plugin = new Switchable(Folder(@"D:\Games\Tools\plugin.exe", "Plugin thing"));
        var (library, config, _) = await NewLibrary(new Switchable(Folder(@"D:\Games\Tools\tool.exe")), [@"D:\Games"], plugin);
        var pluginItem = library.Items.Single(i => i.Name == "Plugin thing");
        library.SetHidden(pluginItem.Id, true);
        Assert.Equal([$"id:{pluginItem.Id}"], Assert.Single(config.Current.Ignored).Keys);
        // The real folder guess in the same game folder stays visible.
        Assert.False(library.Items.Single(i => i.Name == "Guess").Hidden);
    }

    [Fact]
    public async Task Hiding_one_of_two_guesses_in_a_game_folder_updates_the_other_at_once()
    {
        var provider = new Switchable(Folder(@"D:\Games\Junk\a.exe", "A"), Folder(@"D:\Games\Junk\b.exe", "B"));
        var (library, _, _) = await NewLibrary(provider, [@"D:\Games"]);
        library.SetHidden(library.Items.Single(i => i.Name == "A").Id, true);
        Assert.True(library.Items.Single(i => i.Name == "B").Hidden);
        library.SetHidden(library.Items.Single(i => i.Name == "B").Id, false);
        Assert.False(library.Items.Single(i => i.Name == "A").Hidden);
    }

    [Fact]
    public async Task A_full_list_refuses_the_whole_edit_before_anything_is_stored()
    {
        var (library, config, _) = await NewLibrary(new Switchable(Store("steam:10", "Real Game")), []);
        config.Update(c => c.Ignored = [.. Enumerable.Range(0, LibraryIgnore.MaxEntries).Select(i => new IgnoredEntry { Keys = [$"id:x:{i}"] })]);
        Assert.Throws<InvalidOperationException>(() => library.EnsureCanHide("steam:10"));
        Assert.Throws<InvalidOperationException>(() => library.EditItem("steam:10", "Renamed", "app", hidden: true));
        Assert.False(config.Current.Overrides.ContainsKey("steam:10"));
        Assert.Equal("Real Game", library.Find("steam:10")!.Name);
    }

    [Fact]
    public async Task Records_no_entry_matches_are_listed_and_can_be_removed()
    {
        var (library, config, _) = await NewLibrary(new Switchable(Store("steam:10", "Real Game")), []);
        library.SetHidden("steam:10", true);
        config.Update(c => c.Ignored.Add(new IgnoredEntry { Keys = ["id:steam:99", "exe:x"], Name = "Uninstalled" }));
        var orphan = Assert.Single(library.IgnoredOrphans());
        Assert.Equal(("id:steam:99", "Uninstalled"), (orphan.Key, orphan.Name));
        Assert.Throws<KeyNotFoundException>(() => library.RemoveIgnoredOrphan("id:steam:10"));
        library.RemoveIgnoredOrphan("id:steam:99");
        Assert.Empty(library.IgnoredOrphans());
        Assert.Equal(["id:steam:10"], Assert.Single(config.Current.Ignored).Keys);
    }

    [Fact]
    public void An_unbound_grant_pays_for_one_use_only_which_is_why_the_edit_uses_it_once()
    {
        var now = DateTimeOffset.UtcNow;
        var grant = new ParentGrant(() => now, TimeSpan.FromMinutes(1));
        grant.Give();
        var gate = new ParentGate(grant, () => true, () => { });
        var hide = gate.Check("editHidden", "steam:10");
        var type = gate.Check("editType", "steam:10");
        Assert.NotNull(hide);
        Assert.NotNull(type);
        gate.Use(hide);
        Assert.Throws<ParentCodeNeededException>(() => gate.Use(type));
    }

    [Fact]
    public async Task Orphans_are_offered_only_after_a_complete_scan()
    {
        // Before the first scan finished.
        var dir = Directory.CreateTempSubdirectory("megahub-ignore-").FullName;
        var config = new ConfigStore(dir);
        config.Update(c => c.Ignored = [new IgnoredEntry { Keys = ["id:steam:99"], Name = "Gone" }]);
        var artwork = new ArtworkResolver(new HttpClient(new Offline()), Path.Combine(dir, "art.json"), null);
        var library = new LibraryService(config, artwork, [new Switchable(Store("steam:10"))]);
        Assert.Empty(library.IgnoredOrphans());
        await library.ScanAsync();
        Assert.Single(library.IgnoredOrphans());

        // A provider that failed: its entries are unknown, not gone.
        var (lib2, config2, _) = await NewLibrary(new Switchable(Store("steam:10")), [], new Failing());
        config2.Update(c => c.Ignored = [new IgnoredEntry { Keys = ["id:other:1"], Name = "Maybe back" }]);
        Assert.Empty(lib2.IgnoredOrphans());

        // Plugins still loading, or paused by Offline mode.
        var (lib3, config3, _) = await NewLibrary(new Switchable(Store("steam:10")), []);
        config3.Update(c => c.Ignored = [new IgnoredEntry { Keys = ["id:other:1"], Name = "Plugin game" }]);
        lib3.ScanComplete = () => false;
        await lib3.ScanAsync();
        Assert.Empty(lib3.IgnoredOrphans());
        lib3.ScanComplete = () => true;
        Assert.Empty(lib3.IgnoredOrphans());
        await lib3.ScanAsync();
        Assert.Single(lib3.IgnoredOrphans());
        // Paused again after the scan: not offered right now either.
        lib3.ScanComplete = () => false;
        Assert.Empty(lib3.IgnoredOrphans());
    }

    [Fact]
    public async Task Records_of_an_offline_folder_or_drive_are_not_offered()
    {
        var (library, config, _) = await NewLibrary(new Switchable(Store("steam:10")), [@"D:\Games"]);
        config.Update(c => c.Ignored =
        [
            new IgnoredEntry { Keys = [@"id:folder:a", @"dir:d:\games\old"], Name = "On D" },
            new IgnoredEntry { Keys = [@"id:folder:b", @"exe:e:\tools\x.exe"], Name = "On E" },
            new IgnoredEntry { Keys = ["id:steam:99"], Name = "Plain" }
        ]);
        library.DirectoryExists = _ => false;
        Assert.Equal(["Plain"], library.IgnoredOrphans().Select(o => o.Name));
        library.DirectoryExists = p => p.StartsWith("D:", StringComparison.OrdinalIgnoreCase);
        // D is there with its watched folder; E is not.
        Assert.Equal(["On D", "Plain"], library.IgnoredOrphans().Select(o => o.Name).Order());
        library.DirectoryExists = p => p.Equals(@"D:\", StringComparison.OrdinalIgnoreCase);
        // The drive is there but the watched folder is not.
        Assert.Equal(["Plain"], library.IgnoredOrphans().Select(o => o.Name));
    }

    [Fact]
    public async Task Removing_an_orphan_leaves_a_live_record_that_shares_a_key()
    {
        var (library, config, _) = await NewLibrary(new Switchable(Store("steam:10")), []);
        library.SetHidden("steam:10", true);
        // A damaged file: a record that shares a key with the live one.
        config.Update(c => c.Ignored.Add(new IgnoredEntry { Keys = ["id:steam:99", "id:steam:10"], Name = "Overlap" }));
        Assert.Empty(library.IgnoredOrphans());
        Assert.Throws<KeyNotFoundException>(() => library.RemoveIgnoredOrphan("id:steam:10"));
        Assert.Equal(2, config.Current.Ignored.Count);
        config.Update(c => c.Ignored.Add(new IgnoredEntry { Keys = ["id:steam:77"], Name = "Orphan" }));
        library.RemoveIgnoredOrphan("id:steam:77");
        Assert.Equal(2, config.Current.Ignored.Count);
        Assert.Contains(config.Current.Ignored, e => e.Keys.Contains("id:steam:10"));
        Assert.False(LibraryIgnore.RemoveRecord(config.Current.Ignored, "id:steam:10", new HashSet<string>()));
    }

    [Fact]
    public void The_list_is_capped_and_cleaned_on_load()
    {
        var entries = Enumerable.Range(0, LibraryIgnore.MaxEntries + 50).Select(i => $"{{\"keys\":[\"id:s:{i}\"],\"name\":\"n\\u0007{i}\"}}");
        var json = "{\"ignored\":[null,{\"keys\":[]}," + string.Join(",", entries) + "]}";
        var config = ConfigStore.Parse(json)!;
        Assert.Equal(LibraryIgnore.MaxEntries, config.Ignored.Count);
        Assert.Equal("id:s:0", config.Ignored[0].Keys[0]);
        Assert.DoesNotContain('\u0007', config.Ignored[0].Name);
    }

    [Fact]
    public void Every_way_to_hide_goes_through_the_one_gate()
    {
        var root = RepoRoot();
        var hud = File.ReadAllText(Path.Combine(root, "src", "MegaHub.Shell", "Hud.cs"));
        var bridge = File.ReadAllText(Path.Combine(root, "src", "MegaHub.Shell", "UiHost", "UiBridge.cs"));
        var tools = File.ReadAllText(Path.Combine(root, "src", "MegaHub.Shell", "Services", "HudTools.cs"));
        Assert.Contains("case \"hideEntry\":\n                return ToDto(hud.SetHidden(StringArg(0), true));", bridge.Replace("\r\n", "\n"));
        Assert.Contains("case \"showEntry\":\n                return ToDto(hud.SetHidden(StringArg(0), false));", bridge.Replace("\r\n", "\n"));
        Assert.Contains("\"edit_item\" => Summary(hud.EditItem(", tools);

        string Body(string signature)
        {
            var start = hud.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature);
            var end = hud.IndexOf("\n    }", start, StringComparison.Ordinal);
            return hud[start..end];
        }
        var setHidden = Body("public LibraryItem SetHidden(string id, bool hidden)");
        Assert.Contains("RequireHideChange(id, hidden)", setHidden);
        Assert.True(setHidden.IndexOf("UseUp(", StringComparison.Ordinal) < setHidden.IndexOf("library.SetHidden(", StringComparison.Ordinal));
        var edit = Body("public LibraryItem EditItem(string id, string? name, string? type, bool? hidden)");
        Assert.Contains("RequireHideChange(id, hidden.Value)", edit);
        // Every check before the grants are used up, the grants before anything is stored.
        Assert.True(edit.IndexOf("entry.notAGame", StringComparison.Ordinal) < edit.IndexOf("UseUp(hideTicket ?? typeTicket)", StringComparison.Ordinal));
        Assert.True(edit.IndexOf("UseUp(hideTicket ?? typeTicket)", StringComparison.Ordinal) < edit.IndexOf("library.EditItem(", StringComparison.Ordinal));
        // One use for the whole edit, and the type is validated exactly (game or app) before any grant is looked at or used.
        Assert.Equal(1, Regex.Matches(edit, @"UseUp\(").Count);
        var typeCheck = edit.IndexOf("not (\"game\" or \"app\")", StringComparison.Ordinal);
        Assert.True(typeCheck >= 0 && typeCheck < edit.IndexOf("RequireHideChange(", StringComparison.Ordinal));
        Assert.DoesNotContain("RequireParentGrant(", edit);
        var gate = Body("private HideCheck RequireHideChange(string id, bool hidden)");
        Assert.Contains("RequireUnlocked();", gate);
        Assert.Contains("CheckParentGrant(\"editHidden\", id)", gate);
        Assert.Contains("library.EnsureCanHide(id)", gate);
        var orphan = Body("public IReadOnlyList<IgnoredOrphan> RemoveIgnoredOrphan(string key)");
        Assert.Contains("RequireUnlocked();", orphan);
        Assert.Contains("CheckParentGrant(\"editHidden\", key)", orphan);
        // The library itself is never changed from the host past this gate.
        Assert.Equal(1, Regex.Matches(hud, @"library\.SetHidden\(").Count);
        Assert.Equal(1, Regex.Matches(hud, @"library\.EditItem\(").Count);
        Assert.Equal(1, Regex.Matches(hud, @"library\.RemoveIgnoredOrphan\(").Count);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MegaHub.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    private sealed class Switchable(params LibraryItem[] items) : ILibraryProvider
    {
        public IReadOnlyList<LibraryItem> Items { get; set; } = items;
        public string Name => "test";
        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken) => Task.FromResult(Items);
    }

    private sealed class Failing : ILibraryProvider
    {
        public string Name => "failing";
        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken) => throw new IOException("offline");
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("offline");
    }
}
