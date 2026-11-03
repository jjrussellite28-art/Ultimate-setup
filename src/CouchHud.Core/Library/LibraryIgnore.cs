using CouchHud.Core.Config;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>A record of the ignore list that no entry of the library matches any more: shown by name, removable by its first key.</summary>
public sealed record IgnoredOrphan(string Key, string Name, string Source);

/// <summary>
/// The ignore list behind "Hide" (Ausblenden): an entry put on it is left out of every list (Games, Apps, collections, Favorites) and stays
/// hidden after rescans and when a scanner finds it again. Pure rules; <see cref="LibraryService"/> stores the list in config.json
/// (<see cref="AppConfig.Ignored"/>) and the host decides who may change it (unlocked couch, parents' code with parental controls on).
/// <para>
/// An entry is remembered by keys that do not change with a rescan. Store games, Xbox apps, plugin and own entries have stable ids
/// ("steam:1245620", "epic:Fortnite", "custom:kodi"), so their id is the key. A guess of a watched game folder is named after the exe the
/// scanner picked, which can change (a game update brings a bigger exe, the scanner prefers another one): those are also remembered by the
/// exe path and by the game's folder below the watched folder, so the guess stays hidden whichever exe the next scan picks.
/// </para>
/// Hidden entries stay in the library the host keeps: parental controls still see them (play time, limits, the item lock), only the lists
/// leave them out.
/// </summary>
public static class LibraryIgnore
{
    /// <summary>More than any real library; a full list refuses further entries instead of dropping old ones (that would bring them back).</summary>
    public const int MaxEntries = 2000;

    /// <summary>Names are shown only; a longer one is cut.</summary>
    public const int MaxNameLength = 200;

    /// <summary>Keys longer than this are never stored or matched (a path of that length is no real install).</summary>
    public const int MaxKeyLength = 1024;

    public const string IdPrefix = "id:";
    public const string ExePrefix = "exe:";
    public const string DirPrefix = "dir:";

    /// <summary>
    /// The keys an entry is known by: always its id; a game folder guess also by its exe and by its game folder (the first folder below the
    /// watched folder it was found in, or the watched folder itself when the exe sits directly in it). <paramref name="folderGuess"/> says the
    /// entry came from the core's folder scan (<see cref="FolderGamesProvider"/>); an entry of a plugin that calls itself "folder" is known by
    /// its id only.
    /// </summary>
    public static IReadOnlyList<string> KeysOf(LibraryItem item, IEnumerable<string>? watchedFolders, bool folderGuess)
    {
        var keys = new List<string>();
        Add(keys, IdPrefix + item.Id);
        if (!folderGuess || item.Source != "folder" || item.Launch.Kind != LaunchKind.Exe) return keys;
        var exe = NormalizePath(item.Launch.Target);
        if (exe.Length == 0) return keys;
        Add(keys, ExePrefix + exe);
        if (GameFolderOf(exe, watchedFolders) is { } dir) Add(keys, DirPrefix + dir);
        return keys;
    }

    private static void Add(List<string> keys, string key)
    {
        if (key.Length <= MaxKeyLength && !keys.Contains(key, StringComparer.Ordinal)) keys.Add(key);
    }

    /// <summary>The folder of the game below a watched folder: "d:\games\hades" for "d:\games\hades\bin\hades.exe" watched as "D:\Games".</summary>
    internal static string? GameFolderOf(string normalizedExe, IEnumerable<string>? watchedFolders)
    {
        string? best = null;
        var bestWatched = -1;
        foreach (var raw in watchedFolders ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var watched = NormalizePath(raw);
            if (watched.Length == 0 || !normalizedExe.StartsWith(watched + "\\", StringComparison.Ordinal)) continue;
            var rest = normalizedExe[(watched.Length + 1)..];
            var cut = rest.IndexOf('\\');
            // The exe sits directly in the watched folder: that folder is the game (the user picked "D:\Games\Hades").
            var dir = cut < 0 ? watched : watched + "\\" + rest[..cut];
            // Nested watched folders: the innermost one decides (its subfolders are the games).
            if (best is null || watched.Length > bestWatched) (best, bestWatched) = (dir, watched.Length);
        }
        return best;
    }

