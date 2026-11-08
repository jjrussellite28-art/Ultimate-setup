using System.Text.Json;

namespace MegaHub.Plugin.Achievements;

/// <summary>One achievement of a game: its name (as the file spells it) and, when earned, the time.</summary>
internal sealed record AchievementItem(string Name, bool Earned, DateTimeOffset? When)
{
    /// <summary>What the achievement asks for, when the source says (Steam does).</summary>
    public string? Description { get; init; }
}

/// <summary>One game with achievement progress read from a local file.</summary>
internal sealed record AchievementGame(string Id, string Name, int Total, int Earned, DateTimeOffset? LastUnlock, string? LastName, string Folder, IReadOnlyList<AchievementItem> Items)
{
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Earned / Total);
}

/// <summary>
/// Reads the "achievements.json" progress files that Steam emulators such as Goldberg (and its forks) keep, one folder per game.
/// Only reads progress; it never touches a game, a license check or any other file. Written from scratch for Mega Hub.
/// </summary>
internal static class LocalAchievements
{
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxGames = 300;
    private const int MaxDepth = 3;

    /// <summary>The usual save folders of the emulators on Windows (read only).</summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(roaming)) return [];
        return [Path.Combine(roaming, "Goldberg SteamEmu Saves"), Path.Combine(roaming, "GSE Saves")];
    }

    /// <summary>"480=Spacewar;12345=Some game": names for the folders, which are only app numbers.</summary>
    public static Dictionary<string, string> ParseNames(string? text)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('=');
            if (at <= 0 || at == part.Length - 1) continue;
            names[part[..at].Trim()] = part[(at + 1)..].Trim();
        }
        return names;
    }

    public static IReadOnlyList<string> SplitFolders(string? text) =>
        (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static List<AchievementGame> Scan(IEnumerable<string> roots, IReadOnlyDictionary<string, string> names, CancellationToken ct)
    {
        var games = new List<AchievementGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            Walk(root, 0, games, names, seen, ct);
            if (games.Count >= MaxGames) break;
        }
        return [.. games.OrderBy(g => g.Percent == 100).ThenByDescending(g => g.LastUnlock ?? DateTimeOffset.MinValue).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static void Walk(string dir, int depth, List<AchievementGame> games, IReadOnlyDictionary<string, string> names, HashSet<string> seen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (games.Count >= MaxGames) return;
        try
        {
            var file = Path.Combine(dir, "achievements.json");
            if (File.Exists(file) && seen.Add(Path.GetFullPath(file)))
            {
                var info = new FileInfo(file);
                if (info.Length is > 0 and <= MaxFileBytes && Parse(File.ReadAllText(file)) is { } p)
                {
                    var id = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    var name = names.TryGetValue(id, out var n) && n.Length > 0 ? n : id;
                    games.Add(new AchievementGame(id, name, p.Total, p.Earned, p.Last, p.LastName, dir, p.Items));
                }
            }
            if (depth >= MaxDepth) return;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                // Links and junctions can loop or lead anywhere: not followed.
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                Walk(sub, depth + 1, games, names, seen, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // An unreadable folder or file is skipped, never an error for the page.
        }
    }

    /// <summary>The format: { "ACH_NAME": { "earned": true, "earned_time": 1700000000 }, ... }. A list is a definition file, not progress.</summary>
    internal static (int Total, int Earned, DateTimeOffset? Last, string? LastName, IReadOnlyList<AchievementItem> Items)? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            int total = 0, earned = 0;
            long lastTime = 0;
            string? lastName = null;
            var items = new List<AchievementItem>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                total++;
                var done = prop.Value.TryGetProperty("earned", out var e) && e.ValueKind == JsonValueKind.True;
                if (!done)
                {
                    items.Add(new AchievementItem(prop.Name, false, null));
                    continue;
                }
                earned++;
                DateTimeOffset? when = null;
                if (prop.Value.TryGetProperty("earned_time", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var secs) && secs > 0 && secs < 4102444800)
                {
                    when = DateTimeOffset.FromUnixTimeSeconds(secs);
                    if (secs > lastTime)
                    {
                        lastTime = secs;
                        lastName = prop.Name;
                    }
                }
                items.Add(new AchievementItem(prop.Name, true, when));
            }
            if (total == 0) return null;
            DateTimeOffset? last = lastTime > 0 && lastTime < 4102444800 ? DateTimeOffset.FromUnixTimeSeconds(lastTime) : null;
            return (total, earned, last, lastName, items);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
