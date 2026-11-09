using System.Net;
using System.Text;
using MegaHub.Core.Config;
using MegaHub.Core.Files;
using MegaHub.Core.Library;
using MegaHub.Core.Parental;
using CouchHud.Sdk;

namespace MegaHub.Core.Tests;

/// <summary>
/// The exe scoring can pick another start exe than the first plausible one did, and the id of a watched-folder entry comes from the exe's path:
/// everything stored under the old id (above all the parents' locks) has to follow, and nothing may become freer on the way.
/// </summary>
public class FolderIdMigrationTests
{
    private const string Old = @"D:\Games\Foo\launcher.exe";
    private const string New = @"D:\Games\Foo\bin\foo.exe";

    private static string OldId => FolderGamesProvider.IdOf(Old);

    private static string NewId => FolderGamesProvider.IdOf(New);

    private static IReadOnlyList<FolderIdMove> Moves() =>
        FolderIdMigration.MovesFor([new ExeCandidate("Foo", New, 10, [Old, New, @"D:\Games\Foo\unins000.exe"])], FolderGamesProvider.IdOf);

    private static ConfigStore Store() => new(Directory.CreateTempSubdirectory("megahub-idmove-").FullName);

    [Fact]
    public void A_locked_entry_whose_pick_changes_stays_locked_in_every_profile()
    {
        var config = Store();
        config.Update(c =>
        {
            c.Parental.LockedItems = [OldId, "steam:1"];
            c.AddProfile("Kid");
        });

        Assert.Equal(1, FolderIdMigration.Apply(config, Moves()));

        var c2 = config.Current;
        Assert.Equal([NewId, "steam:1"], c2.Parental.LockedItems);
        var kid = c2.AllProfiles.First(p => !p.IsDefault);
        Assert.Equal([NewId, "steam:1"], kid.Rules.LockedItems); // a new profile starts with the default's rules
        // The check the launch gate uses.
        var item = FolderGamesProvider.ToItem(new ExeCandidate("Foo", New, 10));
        Assert.True(ParentalService.IsLocked(c2.Parental, item, [item]));
        Assert.True(ParentalService.IsLocked(kid.Rules, item, [item]));
    }

    [Fact]
    public void Favorites_recent_collections_overrides_and_times_carry_over_and_the_ledger_follows()
    {
        var config = Store();
        config.Update(c =>
        {
            c.Favorites = ["x", OldId];
            c.LastLaunched[OldId] = 42;
            c.PlaytimeMinutes[OldId] = 30;
            c.LastSessionMinutes[OldId] = 5;
            c.Collections = [new ItemCollection { Id = "k", Name = "Koop", Items = [OldId, "y"] }];
            c.Overrides[OldId] = new ItemOverride { Name = "Mein Foo", Hidden = true, StoreMatch = "123" };
        });

        FolderIdMigration.Apply(config, Moves());

        var c2 = config.Current;
        Assert.Equal(["x", NewId], c2.Favorites);
        Assert.Equal(42, c2.LastLaunched[NewId]);
        Assert.Equal(30, c2.PlaytimeMinutes[NewId]);
        Assert.Equal(5, c2.LastSessionMinutes[NewId]);
        Assert.Equal([NewId, "y"], c2.Collections[0].Items);
        Assert.Equal("Mein Foo", c2.Overrides[NewId].Name);
        Assert.True(c2.Overrides[NewId].Hidden);
        Assert.Equal("123", c2.Overrides[NewId].StoreMatch);
        Assert.False(c2.Overrides.ContainsKey(OldId));
        var move = Assert.Single(c2.LimitKeyMoves);
        Assert.Equal((@"dir:d:\games\foo", @"dir:d:\games\foo\bin"), (move.From, move.To));

        var ledger = new PlayLedger();
        var day = new DateOnly(2026, 11, 2);
        ledger.Add(day, @"dir:d:\games\foo", 600);
        ledger.Add(day, @"dir:d:\games\foo\bin", 60);
        ledger.Rekey(@"dir:d:\games\foo", @"dir:d:\games\foo\bin");
        Assert.Equal(660, ledger.Seconds(day, @"dir:d:\games\foo\bin"));
        Assert.Equal(0, ledger.Seconds(day, @"dir:d:\games\foo"));
        ledger.Rekey(@"dir:d:\games\foo", @"dir:d:\games\foo\bin");
        Assert.Equal(660, ledger.Seconds(day, @"dir:d:\games\foo\bin"));
    }

    [Fact]
    public void Running_twice_changes_nothing()
    {
        var config = Store();
        config.Update(c =>
        {
            c.Favorites = [OldId];
            c.Parental.LockedItems = [OldId];
            c.PlaytimeMinutes[OldId] = 30;
        });
        Assert.Equal(1, FolderIdMigration.Apply(config, Moves()));
        var json = System.Text.Json.JsonSerializer.Serialize(config.Current);

        Assert.Equal(0, FolderIdMigration.Apply(config, Moves()));

        Assert.Equal(json, System.Text.Json.JsonSerializer.Serialize(config.Current));
        Assert.Equal(30, config.Current.PlaytimeMinutes[NewId]);
    }

