using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>Why an app cannot be a companion. <see cref="Ok"/> is the only one that starts.</summary>
public enum CompanionVerdict
{
    Ok,
    /// <summary>A game (or something that is not in the library as an app).</summary>
    NotAnApp,
    /// <summary>A web page or the couch browser: not a program next to a game.</summary>
    Web,
    /// <summary>A batch file: cmd.exe runs whatever is in it, and the launcher's hold on the script is for the entry's own start.</summary>
    Script,
    /// <summary>A protocol or a Store (MSIX) app. The activation manager cannot start without taking the focus.</summary>
    Packaged,
    /// <summary>No program file, a relative path, a network path or a device path.</summary>
    NoProgram,
    /// <summary>A shell, an interpreter, a Windows tool or a client (what <see cref="GameEntryRules.MayBeGame"/> refuses).</summary>
    Tool,
    /// <summary>A web browser.</summary>
    Browser,
    /// <summary>Inside the Windows folder.</summary>
    SystemFolder
}

/// <summary>A change of one companion rule as the page sends it: only what is set changes. Checked in full by the host.</summary>
public sealed class CompanionChange
{
    public string? Mode { get; set; }
    public List<string>? GameIds { get; set; }
    public bool? CloseWithGame { get; set; }
    public bool? Minimized { get; set; }
    public bool? WhenLocked { get; set; }
}

/// <summary>One companion that is to start with a game, with the start the library decided on right now.</summary>
public sealed record CompanionStart(CompanionRule Rule, LibraryItem App, LaunchTarget Start);

/// <summary>An app that does not start, and why (<paramref name="Reason"/> is a short code for the log).</summary>
public sealed record CompanionSkip(string AppId, string Reason);

public sealed record CompanionPlan(IReadOnlyList<CompanionStart> Start, IReadOnlyList<CompanionSkip> Skipped);

/// <summary>
/// The rules of companion apps. Pure, so every refusal is tested without a window. The point is: a companion is never a way around what the
/// person at the controller may start. The same checks run when a rule is made and again when it is used (the library, the profile and the
/// couch lock may have changed since), and every doubt means "does not start".
/// </summary>
public static class CompanionRules
{
    /// <summary>Companion rules kept at all.</summary>
    public const int MaxRules = 12;

    /// <summary>Games named in one rule.</summary>
    public const int MaxGames = 50;

    /// <summary>Companions started with one game.</summary>
    public const int MaxStartsPerGame = 6;

    private const int MaxIdLength = 4200;

