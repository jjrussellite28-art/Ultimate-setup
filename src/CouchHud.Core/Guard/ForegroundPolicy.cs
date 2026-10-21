namespace CouchHud.Core.Guard;

/// <summary>What the foreground guard does with a window that just came to the front.</summary>
public enum ForegroundAction
{
    Allow,
    /// <summary>Minimize it and bring the HUD back.</summary>
    Minimize,
    /// <summary>Big Picture: ask the window to close, minimize as a fallback.</summary>
    CloseAndMinimize,
    /// <summary>Locked couch: a standard file or print dialog is asked to close (minimized if it stays).</summary>
    CloseDialog
}

/// <summary>Why a window is suppressed or left alone (for the log and the tests).</summary>
public enum ForegroundReason
{
    Suppressed,
    NotListed,
    Disabled,
    GameRunning,
    MouseMode,
    PcMode,
    Browsing,
    LockPrompt,
    Suspended,
    FamilyReleased,
    StartedByCouchHud,
    OwnWindow,
    // Locked couch (see LockedForeground)
    NotLocked,
    LockedSystem,
    LockedGame,
    LockedFullscreen,
    LockedDialog,
    LockedForeign,
    LockedUnknown,
    /// <summary>An app the parents started while unlocked: free until the HUD is back in front.</summary>
    LockedAppReleased
}

/// <summary>State of the HUD at the moment a window comes to the front.</summary>
public sealed record ForegroundContext(
    bool Enabled,
    bool GameRunning,
    bool MouseMode,
    bool PcMode,
    bool Browsing,
    bool LockPrompt,
    bool Suspended = false,
    IReadOnlyCollection<string>? Released = null,
    /// <summary>Parental controls: while a game runs, clients stay down too (no way out through Steam's file dialogs).</summary>
    bool StrictClients = false,
    /// <summary>The couch lock is on and closed: only the positive list of <see cref="LockedForeground"/> may be in front.</summary>
    bool Locked = false,
    /// <summary>Locked couch: a launch is under way and its game is not there yet (the only time a released client may show).</summary>
    bool GameStarting = false,
    /// <summary>CouchHud is the shell (no desktop of its own behind it).</summary>
    bool AsShell = false,
    /// <summary>Locked couch: apps that were started while unlocked are released until the HUD is back in front (see ShowHud).</summary>
    bool AppsReleased = false);

public sealed record ForegroundDecision(ForegroundAction Action, ForegroundReason Reason, string? Family = null)
{
    public bool Suppress => Action != ForegroundAction.Allow;
}

/// <summary>
/// CouchHud is in charge on the couch: game clients (Steam, Epic …) and known nuisances do not get
/// to cover it. Pure data and decisions; the Windows side lives in the Shell project.
/// </summary>
public static class ForegroundPolicy
{
    /// <summary>Process names (no .exe) per client family, helpers included.</summary>
    private static readonly (string Family, string[] Processes)[] Clients =
    [
        ("steam", ["steam", "steamwebhelper", "steamerrorreporter"]),
        ("epic", ["EpicGamesLauncher", "EpicWebHelper"]),
        ("ea", ["EADesktop", "EABackgroundService", "EACefSubProcess"]),
        ("ubisoft", ["upc", "UbisoftConnect", "UplayWebCore"]),
        ("gog", ["GalaxyClient", "GalaxyClient Helper"]),
        ("battlenet", ["Battle.net", "Battle.net Launcher"]),
        ("xbox", ["XboxPcApp", "XboxPcAppFT", "GamingApp", "XboxApp", "GameBar"]),
        ("playnite", ["Playnite.FullscreenApp", "Playnite.DesktopApp"])
    ];

    /// <summary>Known pop-up makers that are not clients: exact names only (a pattern would catch legitimate tools).</summary>
    private static readonly string[] Nuisances =
    [
        "GoogleUpdate", "MicrosoftEdgeUpdate", "OneDrive", "OneDriveStandaloneUpdater", "jusched",
        "MusNotificationUx", "MoNotificationUx", "DropboxUpdate", "AdobeARM", "MozillaMaintenance"
    ];

    public static IReadOnlyList<(string Family, string[] Processes)> ClientList => Clients;

    public static IReadOnlyList<string> NuisanceList => Nuisances;