    [Fact]
    public void When_both_ids_have_state_locks_are_united_and_the_users_override_wins()
    {
        var config = Store();
        config.Update(c =>
        {
            c.Parental.LockedItems = [OldId];
            c.Overrides[OldId] = new ItemOverride { Name = "Alt" };
            c.Overrides[NewId] = new ItemOverride { Name = "Neu", Hidden = true };
            c.PlaytimeMinutes[OldId] = 10;
            c.PlaytimeMinutes[NewId] = 5;
            c.AddProfile("Kid").Rules.LockedItems.Add(NewId);
        });

        FolderIdMigration.Apply(config, Moves());

        var c2 = config.Current;
        Assert.Equal([NewId], c2.Parental.LockedItems);
        Assert.Equal([NewId], c2.AllProfiles.First(p => !p.IsDefault).Rules.LockedItems);
        Assert.Equal("Alt", c2.Overrides[NewId].Name);
        Assert.True(c2.Overrides[NewId].Hidden);
        Assert.Equal(15, c2.PlaytimeMinutes[NewId]);
    }

    [Fact]
    public void An_entry_whose_folder_is_gone_is_not_touched()
    {
        var config = Store();
        config.Update(c =>
        {
            c.Parental.LockedItems = [OldId];
            c.Favorites = [OldId];
        });

        // The scan found nothing for that folder: no moves.
        Assert.Equal(0, FolderIdMigration.Apply(config, []));
        // And a move of another game does not touch it either.
        var other = FolderIdMigration.MovesFor([new ExeCandidate("Bar", @"D:\Games\Bar\bar.exe", 1, [@"D:\Games\Bar\x.exe", @"D:\Games\Bar\bar.exe"])], FolderGamesProvider.IdOf);
        Assert.Equal(0, FolderIdMigration.Apply(config, other));

        Assert.Equal([OldId], config.Current.Parental.LockedItems);
        Assert.Equal([OldId], config.Current.Favorites);
    }

    [Fact]
    public void An_unchanged_pick_makes_no_move()
    {
        var same = FolderIdMigration.MovesFor([new ExeCandidate("Foo", New, 10, [New])], FolderGamesProvider.IdOf);
        Assert.Empty(same);
    }

    private const string KeyA = @"dir:d:\games\foo";
    private const string KeyB = @"dir:d:\games\foo\bin";
    private const string KeyC = @"dir:d:\games\foo\x64";
    private static readonly DateOnly Day = new(2026, 11, 2);

    private static void Move(ConfigStore config, string from, string to) => config.Update(c => FolderIdMigration.Record(c, from, to));

    [Fact]
    public void A_flip_flop_and_a_chain_end_on_the_current_key_and_each_move_applies_once()
    {
        var config = Store();
        var ledger = new PlayLedger();
        ledger.Add(Day, KeyA, 600);

        Move(config, KeyA, KeyB);
        Move(config, KeyB, KeyA); // the pick flipped back
        Assert.True(FolderIdMigration.ApplyPending(config, "default", ledger));
        Assert.Equal(600, ledger.Seconds(Day, KeyA));
        Assert.Equal(0, ledger.Seconds(Day, KeyB));
        // Applied: not again (a replay in another order would move the minutes away).
        ledger.Add(Day, KeyA, 60);
        Assert.False(FolderIdMigration.ApplyPending(config, "default", ledger));
        Assert.Equal(660, ledger.Seconds(Day, KeyA));

        // A chain across scans.
        Move(config, KeyA, KeyB);
        FolderIdMigration.ApplyPending(config, "default", ledger);
        Move(config, KeyB, KeyC);
        FolderIdMigration.ApplyPending(config, "default", ledger);
        Assert.Equal(660, ledger.Seconds(Day, KeyC));
        Assert.Equal(0, ledger.Seconds(Day, KeyA) + ledger.Seconds(Day, KeyB));
    }

    [Fact]
    public void An_inactive_profile_applies_the_moves_once_when_it_opens_and_the_log_is_pruned_after_everyone()
    {
        var config = Store();
        config.Update(c => c.AddProfile("Kid"));
        var kid = config.Current.AllProfiles.First(p => !p.IsDefault).Id;
        var kidLedger = new PlayLedger();
        kidLedger.Add(Day, KeyA, 300);
        var mine = new PlayLedger();
        mine.Add(Day, KeyA, 100);

        Move(config, KeyA, KeyB);
        Move(config, KeyB, KeyC);
        FolderIdMigration.ApplyPending(config, "default", mine);
        Assert.Equal(100, mine.Seconds(Day, KeyC));
        Assert.Equal(2, config.Current.LimitKeyMoves.Count); // the kid has not applied them yet

        Assert.True(FolderIdMigration.ApplyPending(config, kid, kidLedger));
        Assert.Equal(300, kidLedger.Seconds(Day, KeyC));
        Assert.False(FolderIdMigration.ApplyPending(config, kid, kidLedger));
        Assert.Empty(config.Current.LimitKeyMoves);
    }