    /// <summary>A rule list as it may be kept: known modes, no empty or oversized ids, no duplicate app, lists cut. Order is kept.</summary>
    public static List<CompanionRule> Sanitize(IEnumerable<CompanionRule?>? raw)
    {
        var result = new List<CompanionRule>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in raw ?? [])
        {
            if (rule is null || result.Count >= MaxRules) continue;
            var app = (rule.AppId ?? "").Trim();
            if (!ValidId(app) || !seen.Add(app)) continue;
            var games = new List<string>();
            foreach (var id in rule.GameIds ?? [])
            {
                var g = (id ?? "").Trim();
                if (ValidId(g) && g != app && !games.Contains(g, StringComparer.Ordinal) && games.Count < MaxGames) games.Add(g);
            }
            result.Add(new CompanionRule
            {
                AppId = app,
                // An unknown mode is the narrow one: a hand-edited file never widens a rule.
                Mode = rule.Mode == CompanionRule.ModeAll ? CompanionRule.ModeAll : CompanionRule.ModeGames,
                GameIds = games,
                CloseWithGame = rule.CloseWithGame,
                Minimized = rule.Minimized,
                WhenLocked = rule.WhenLocked,
                // Empty stays empty (an ordinary companion). A hash stays a hash. Anything else was meant as a hash and is kept as "broken":
                // such a rule does not start and shows as "signature invalid" instead of silently losing its signature requirement.
                Sha256 = string.IsNullOrWhiteSpace(rule.Sha256) ? "" : SignedProgram.IsHash(rule.Sha256.Trim()) ? rule.Sha256.Trim().ToLowerInvariant() : SignedProgram.Broken
            });
        }
        return result;
    }

    private static bool ValidId(string id) => id.Length is > 0 and <= MaxIdLength && !id.Any(char.IsControl);

    /// <summary>Does the rule apply to this game.</summary>
    public static bool Applies(CompanionRule rule, string gameId) =>
        rule.Mode == CompanionRule.ModeAll || (rule.Mode == CompanionRule.ModeGames && rule.GameIds?.Contains(gameId, StringComparer.Ordinal) == true);

    /// <summary>
    /// May this app be a companion, judged on the start the library would run (the user's own start target and parameters count, they are
    /// what runs). A companion is a plain program file: it must pass what an entry has to pass to be a game on a locked couch.
    /// </summary>
    public static CompanionVerdict Eligible(LibraryItem app, LaunchTarget start, string windowsDir, Func<string, string?>? resolve = null)
    {
        if (app.Type != ItemType.App) return CompanionVerdict.NotAnApp;
        if (start.Embedded) return CompanionVerdict.Web;
        if (start.Kind == LaunchKind.Url) return WebLinks.IsWebUrl(start.Target) ? CompanionVerdict.Web : CompanionVerdict.Packaged;
        var target = start.Target?.Trim().Trim('"') ?? "";
        if (LaunchOptions.IsScript(target)) return CompanionVerdict.Script;
        if (!IsLocalProgramPath(target)) return CompanionVerdict.NoProgram;
        // Judged on the real path behind the file (links, junctions and 8.3 names resolved by Windows from an open handle), never on the
        // spelling in the library. A file that cannot be opened and resolved is no companion.
        var real = (resolve ?? CompanionPath.Resolve)(target);
        return real is null ? CompanionVerdict.NoProgram : CompanionPath.Judge(target, real, windowsDir);
    }

    /// <summary>A plain drive path to an .exe: see <see cref="CompanionPath.IsPlainProgramPath"/>.</summary>
    public static bool IsLocalProgramPath(string? path) => CompanionPath.IsPlainProgramPath(path);

    /// <summary>
    /// Which companions start with this game right now. <paramref name="gate"/> is the person's own right to start the app (areas, the child's
    /// list and time rules, a protected profile): null means yes, anything else is the reason it does not. <paramref name="locked"/> is the couch
    /// lock at this moment (counted as open when the game was started with the couch open). A game that is no game, the game itself and an app
    /// the library no longer has start nothing.
    /// </summary>
    public static CompanionPlan Plan(
        LibraryItem game,
        IReadOnlyList<CompanionRule> rules,
        Func<string, LibraryItem?> find,
        Func<LibraryItem, LaunchTarget> effectiveStart,
        Func<LibraryItem, string?> gate,
        bool locked,
        string windowsDir,
        Func<string, string?>? resolve = null)
    {
        var start = new List<CompanionStart>();
        var skipped = new List<CompanionSkip>();
        if (game.Type != ItemType.Game) return new CompanionPlan(start, skipped);
        foreach (var rule in Sanitize(rules))
        {
            if (!Applies(rule, game.Id)) continue;
            if (start.Count >= MaxStartsPerGame) { skipped.Add(new CompanionSkip(rule.AppId, "limit")); continue; }
            if (rule.AppId == game.Id || find(rule.AppId) is not { } app) { skipped.Add(new CompanionSkip(rule.AppId, "missing")); continue; }
            if (locked && !rule.WhenLocked) { skipped.Add(new CompanionSkip(rule.AppId, "locked")); continue; }
            if (SignedProgram.IsBroken(rule.Sha256)) { skipped.Add(new CompanionSkip(rule.AppId, "signature")); continue; }
            if (gate(app) is { } refused) { skipped.Add(new CompanionSkip(rule.AppId, refused)); continue; }
            LaunchTarget target;
            try
            {
                target = effectiveStart(app);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
            {
                // A start target that moved or was swapped: not started instead of starting something else.
                skipped.Add(new CompanionSkip(rule.AppId, "start"));
                continue;
            }
            var verdict = Eligible(app, target, windowsDir, resolve);
            if (verdict != CompanionVerdict.Ok) { skipped.Add(new CompanionSkip(rule.AppId, "not-eligible:" + verdict)); continue; }
            start.Add(new CompanionStart(rule, app, target));
        }
        return new CompanionPlan(start, skipped);
    }
}