    /// <summary>Lower case, backslashes, no doubled or trailing separators, no "." segments: the same folder always gives the same key.</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)) return "";
        var parts = path.Trim().Replace('/', '\\').Split('\\');
        var kept = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            // A UNC path keeps its two leading separators.
            if (part.Length == 0 && (i > 1 || !path.Trim().Replace('/', '\\').StartsWith(@"\\", StringComparison.Ordinal))) continue;
            if (part == ".") continue;
            kept.Add(part);
        }
        return string.Join('\\', kept).TrimEnd('\\').ToLowerInvariant();
    }

    /// <summary>Every key of the list (entries without keys and overlong keys are skipped).</summary>
    public static HashSet<string> KeySet(IEnumerable<IgnoredEntry?>? entries)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries ?? [])
            foreach (var key in entry?.Keys ?? [])
                if (!string.IsNullOrEmpty(key) && key.Length <= MaxKeyLength) set.Add(key);
        return set;
    }

    /// <summary>The entry is on the list when any of its keys is.</summary>
    public static bool IsIgnored(IReadOnlyList<string> keys, IReadOnlySet<string> ignored) => keys.Any(ignored.Contains);

    /// <summary>
    /// Puts the entry on the list (or adds its current keys to the record it already has, e.g. a folder guess that got a new id). Throws
    /// <see cref="InvalidOperationException"/> with <paramref name="fullMessage"/> when the list is full.
    /// </summary>
    public static void Hide(List<IgnoredEntry> list, LibraryItem item, IReadOnlyList<string> keys, long now, string fullMessage = "ignore list full")
    {
        list.RemoveAll(e => e is null);
        if (!CanHide(list, keys)) throw new InvalidOperationException(fullMessage);
        var existing = list.FirstOrDefault(e => (e.Keys ?? []).Any(keys.Contains));
        if (existing is not null)
        {
            existing.Keys = [.. (existing.Keys ?? []).Concat(keys).Distinct(StringComparer.Ordinal)];
            existing.Name = CleanName(item.Name);
            return;
        }
        list.Add(new IgnoredEntry { Keys = [.. keys], Name = CleanName(item.Name), Source = CleanName(item.Source), HiddenAt = now });
    }

    /// <summary>Whether <see cref="Hide"/> would succeed: the entry already has a record, or the list has room for one more.</summary>
    public static bool CanHide(IReadOnlyList<IgnoredEntry?>? list, IReadOnlyList<string> keys) =>
        list is null || list.Any(e => e is not null && (e.Keys ?? []).Any(keys.Contains)) || list.Count(e => e is not null) < MaxEntries;

    /// <summary>Takes every record off the list that shares a key with the entry; returns how many went.</summary>
    public static int Show(List<IgnoredEntry> list, IReadOnlyList<string> keys) =>
        list.RemoveAll(e => e is null || (e.Keys ?? []).Any(keys.Contains));

    /// <summary>
    /// Hidden flags from before 0.6.4 lived in the item's override (keyed by its id only). They move onto the list with the entry's stable
    /// keys, so the next exe pick of a folder guess no longer brings it back. Only entries this scan found move (their keys are known only
    /// then): a flag of an entry missing now (a partial scan, a drive that is offline, a skipped store) stays where it is and still hides the
    /// entry until a later scan finds it. Returns whether anything changed.
    /// </summary>
    public static bool MigrateLegacy(AppConfig config, IEnumerable<LibraryItem> scanned, long now, Func<LibraryItem, bool>? isFolderGuess = null)
    {
        config.Ignored ??= [];
        var changed = false;
        var byId = scanned.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var (id, o) in config.Overrides.ToList())
        {
            if (o is null || !o.Hidden || byId.GetValueOrDefault(id) is not { } item) continue;
            var keys = KeysOf(item, config.GameFolders, isFolderGuess?.Invoke(item) == true);
            if (CanHide(config.Ignored, keys))
            {
                Hide(config.Ignored, item, keys, now);
                o.Hidden = false;
                if (o.IsEmpty) config.Overrides.Remove(id);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// The list as read from config.json, made safe: no empty records, no keys that are empty or overlong, at most <see cref="MaxEntries"/>
    /// records (a hand-edited or damaged file never grows past the cap), names cleaned. Returns whether anything changed.
    /// </summary>
    public static bool Sanitize(AppConfig config)
    {
        var before = config.Ignored;
        var clean = new List<IgnoredEntry>();
        foreach (var e in before ?? [])
        {
            if (e is null) continue;
            var keys = (e.Keys ?? []).Where(k => !string.IsNullOrEmpty(k) && k.Length <= MaxKeyLength).Distinct(StringComparer.Ordinal).ToList();
            if (keys.Count == 0) continue;
            if (clean.Count >= MaxEntries) break;
            clean.Add(new IgnoredEntry { Keys = keys, Name = CleanName(e.Name), Source = CleanName(e.Source), HiddenAt = e.HiddenAt });
        }
        var changed = before is null || before.Count != clean.Count || before.Zip(clean).Any(p =>
            p.First is null || !(p.First.Keys ?? []).SequenceEqual(p.Second.Keys) || p.First.Name != p.Second.Name || p.First.Source != p.Second.Source);
        config.Ignored = clean;
        return changed;
    }

    /// <summary>
    /// Takes the one record whose first key is <paramref name="key"/> off the list ("Remove" for a record no entry of the library matches any
    /// more). <paramref name="orphanKeys"/> are the first keys of the records that really are orphans: another record that merely shares a
    /// key is never touched.
    /// </summary>
    public static bool RemoveRecord(List<IgnoredEntry> list, string key, IReadOnlySet<string> orphanKeys)
    {
        if (!orphanKeys.Contains(key)) return false;
        return list.RemoveAll(e => e?.Keys is { Count: > 0 } && e.Keys[0] == key) > 0;
    }

    internal static string CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > MaxNameLength ? clean[..MaxNameLength] : clean;
    }
}
