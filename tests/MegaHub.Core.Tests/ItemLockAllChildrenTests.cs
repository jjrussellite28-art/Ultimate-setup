using System.Text.Json;
using MegaHub.Core.Config;
using MegaHub.Core.Files;
using MegaHub.Core.Library;
using MegaHub.Core.Parental;
using CouchHud.Sdk;
using Xunit;

namespace MegaHub.Core.Tests;

/// <summary>
/// A game locked for all child profiles: one household list besides each profile's own, read through ProfileView.EffectiveRules, so a child
/// made later is covered, an adult is not, a damaged list locks everything for children, and the sanitizer keeps the list sane.
/// </summary>
public sealed class ItemLockAllChildrenTests
{
    private static LibraryItem Item(string id) => new()
    {
        Id = id,
        Source = id.Split(':')[0],
        Name = id,
        Type = ItemType.Game,
        Launch = new LaunchTarget(LaunchKind.Exe, "x")
    };

    private static AppConfig Config()
    {
        var c = new AppConfig();
        c.Parental.Enabled = true;
        return c;
    }

    private static bool LockedFor(AppConfig c, ProfileView p, LibraryItem item) => ParentalService.IsLocked(p.EffectiveRules, item, [item]);

    [Fact]
    public void A_lock_for_all_children_holds_in_every_child_profile_and_a_lock_of_one_profile_does_not()
    {
        var c = Config();
        var a = c.AddProfile("A");
        var b = c.AddProfile("B");
        var game = Item("steam:1");
        var other = Item("steam:2");
        a.Rules.LockedItems.Add("steam:2"); // only A
        c.LockedItemsAllChildren!.Add("steam:1"); // everyone
        Assert.True(LockedFor(c, a, game));
        Assert.True(LockedFor(c, b, game));
        Assert.True(LockedFor(c, a, other));
        Assert.False(LockedFor(c, b, other), "a lock of one profile stays in that profile");
    }

    [Fact]
    public void A_lock_for_all_children_applies_to_a_child_profile_created_later()
    {
        var c = Config();
        c.LockedItemsAllChildren!.Add("steam:1");
        var later = c.AddProfile("Later");
        Assert.DoesNotContain("steam:1", later.Rules.LockedItems); // not copied: the household list is read live
        Assert.True(LockedFor(c, later, Item("steam:1")));
        c.LockedItemsAllChildren.Remove("steam:1");
        Assert.False(LockedFor(c, later, Item("steam:1")), "lifting it frees every child at once");
    }

    [Fact]
    public void An_adult_profile_has_no_locks_from_the_list()
    {
        var c = Config();
        c.LockedItemsAllChildren!.Add("steam:1");
        var adult = c.AddProfile("Parent", restricted: false);
        Assert.False(LockedFor(c, adult, Item("steam:1")));
        Assert.Empty(adult.EffectiveRules.LockedItems);
        // The default profile without any limit is an adult's too; with a limit (a child that played there before profiles) it is a child's.
        var def = c.Profile(ProfileIds.Default);
        def.Rules.WeekdayMinutes = 0;
        def.Rules.WeekendMinutes = 0;
        Assert.False(LockedFor(c, def, Item("steam:1")));
        def.Rules.WeekdayMinutes = 60;
        Assert.True(LockedFor(c, def, Item("steam:1")));
        // Making the adult a child again brings the lock back, making the child an adult takes it away.
        c.SetProfileRestricted(adult.Id, true);
        Assert.True(LockedFor(c, c.Profile(adult.Id), Item("steam:1")));
    }

