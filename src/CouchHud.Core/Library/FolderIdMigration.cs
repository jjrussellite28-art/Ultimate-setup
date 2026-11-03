using CouchHud.Core.Parental;
using CouchHud.Core.Config;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Library;

/// <summary>One watched-folder game whose start exe may have been another one before: <see cref="Old"/> are the ids the other exes of its folder would have had.</summary>
public sealed record FolderIdMove(string NewId, string NewDir, IReadOnlyList<(string Id, string Dir)> Old);

/// <summary>
/// The id of a watched-folder entry comes from the path of its start exe, and the exe scoring picks the exe by other rules than the first
/// plausible one did. For an install that picked another exe before, everything stored under the old id (favourites, collections, "recent",
/// play times, the user's overrides, game profiles, the parents' list of locked games in every profile, and the per-game play time of the
/// ledger) would be orphaned, and a locked game would start without the code. This moves it all to the new id, once, in a single config update.
/// Nothing is dropped: a lock stays a lock (locks of both ids are united), the user's own override wins over one made since, times add up.
/// Idempotent: with the old ids gone from the config a second run changes nothing. An entry whose folder is gone is never scanned, so never touched.
/// </summary>
public static class FolderIdMigration
{
    /// <summary>At most this many ledger moves are remembered.</summary>
    public const int MaxLimitKeyMoves = 256;

    public static string LimitKeyOf(string dir) => "dir:" + dir.Trim().TrimEnd('\\', '/').ToLowerInvariant();

    /// <summary>The moves for the entries found by a scan; only ones with a different old id are listed.</summary>
    public static IReadOnlyList<FolderIdMove> MovesFor(IEnumerable<CouchHud.Core.Files.ExeCandidate> found, Func<string, string> idOfPath)
    {
        var moves = new List<FolderIdMove>();
        foreach (var c in found)
        {
            if (c.LegacyPaths is not { Count: > 0 } paths) continue;
            var newId = idOfPath(c.Path);
            var old = paths.Select(p => (Id: idOfPath(p), Dir: WinPath.DirectoryName(p) ?? ""))
                .Where(o => o.Id != newId).DistinctBy(o => o.Id).ToList();
            if (old.Count > 0) moves.Add(new FolderIdMove(newId, WinPath.DirectoryName(c.Path) ?? "", old));
        }
        return moves;
    }

    /// <summary>Applies the moves; returns how many ids were moved (0: the config was not touched).</summary>
    public static int Apply(ConfigStore config, IReadOnlyList<FolderIdMove> moves)
    {
        if (moves.Count == 0) return 0;
        var current = config.Current;
        var relevant = moves.Where(m => m.Old.Any(o => Has(current, o.Id))).ToList();
        if (relevant.Count == 0) return 0;
        var moved = 0;
        config.Update(c =>
        {
            foreach (var m in relevant)
            {
                foreach (var (oldId, oldDir) in m.Old)
                {
                    if (!Has(c, oldId)) continue;
                    MoveEverywhere(c, oldId, m.NewId);
                    moved++;
                    var from = LimitKeyOf(oldDir);
                    var to = LimitKeyOf(m.NewDir);
                    if (from != to) Record(c, from, to);
                }
            }
        });
        if (moved > 0) Console.Error.WriteLine($"[folders] {moved} entr{(moved == 1 ? "y" : "ies")} follow their new start program (saved settings, locks and play time moved)");
        return moved;
    }

    /// <summary>
    /// Appends a move to the log (copy on write); the same move twice in a row is one. Past <see cref="MaxLimitKeyMoves"/> the oldest go, with a
    /// log line: a profile that has not applied them by then loses the carry-over of that game's minutes, nothing else.
    /// </summary>
    internal static void Record(AppConfig c, string from, string to)
    {
        var log = c.LimitKeyMoves;
        if (log.Count > 0 && log[^1].From == from && log[^1].To == to) return;
        var next = new List<LimitKeyMove>(log) { new() { Seq = ++c.LimitKeyMoveSeq, From = from, To = to } };
        if (next.Count > MaxLimitKeyMoves)
        {
            var drop = next.Count - MaxLimitKeyMoves;
            Console.Error.WriteLine($"[folders] {drop} oldest play time move(s) dropped (more than {MaxLimitKeyMoves} pending)");
            next.RemoveRange(0, drop);
        }
        c.LimitKeyMoves = next;
    }