    [Fact]
    public void Past_the_cap_the_oldest_move_goes_and_the_newest_stay()
    {
        var config = Store();
        for (var i = 0; i < FolderIdMigration.MaxLimitKeyMoves + 5; i++) Move(config, $"dir:a{i}", $"dir:b{i}");

        var log = config.Current.LimitKeyMoves;
        Assert.Equal(FolderIdMigration.MaxLimitKeyMoves, log.Count);
        Assert.Equal("dir:a5", log[0].From);
        Assert.Equal($"dir:b{FolderIdMigration.MaxLimitKeyMoves + 4}", log[^1].To);
    }

    [Fact]
    public void The_question_right_after_a_move_sees_the_old_minutes_on_the_new_key()
    {
        var config = Store();
        var ledger = new PlayLedger();
        ledger.Add(Day, KeyA, 3600);
        config.Update(c => c.Favorites = [OldId]);

        FolderIdMigration.Apply(config, Moves());
        // What the controller does before every start question.
        FolderIdMigration.ApplyPending(config, "default", ledger);

        Assert.Equal(0, ledger.Seconds(Day, KeyA));
        Assert.Equal(3600, ledger.Seconds(Day, KeyB));
    }

    [Fact]
    public void Moves_recorded_twice_in_a_row_are_one()
    {
        var config = Store();
        Move(config, KeyA, KeyB);
        Move(config, KeyA, KeyB);
        Assert.Single(config.Current.LimitKeyMoves);
    }

    [Fact]
    public void The_provider_raises_the_moves_with_the_ids_of_the_other_exes_of_the_folder()
    {
        var root = Directory.CreateTempSubdirectory("megahub-idmove-scan-").FullName;
        var game = Path.Combine(root, "Foo Game");
        Directory.CreateDirectory(Path.Combine(game, "bin"));
        File.WriteAllBytes(Path.Combine(game, "FooGameLauncher.exe"), new byte[100]);
        File.WriteAllBytes(Path.Combine(game, "bin", "FooGame.exe"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(game, "unins000.exe"), new byte[10]);
        var provider = new FolderGamesProvider(() => [root]);
        IReadOnlyList<FolderIdMove>? raised = null;
        provider.IdsMoved += m => raised = m;

        var items = provider.GetItemsAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.Single(items);
        Assert.NotNull(raised);
        var move = Assert.Single(raised!);
        Assert.Equal(items[0].Id, move.NewId);
        Assert.Contains(FolderGamesProvider.IdOf(Path.Combine(game, "unins000.exe")), move.Old.Select(o => o.Id));
    }

    [Fact]
    public void A_watched_steam_library_folder_gives_no_games()
    {
        var root = Directory.CreateTempSubdirectory("megahub-steamapps-").FullName;
        var common = Path.Combine(root, "SteamLibrary", "steamapps", "common");
        Directory.CreateDirectory(Path.Combine(common, "Portal"));
        File.WriteAllBytes(Path.Combine(common, "Portal", "portal.exe"), new byte[100]);

        Assert.Empty(ExeScanner.Scan(common));
        Assert.Empty(ExeScanner.Scan(Path.Combine(root, "SteamLibrary", "steamapps")));
        Assert.True(ExeScanner.InSteamApps(@"X:\SteamLibrary\SteamApps\common"));
        Assert.False(ExeScanner.InSteamApps(@"X:\Games\steamapps-backup"));
    }

    [Fact]
    public void A_name_search_cache_entry_from_before_is_not_reused()
    {
        var cache = Path.Combine(Directory.CreateTempSubdirectory("megahub-artv2-").FullName, "cache.json");
        var json = """{ "data": [ { "id": 2, "name": "Mortal Kombat 1" } ] }""";
        var grid = """{ "data": [ { "url": "https://cdn2.steamgriddb.com/grid/x.png", "thumb": "https://cdn2.steamgriddb.com/thumb/x.png" } ] }""";
        var item = new LibraryItem
        {
            Id = "folder:mk1", Source = "folder", Name = "Mortal Kombat 1", Type = ItemType.Game,
            Launch = new LaunchTarget(LaunchKind.Url, "https://example.com"), Art = Artwork.Empty
        };
        var first = new CountingHost(("autocomplete", json), ("/grids/", grid), ("/heroes/", grid), ("/logos/", grid));
        new ArtworkResolver(new HttpClient(first), cache, "key").ResolveAsync([item]).GetAwaiter().GetResult();
        var text = File.ReadAllText(cache);
        Assert.Contains("game:v2:mortal kombat 1", text);

        // The same cache with the key of an older version: the line is not used, the services are asked again.
        File.WriteAllText(cache, text.Replace("game:v2:", "game:"));
        var second = new CountingHost(("autocomplete", json), ("/grids/", grid), ("/heroes/", grid), ("/logos/", grid));
        new ArtworkResolver(new HttpClient(second), cache, "key").ResolveAsync([item]).GetAwaiter().GetResult();
        Assert.NotEmpty(second.Requests);
    }

    private sealed class CountingHost(params (string Part, string Json)[] routes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            foreach (var (part, json) in routes)
                if (url.Contains(part, StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            if (url.Contains("storesearch")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "items": [] }""", Encoding.UTF8, "application/json") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
