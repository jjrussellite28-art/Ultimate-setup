using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MegaHub.Plugin.Achievements;

/// <summary>What the Steam source is doing, for the row in Sources.</summary>
internal enum SteamStatus
{
    Off,
    NoKey,
    NoAccount,
    Loading,
    Ready,
    Private,
    BadKey,
    Blocked,
    Error
}

/// <summary>The Steam account that is signed in to Steam on this PC.</summary>
internal sealed record SteamAccount(string SteamId, string Persona);

/// <summary>
/// Reads one Steam account's achievements through Steam's web service (api.steampowered.com), the same way sites such as Exophase do:
/// the account's profile (game details) must be public and the request carries the owner's own free web key. Only reads.
/// The key is only ever put into the request address; it is never logged, stored in the cache or shown.
/// </summary>
internal static partial class SteamAchievements
{
    private const int MaxGames = 400;
    private const int Parallel = 4;
    private const string Base = "https://api.steampowered.com/";

    /// <summary>A Steam ID64: 17 digits.</summary>
    public static bool IsSteamId(string? text) => text is { Length: 17 } && text.All(char.IsAsciiDigit) && text.StartsWith("7656", StringComparison.Ordinal);

    /// <summary>The account Steam itself is signed in with on this PC (the file loginusers.vdf of Steam's own folder), or null.</summary>
    public static SteamAccount? FindAccount()
    {
        foreach (var file in LoginFiles())
        {
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length > 256 * 1024) continue;
                if (ParseLoginUsers(File.ReadAllText(file)) is { } found) return found;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not readable right now: the next refresh tries again.
            }
        }
        return null;
    }

    private static IEnumerable<string> LoginFiles()
    {
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var root in new[] { x86, pf })
            if (!string.IsNullOrEmpty(root)) yield return Path.Combine(root, "Steam", "config", "loginusers.vdf");
    }

    [GeneratedRegex("^\\s*\"(\\d{17})\"\\s*$")]
    private static partial Regex AccountLine();

    [GeneratedRegex("^\\s*\"(\\w+)\"\\s+\"(.*)\"\\s*$")]
    private static partial Regex ValueLine();

    /// <summary>The most recently used account of a loginusers.vdf text (the first one when none says so).</summary>
    internal static SteamAccount? ParseLoginUsers(string text)
    {
        SteamAccount? first = null;
        SteamAccount? recent = null;
        string? id = null;
        string persona = "";
        var isRecent = false;

        void Close()
        {
            if (id is null) return;
            var account = new SteamAccount(id, persona.Length > 0 ? persona : id);
            first ??= account;
            if (isRecent) recent ??= account;
            id = null;
            persona = "";
            isRecent = false;
        }

        foreach (var line in text.Split('\n'))
        {
            if (AccountLine().Match(line) is { Success: true } a)
            {
                Close();
                id = a.Groups[1].Value;
                continue;
            }
            if (id is null || ValueLine().Match(line) is not { Success: true } v) continue;
            var name = v.Groups[1].Value;
            if (name.Equals("PersonaName", StringComparison.OrdinalIgnoreCase)) persona = v.Groups[2].Value;
            else if (name.Equals("MostRecent", StringComparison.OrdinalIgnoreCase)) isRecent = v.Groups[2].Value == "1";
        }
        Close();
        return recent ?? first;
    }

    /// <summary>How a refresh ended.</summary>
    internal sealed record Outcome(SteamStatus Status, List<AchievementGame> Games);

    /// <summary>
    /// Asks Steam for the owned games and then for the achievements of each (four at a time). <paramref name="progress"/> gets the games done
    /// and the games to ask about, now and then.
    /// </summary>
    public static async Task<Outcome> FetchAsync(HttpClient http, string key, string steamId, Action<int, int> progress, CancellationToken ct)
    {
        var games = new List<AchievementGame>();
        JsonElement owned;
        try
        {
            var (status, body) = await GetAsync(http, "IPlayerService/GetOwnedGames/v1/?key=" + Uri.EscapeDataString(key) + "&steamid=" + steamId
                + "&include_appinfo=1&include_played_free_games=1&format=json", ct).ConfigureAwait(false);
            if (status is 401 or 403) return new(SteamStatus.BadKey, games);
            if (body is null) return new(SteamStatus.Error, games);
            owned = body.Value;
        }
        catch (HttpRequestException)
        {
            return new(SteamStatus.Blocked, games);
        }

        // A private profile answers with an empty "response" (no game_count at all).
        if (!owned.TryGetProperty("response", out var response) || !response.TryGetProperty("game_count", out _)) return new(SteamStatus.Private, games);
        var list = new List<(int App, string Name, long Last)>();
        if (response.TryGetProperty("games", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in array.EnumerateArray())
            {
                if (!g.TryGetProperty("appid", out var app) || !app.TryGetInt32(out var appId)) continue;
                var name = g.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                var last = g.TryGetProperty("rtime_last_played", out var r) && r.TryGetInt64(out var t) ? t : 0;
                list.Add((appId, name.Length > 0 ? name : appId.ToString(CultureInfo.InvariantCulture), last));
            }
        }
        list = [.. list.OrderByDescending(g => g.Last).Take(MaxGames)];

        var done = 0;
        var failed = 0;
        var gate = new SemaphoreSlim(Parallel);
        var found = new List<AchievementGame>();
        var tasks = list.Select(async g =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var game = await FetchGameAsync(http, key, steamId, g.App, g.Name, ct).ConfigureAwait(false);
                if (game is not null) lock (found) found.Add(game);
            }
            catch (HttpRequestException)
            {
                Interlocked.Increment(ref failed);
            }
            finally
            {
                gate.Release();
                var n = Interlocked.Increment(ref done);
                if (n % 10 == 0 || n == list.Count) progress(n, list.Count);
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        games = [.. found.OrderByDescending(g => g.LastUnlock ?? DateTimeOffset.MinValue).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)];
        // Nothing came back and every call failed: that is a problem, not an empty library.
        return new(games.Count == 0 && failed > 0 ? SteamStatus.Error : SteamStatus.Ready, games);
    }

    /// <summary>One game's achievements, or null for a game without any.</summary>
    private static async Task<AchievementGame?> FetchGameAsync(HttpClient http, string key, string steamId, int appId, string name, CancellationToken ct)
    {
        var (status, body) = await GetAsync(http, "ISteamUserStats/GetPlayerAchievements/v1/?key=" + Uri.EscapeDataString(key) + "&steamid=" + steamId
            + "&appid=" + appId.ToString(CultureInfo.InvariantCulture) + "&l=english&format=json", ct).ConfigureAwait(false);
        // 400: the game has no achievements (or no stats). Not an error.
        if (status == 400 || body is null) return null;
        if (!body.Value.TryGetProperty("playerstats", out var stats) || !stats.TryGetProperty("achievements", out var array) || array.ValueKind != JsonValueKind.Array) return null;

        var items = new List<AchievementItem>();
        DateTimeOffset? last = null;
        string? lastName = null;
        foreach (var a in array.EnumerateArray())
        {
            var earned = a.TryGetProperty("achieved", out var ach) && ach.TryGetInt32(out var flag) && flag == 1;
            var title = a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
                ? n.GetString()!
                : a.TryGetProperty("apiname", out var api) && api.ValueKind == JsonValueKind.String ? api.GetString() ?? "?" : "?";
            DateTimeOffset? when = null;
            if (earned && a.TryGetProperty("unlocktime", out var u) && u.TryGetInt64(out var seconds) && seconds > 0)
                when = DateTimeOffset.FromUnixTimeSeconds(seconds);
            var description = a.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()?.Trim() : null;
            items.Add(new AchievementItem(title, earned, when) { Description = description is { Length: > 200 } ? description[..200] : description });
            if (when is { } w && (last is null || w > last)) { last = w; lastName = title; }
        }
        if (items.Count == 0) return null;
        return new AchievementGame(appId.ToString(CultureInfo.InvariantCulture), name, items.Count, items.Count(i => i.Earned), last, lastName, "", items);
    }

    /// <summary>GET; the status code and the parsed body (null when there is none or it is not JSON).</summary>
    private static async Task<(int Status, JsonElement? Body)> GetAsync(HttpClient http, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(Base + path, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status == 429) throw new HttpRequestException("rate limited");
        if (status is 401 or 403) return (status, null);
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            return (status, doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return (status, null);
        }
    }
}