    /// <summary>
    /// Lets one profile's ledger follow the recorded moves it has not seen yet, once and in order, and marks them applied (the log is pruned
    /// when every profile has). Cheap when there is nothing new (no lock, no save). Returns whether anything was applied.
    /// </summary>
    public static bool ApplyPending(ConfigStore config, string profileId, PlayLedger ledger)
    {
        var cfg = config.Current;
        var seq = cfg.LimitKeyMoveSeq;
        if (seq == 0 || cfg.LimitKeyApplied.GetValueOrDefault(profileId) >= seq) return false;
        var done = cfg.LimitKeyApplied.GetValueOrDefault(profileId);
        foreach (var m in cfg.LimitKeyMoves.Where(m => m.Seq > done).OrderBy(m => m.Seq).ToList()) ledger.Rekey(m.From, m.To);
        config.Update(c =>
        {
            var applied = new Dictionary<string, long>(c.LimitKeyApplied) { [profileId] = Math.Max(c.LimitKeyApplied.GetValueOrDefault(profileId), seq) };
            c.LimitKeyApplied = applied;
            var everyone = c.AllProfiles.Select(p => p.Id).Append(profileId).Distinct().Min(id => applied.GetValueOrDefault(id));
            if (c.LimitKeyMoves.Any(m => m.Seq <= everyone)) c.LimitKeyMoves = c.LimitKeyMoves.Where(m => m.Seq > everyone).ToList();
        });
        return true;
    }

    private static bool Has(AppConfig c, string id) =>
        c.Overrides.ContainsKey(id) || c.GameProfiles.ContainsKey(id)
        || c.AllProfiles.Any(p => p.Favorites.Contains(id) || p.LastLaunched.ContainsKey(id) || p.PlaytimeMinutes.ContainsKey(id)
            || p.LastSessionMinutes.ContainsKey(id) || p.Rules.LockedItems.Contains(id)
            || p.Collections.Any(x => x?.Items?.Contains(id) == true));

    /// <summary>Everything per person (the default profile and every further one) and the household's per-entry settings.</summary>
    private static void MoveEverywhere(AppConfig c, string from, string to)
    {
        MoveKey(c.GameProfiles, from, to, (old, _) => old);
        MoveKey(c.Overrides, from, to, (old, mine) =>
        {
            old.Hidden |= mine.Hidden;
            return old;
        });
        foreach (var p in c.AllProfiles)
        {
            MoveInList(p.Favorites, from, to);
            MoveInList(p.Rules.LockedItems, from, to);
            MoveCollections(p.Collections, from, to);
            MoveKey(p.LastLaunched, from, to, Math.Max);
            MoveKey(p.PlaytimeMinutes, from, to, (a, b) => a + b);
            MoveKey(p.LastSessionMinutes, from, to, Math.Max);
        }
    }

    /// <summary>The new id takes the old one's place; when it is there already the old one just goes.</summary>
    private static void MoveInList(List<string> list, string from, string to)
    {
        var at = list.IndexOf(from);
        if (at < 0) return;
        list.RemoveAll(x => x == from);
        if (!list.Contains(to)) list.Insert(Math.Min(at, list.Count), to);
    }

    private static void MoveCollections(List<ItemCollection> collections, string from, string to)
    {
        foreach (var x in collections)
            if (x?.Items is { } items) MoveInList(items, from, to);
    }

    private static void MoveKey<T>(Dictionary<string, T> map, string from, string to, Func<T, T, T> merge)
    {
        if (!map.Remove(from, out var old)) return;
        map[to] = map.TryGetValue(to, out var mine) ? merge(old, mine) : old;
    }
}