    /// <summary>Process name without ".exe", any case. Null when it is neither a client nor a listed nuisance.</summary>
    public static string? Classify(string? processName)
    {
        var name = Normalize(processName);
        if (name.Length == 0) return null;
        foreach (var (family, processes) in Clients)
        {
            if (processes.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase))) return family;
        }
        return Nuisances.Any(p => Matches(p, name)) ? "nuisance" : null;
    }

    /// <summary>All process names of the family of this process (Steam → steam, steamwebhelper …); the name itself if unlisted.</summary>
    public static IReadOnlyList<string> FamilyProcesses(string processName)
    {
        var family = Classify(processName);
        var found = Clients.FirstOrDefault(c => c.Family == family).Processes;
        return found ?? [processName];
    }

    /// <summary>Every client process name (helpers included), e.g. to minimize all their windows.</summary>
    public static IEnumerable<string> AllClientProcesses() => Clients.SelectMany(c => c.Processes);

    private static readonly string[] BigPictureTitles =
        ["Steam Big Picture Mode", "Steam Big Picture", "Big Picture Mode", "Big Picture", "Steam Big Picture-Modus", "Steam Big Picture Modus"];

    /// <summary>Steam's Big Picture window by its exact title; anything else is only minimized, never closed.</summary>
    /// <summary>
    /// A launcher was opened while unlocked (mouse mode, HUD hidden) and the lock has closed again: a locked couch has no mouse mode, it
    /// ends and the HUD comes back (otherwise the minimized foreign window leaves a black screen). Not in PC mode or in the couch browser.
    /// </summary>
    public static bool ShouldEndMouseMode(ForegroundContext ctx) => ctx.Locked && ctx.MouseMode && !ctx.PcMode && !ctx.Browsing;

    public static bool IsBigPicture(string? family, string? title) =>
        family == "steam" && title is not null && BigPictureTitles.Any(t => string.Equals(t, title.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The client family a library item or own entry starts (Steam game, Epic URL, a client's exe …), or null.</summary>
    public static string? FamilyOfLaunch(string? source, CouchHud.Sdk.LaunchKind kind, string? target)
    {
        var bySource = source?.ToLowerInvariant() switch
        {
            "steam" => "steam",
            "epic" => "epic",
            "gog" => "gog",
            "ubisoft" or "uplay" => "ubisoft",
            "ea" or "origin" => "ea",
            "battlenet" => "battlenet",
            "xbox" => "xbox",
            _ => null
        };
        if (bySource is not null || string.IsNullOrWhiteSpace(target)) return bySource;
        if (kind == CouchHud.Sdk.LaunchKind.Url)
        {
            var t = target.Trim();
            string[][] schemes =
            [
                ["steam", "steam:", "steam-"], ["epic", "com.epicgames.launcher:"], ["ubisoft", "uplay:", "ubisoft:"],
                ["gog", "goggalaxy:"], ["ea", "origin:", "origin2:", "eadesktop:"], ["battlenet", "battlenet:"], ["xbox", "ms-xbl-", "xbox:"]
            ];
            foreach (var entry in schemes)
            {
                if (entry.Skip(1).Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return entry[0];
            }
            // Store apps by AUMID: only the Xbox ones.
            if (t.StartsWith("shell:appsfolder", StringComparison.OrdinalIgnoreCase)
                && (t.Contains("Xbox", StringComparison.OrdinalIgnoreCase) || t.Contains("Microsoft.Gaming", StringComparison.OrdinalIgnoreCase))) return "xbox";
            return null;
        }
        var family = Classify(Path.GetFileName(target.Trim().Trim('"')));
        return family is "nuisance" ? null : family;
    }

    /// <param name="startedByCouchHud">Lazy (costs a process snapshot): the process was started by CouchHud itself (Apps tab, own entries) or descends from such a process.</param>
    /// <param name="ownProcess">The window belongs to CouchHud (HUD, browser).</param>
    public static ForegroundDecision Decide(
        ForegroundContext ctx, string? processName, string? title, Func<bool> startedByCouchHud, bool ownProcess = false)
    {
        if (ownProcess) return new(ForegroundAction.Allow, ForegroundReason.OwnWindow);
        var family = Classify(processName);
        if (family is null) return new(ForegroundAction.Allow, ForegroundReason.NotListed);
        if (ctx.Suspended) return new(ForegroundAction.Allow, ForegroundReason.Suspended, family);
        if (!ctx.Enabled) return new(ForegroundAction.Allow, ForegroundReason.Disabled, family);
        if (ctx.GameRunning && !ctx.StrictClients) return new(ForegroundAction.Allow, ForegroundReason.GameRunning, family);
        if (ctx.MouseMode) return new(ForegroundAction.Allow, ForegroundReason.MouseMode, family);
        if (ctx.PcMode) return new(ForegroundAction.Allow, ForegroundReason.PcMode, family);
        if (ctx.Browsing) return new(ForegroundAction.Allow, ForegroundReason.Browsing, family);
        if (ctx.LockPrompt) return new(ForegroundAction.Allow, ForegroundReason.LockPrompt, family);
        // A client the user's own action needs (game start, login): let it come until the user is back in the HUD.
        if (!ctx.StrictClients && ctx.Released is { } released && released.Contains(family)) return new(ForegroundAction.Allow, ForegroundReason.FamilyReleased, family);
        if (!ctx.StrictClients && startedByCouchHud()) return new(ForegroundAction.Allow, ForegroundReason.StartedByCouchHud, family);
        var action = IsBigPicture(family, title) ? ForegroundAction.CloseAndMinimize : ForegroundAction.Minimize;
        return new(action, ForegroundReason.Suppressed, family);
    }

    private static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static bool Matches(string pattern, string name)
    {
        if (!pattern.Contains('*')) return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);
        return pattern.Trim('*') is { Length: > 0 } core && name.Contains(core, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>At most one action per key within the interval (a window handle, or 0 for "any").</summary>
public sealed class ActionThrottle(TimeSpan interval)
{
    private readonly Dictionary<long, DateTimeOffset> _last = [];

    public bool TryAcquire(long key, DateTimeOffset now)
    {
        if (_last.TryGetValue(key, out var at) && now - at < interval) return false;
        _last[key] = now;
        if (_last.Count > 256)
        {
            foreach (var old in _last.Where(p => now - p.Value > interval).Select(p => p.Key).ToList()) _last.Remove(old);
        }
        return true;
    }
}

public enum BackoffAction
{
    /// <summary>Go ahead and minimize.</summary>
    Act,
    /// <summary>The window keeps coming back or can't be minimized: stop fighting and tell the user (once).</summary>
    GiveUp,
    /// <summary>Already given up on this one.</summary>
    Quiet
}

/// <summary>
/// Time-bound state of the guard: client families released for the user's own actions (game start, login) and
/// the give-up rule per process. Pure, the clock is passed in.
/// </summary>
public sealed class ForegroundState
{
    public TimeSpan ReleaseTime { get; init; } = TimeSpan.FromMinutes(10);
    public int MaxAttempts { get; init; } = 2;
    public TimeSpan AttemptWindow { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan QuietTime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Not null: giving up is never for good (the locked couch). The quiet time after a give-up starts at this value and doubles with
    /// every further give-up of the same process, up to <see cref="QuietTime"/>, so a window that insists is met again and again, with
    /// growing pauses instead of a loop.
    /// </summary>
    public TimeSpan? BackoffStart { get; init; }

    private readonly Dictionary<string, int> _giveUps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _released = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTimeOffset>> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _gaveUp = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows of this family may come to the front until <see cref="ReleaseAll"/> or the release time is over.</summary>
    public void Release(string? family, DateTimeOffset now)
    {
        if (family is not null) _released[family] = now + ReleaseTime;
    }

    /// <summary>The user is back in the HUD (guide, tray …).</summary>
    public void ReleaseAll() => _released.Clear();

    public IReadOnlyCollection<string> Released(DateTimeOffset now)
    {
        foreach (var gone in _released.Where(p => p.Value <= now).Select(p => p.Key).ToList()) _released.Remove(gone);
        return _released.Keys.ToList();
    }

    /// <summary>How long the guard stays quiet about this process after it gave up on it.</summary>
    public TimeSpan QuietFor(string key)
    {
        if (BackoffStart is not { } start) return QuietTime;
        var count = _giveUps.GetValueOrDefault(key);
        if (count <= 1) return start < QuietTime ? start : QuietTime;
        var grown = start * Math.Pow(2, Math.Min(count - 1, 16));
        return grown < QuietTime ? grown : QuietTime;
    }

    private void GaveUp(string key, DateTimeOffset now)
    {
        // A long calm since the last give-up starts the backoff from the beginning.
        if (_gaveUp.TryGetValue(key, out var last) && now - last > QuietTime * 3) _giveUps.Remove(key);
        _gaveUp[key] = now;
        _giveUps[key] = _giveUps.GetValueOrDefault(key) + 1;
    }

    public BackoffAction Attempt(string key, DateTimeOffset now)
    {
        if (_gaveUp.TryGetValue(key, out var at) && now - at < QuietFor(key)) return BackoffAction.Quiet;
        if (BackoffStart is null) _gaveUp.Remove(key);
        if (!_attempts.TryGetValue(key, out var list)) _attempts[key] = list = [];
        list.RemoveAll(t => now - t > AttemptWindow);
        if (list.Count >= MaxAttempts)
        {
            GaveUp(key, now);
            return BackoffAction.GiveUp;
        }
        list.Add(now);
        return BackoffAction.Act;
    }

    /// <summary>The window was still not minimized after the attempt (elevated window …): stop at once.</summary>
    public BackoffAction Failed(string key, DateTimeOffset now)
    {
        if (_gaveUp.TryGetValue(key, out var at) && now - at < QuietFor(key)) return BackoffAction.Quiet;
        GaveUp(key, now);
        return BackoffAction.GiveUp;
    }
}