    [Fact]
    public void The_shared_list_reaches_the_launch_verdict_and_the_shelf()
    {
        var c = Config();
        var kid = c.AddProfile("Kid");
        c.LockedItemsAllChildren!.Add("steam:1");
        var game = Item("steam:1");
        Assert.Equal("locked", ParentalService.CodeReason(kid.EffectiveRules, game, [game], null));
        Assert.Null(ParentalService.CodeReason(kid.EffectiveRules, Item("steam:2"), [game], null));
        Assert.False(MegaHub.Core.Library.FavoritesShelf.ChildAllows(kid.EffectiveRules, game, [game], null));
        // The stored rules alone do not know the household list: the judging must go through EffectiveRules.
        Assert.Null(ParentalService.CodeReason(kid.Rules, game, [game], null));
    }

    [Fact]
    public void A_duplicate_entry_of_a_game_locked_for_all_children_is_locked_too()
    {
        var c = Config();
        var kid = c.AddProfile("Kid");
        var steam = Item("steam:730");
        var shortcut = new LibraryItem { Id = "custom:cs", Source = "custom", Name = "cs", Type = ItemType.Game, Launch = new LaunchTarget(LaunchKind.Url, "steam://rungameid/730") };
        c.LockedItemsAllChildren!.Add("steam:730");
        Assert.True(ParentalService.IsLocked(kid.EffectiveRules, shortcut, [steam, shortcut]));
    }

    [Fact]
    public void An_unreadable_list_locks_everything_for_children_and_never_for_adults()
    {
        var c = Config();
        var kid = c.AddProfile("Kid");
        var adult = c.AddProfile("Parent", restricted: false);
        c.LockedItemsAllChildren = null;
        Assert.True(LockedFor(c, kid, Item("steam:1")));
        Assert.True(LockedFor(c, kid, Item("anything:else")));
        Assert.False(LockedFor(c, adult, Item("steam:1")));
        c.LockedItemsAllChildren = [];
        c.LockedItemsAllChildrenUnreadable = true;
        Assert.True(LockedFor(c, kid, Item("steam:1")), "marked by the sanitizer: still everything locked");
        c.LockedItemsAllChildrenUnreadable = false;
        Assert.False(LockedFor(c, kid, Item("steam:1")));
    }

    [Fact]
    public void The_effective_rules_are_a_copy_and_are_not_written_to_the_file()
    {
        var c = Config();
        var kid = c.AddProfile("Kid");
        c.LockedItemsAllChildren!.Add("steam:1");
        var effective = kid.EffectiveRules;
        Assert.NotSame(kid.Rules, effective);
        Assert.Contains("steam:1", effective.LockedItems);
        Assert.DoesNotContain("steam:1", kid.Rules.LockedItems);
        Assert.DoesNotContain("lockEverything", JsonSerializer.Serialize(effective, ConfigStore.JsonOptions), StringComparison.OrdinalIgnoreCase);
        // Without a household list the stored rules are used as they are.
        c.LockedItemsAllChildren.Clear();
        Assert.Same(kid.Rules, kid.EffectiveRules);
    }

    [Fact]
    public void The_list_survives_a_save_and_a_load()
    {
        var c = Config();
        c.LockedItemsAllChildren!.AddRange(["steam:1", "epic:2"]);
        var back = ConfigStore.Parse(JsonSerializer.Serialize(c, ConfigStore.JsonOptions))!;
        Assert.Equal(["steam:1", "epic:2"], back.LockedItemsAllChildren);
        Assert.False(back.LockedItemsAllChildrenUnreadable);
    }

    // --- Sanitizer -------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_sanitizer_drops_null_entries_from_the_list_and_keeps_the_rest()
    {
        var c = ConfigStore.Parse("""{ "parental": { "enabled": true }, "lockedItemsAllChildren": ["a", null, "b", null] }""")!;
        Assert.Equal(["a", "b"], c.LockedItemsAllChildren);
        Assert.False(c.LockedItemsAllChildrenUnreadable);
    }

