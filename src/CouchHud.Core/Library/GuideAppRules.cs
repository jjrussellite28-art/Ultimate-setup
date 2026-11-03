using CouchHud.Core.Guard;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>
/// Apps in the Guide menu: a quick start during a game. The list only says which apps are offered there; starting one goes through the
/// library's own start (<c>Hud.Launch</c>), with every check of the library (couch lock, areas, the child's rules, the parents' code), so
/// the menu is never a second way to start something. What is offered is narrowed to what that start would let through.
/// </summary>
public static class GuideAppRules
{
    public const int Max = 12;

    private const int MaxIdLength = 4200;

    /// <summary>An id list as it may be kept: no empty, over-long or duplicate ids, cut at <see cref="Max"/>. Order is kept.</summary>
    public static List<string> Clean(IEnumerable<string?>? ids)
    {
        var result = new List<string>();
        foreach (var raw in ids ?? [])
        {
            var id = raw?.Trim() ?? "";
            if (id.Length is 0 or > MaxIdLength || id.Any(char.IsControl) || result.Contains(id, StringComparer.Ordinal) || result.Count >= Max) continue;
            result.Add(id);
        }
        return result;
    }

    /// <summary>Only an app can be put into the Guide menu (games start from the library; a second game during a game is the library's own question).</summary>
    public static bool CanBe(LibraryItem? item) => item is { Type: ItemType.App };

    /// <summary>
    /// The entries the menu shows for this person right now, in the saved order. On a locked couch there are none (an app needs the unlock,
    /// <see cref="LockPolicy.MayLaunchLocked"/>). <paramref name="shown"/> is the areas, <paramref name="allowed"/> the child's own list and
    /// age rules (the entry would be refused anyway; hiding it is the courtesy).
    /// </summary>
    public static IReadOnlyList<LibraryItem> Visible(IEnumerable<string?>? ids, Func<string, LibraryItem?> find, Func<LibraryItem, bool> shown, Func<LibraryItem, bool> allowed, bool locked)
    {
        var list = new List<LibraryItem>();
        foreach (var id in Clean(ids))
        {
            if (find(id) is not { } item || !CanBe(item)) continue;
            if (locked && !LockPolicy.MayLaunchLocked(item.Type)) continue;
            if (!shown(item) || !allowed(item)) continue;
            list.Add(item);
        }
        return list;
    }

    /// <summary>The list after switching one app on or off. Switching on at the limit, or for something that is no app, is refused (null).</summary>
    public static List<string>? With(IEnumerable<string?>? ids, LibraryItem? item, string id, bool on)
    {
        var list = Clean(ids);
        if (!on)
        {
            list.RemoveAll(x => x == id);
            return list;
        }
        if (!CanBe(item) || item!.Id != id) return null;
        if (list.Contains(id)) return list;
        if (list.Count >= Max) return null;
        list.Add(id);
        return list;
    }
}