    [Fact]
    public void The_sanitizer_cuts_the_list_like_the_locks_of_a_profile_and_keeps_the_first_ones()
    {
        var ids = string.Join(",", Enumerable.Range(0, ConfigLimits.MaxListEntries + 25).Select(i => $"\"g{i}\""));
        var c = ConfigStore.Parse($$"""{ "parental": { "enabled": true }, "lockedItemsAllChildren": [{{ids}}] }""")!;
        Assert.Equal(ConfigLimits.MaxListEntries, c.LockedItemsAllChildren!.Count);
        Assert.Equal("g0", c.LockedItemsAllChildren[0]);
    }

    [Fact]
    public void A_list_that_is_null_in_the_file_becomes_empty_but_locks_everything_for_children()
    {
        var c = ConfigStore.Parse("""{ "parental": { "enabled": true }, "lockedItemsAllChildren": null }""")!;
        Assert.NotNull(c.LockedItemsAllChildren);
        Assert.True(c.LockedItemsAllChildrenUnreadable);
        var kid = c.AddProfile("Kid");
        Assert.True(LockedFor(c, kid, Item("steam:1")));
        // The mark is written, so it survives the next save and load.
        var again = ConfigStore.Parse(JsonSerializer.Serialize(c, ConfigStore.JsonOptions))!;
        Assert.True(again.LockedItemsAllChildrenUnreadable);
        Assert.Empty(again.Sanitize());
    }

    [Fact]
    public void A_file_without_the_list_has_no_extra_locks()
    {
        var c = ConfigStore.Parse("""{ "parental": { "enabled": true } }""")!;
        Assert.Empty(c.LockedItemsAllChildren!);
        Assert.False(c.LockedItemsAllChildrenUnreadable);
        Assert.False(LockedFor(c, c.AddProfile("Kid"), Item("steam:1")));
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("lockAll")]
    [InlineData("lockAllReset")]
    public void Each_lock_scope_has_its_own_grant_action_that_needs_the_parents_code_with_parental_controls_on(string action)
    {
        Assert.Contains(action, MegaHub.Core.Security.ParentCodeKind.HudOnlyActions);
        Assert.Equal(MegaHub.Core.Security.ParentCheckKind.Parent, MegaHub.Core.Security.ParentCodeKind.For(true, action, true));
    }

    [Fact]
    public void A_grant_for_one_profile_does_not_fit_the_all_children_action()
    {
        var grant = new MegaHub.Core.Security.ParentGrant(() => DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        grant.Give("lock", "steam:1");
        Assert.False(grant.Fits("lockAll", "steam:1"));
        Assert.False(grant.TryConsume("lockAll", "steam:1"));
        Assert.False(grant.TryConsume("lockAllReset"));
        Assert.True(grant.TryConsume("lock", "steam:1"));
        grant.Give("lockAll", "steam:1");
        Assert.False(grant.TryConsume("lock", "steam:1"));
        Assert.False(grant.TryConsume("lockAll", "steam:2"), "bound to the game too");
        grant.Give("lockAll", "steam:1");
        Assert.True(grant.TryConsume("lockAll", "steam:1"));
        grant.Give("lockAllReset");
        Assert.False(grant.TryConsume("lockAll", "steam:1"));
    }

    [Fact]
    public void A_folder_game_id_move_follows_the_list()
    {
        var path = Path.Combine(Path.GetTempPath(), "megahub-lockall", "Foo", "launcher.exe");
        var to = Path.Combine(Path.GetTempPath(), "megahub-lockall", "Foo", "bin", "foo.exe");
        var dir = Directory.CreateTempSubdirectory("megahub-idmove-").FullName;
        var store = new ConfigStore(dir);
        var from = FolderGamesProvider.IdOf(path);
        store.Update(x => x.LockedItemsAllChildren!.Add(from));
        var moves = FolderIdMigration.MovesFor([new ExeCandidate("Foo", to, 10, [path, to])], FolderGamesProvider.IdOf);
        Assert.Equal(1, FolderIdMigration.Apply(store, moves));
        Assert.Equal([FolderGamesProvider.IdOf(to)], store.Current.LockedItemsAllChildren);
    }
}
