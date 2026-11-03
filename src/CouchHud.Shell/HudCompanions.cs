using System.Windows;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;
using CouchHud.Core.Remote;
using CouchHud.Sdk;
using CouchHud.Shell.Services;

namespace CouchHud.Shell;

/// <summary>
/// Companion apps (they start next to a game), apps in the Guide menu, and Wand's move from a plugin to a companion app. The rules are
/// in Core (<see cref="CompanionRules"/>, <see cref="CompanionLauncher"/>, <see cref="GuideAppRules"/>, tested without a window); this is
/// the glue: who is asked, when, and with which right.
/// <para>
/// Threat model in short: a companion must never be a way around what the person at the controller may start. So (1) a rule names a
/// library app by id only, never a path; (2) the app must be a plain program file and pass the "may be a game" test again at each start,
/// on the start the library decides on right now; (3) the person's own gate applies at each start: areas, a protected profile, the child's
/// list, age and time rules, and the couch lock (a locked couch starts none, unless the rule says so and the program is not a shell,
/// tool or browser); (4) anything unclear (unusable settings, a start target that moved, a Store app) means it does not start; (5) ending
/// one checks pid, start time and image again on the handle that ends it; (6) a companion starts after the game is in front and without
/// the right to the foreground, and a window that takes the focus anyway is minimized again.
/// </para>
/// </summary>
public sealed partial class Hud
{
    private readonly CompanionRegistry _companions = new();
    // Signed companions (Wand) whose file changed and no longer carries a valid signature by a known publisher: not started, shown as such.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _signatureInvalid = new(StringComparer.Ordinal);
    private OpenLaunch? _openLaunch;

    private sealed record OpenLaunch(string ItemId, DateTimeOffset At);

    private static string WindowsFolder => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// A game was started from here while the couch was open: its companions are judged as at an open couch (the unlock is forgotten right after
    /// the start, but the start itself was allowed). Only for this game and only for a few minutes.
    /// </summary>
    private void NoteOpenLaunch(LibraryItem item) =>
        Interlocked.Exchange(ref _openLaunch, Locked ? null : new OpenLaunch(item.Id, DateTimeOffset.UtcNow));

    private bool TakeOpenLaunch(string itemId)
    {
        var note = Interlocked.Exchange(ref _openLaunch, null);
        return note is not null && note.ItemId == itemId && DateTimeOffset.UtcNow - note.At < TimeSpan.FromMinutes(5);
    }

    private IReadOnlySet<int> CompanionGamePids()
    {
        var pids = new HashSet<int>();
        if (_sessions.Current is { } current) pids.UnionWith(current.Pids.Where(p => p > 0));
        foreach (var other in _sessions.Others) pids.UnionWith(other.Pids.Where(p => p > 0));
        return pids;
    }

    /// <summary>The person's own right to start this app, as the library would judge it. Null: yes.</summary>
    private string? CompanionGate(LibraryItem app)
    {
        if (!Areas.Shows(app)) return "area";
        var rules = config.Current.Profile(Parental.ProfileId).Rules;
        if (!rules.Enabled) return null;
        // The child's list and age rules, then the time rules and "cannot be tracked": a start that would ask for the parents' code (or only
        // advise) does not happen on its own.
        if (!FavoritesShelf.ChildAllows(rules, app, library.Items, null)) return "child";
        var check = Parental.CheckLaunch(app, null, grant: false);
        return check is { Ok: true, Advisory: false, UsesCode: false } ? null : "child";
    }

    /// <summary>The companions to start with this game now (UI thread). Nothing while settings are unusable, a protected profile waits or Gaming is off.</summary>
    private CompanionPlan CompanionPlanFor(GameSession session, bool openAtLaunch)
    {
        var none = new CompanionPlan([], []);
        if (config.Fault is not null || ProfileHeld || !Areas.Runs("companionStart")) return none;
        var rules = config.Current.Companions;
        if (rules.Count == 0 || session.Item.Type != ItemType.Game) return none;
        var plan = CompanionRules.Plan(session.Item, rules, library.Find, app => library.EffectiveLaunch(app), CompanionGate,
            Locked && !openAtLaunch, WindowsFolder);
        foreach (var skip in plan.Skipped) Console.WriteLine($"[companion] {PluginSettingRules.LogText(skip.AppId)}: not started ({skip.Reason})");
        return plan;
    }

    /// <summary>A game showed up as running (any thread): its companions follow once the game is in front.</summary>
    private void OnCompanionGameStarted(GameSession session)
    {
        if (session.Item.Type != ItemType.Game) return;
        var open = TakeOpenLaunch(session.Item.Id);
        Ui(() => _ = RunCompanionsAsync(session, open));
    }

    private async Task RunCompanionsAsync(GameSession session, bool openAtLaunch)
    {
        try
        {
            if (CompanionPlanFor(session, openAtLaunch).Start.Count == 0) return;
            var host = new CompanionHost(CompanionGamePids, OnCompanionSignature);
            // After the game is in front, at most 15 seconds: the game got the foreground through CouchHUD's permission, and a program started
            // afterwards has none. Before that it would race the game for it.
            for (var i = 0; i < 60; i++)
            {
                if (_sessions.Current?.Item.Id != session.Item.Id) return;
                if (host.Foreground() is { } front && host.InGameTree(front.Pid)) break;
                await Task.Delay(250);
            }
            if (_sessions.Current?.Item.Id != session.Item.Id) return;
            // Decided again now: the lock, the profile or the library may have changed during the wait (no stale decision is used).
            var plan = await Application.Current.Dispatcher.InvokeAsync(() => CompanionPlanFor(session, openAtLaunch));
            if (plan.Start.Count == 0 || _sessions.Current?.Item.Id != session.Item.Id) return;
            // Asked again before every single start, on the UI thread: the lock, the profile, the rules and the program may have changed since the plan.
            var runs = await CompanionLauncher.StartAsync(plan, session.Item.Id, host, Environment.ProcessId, start =>
                Application.Current.Dispatcher.Invoke(() => CompanionPlanFor(session, openAtLaunch).Start.Any(s => s.App.Id == start.App.Id)));
            _companions.Add(runs);
            // The game ended while the companions were starting: they are ended now, as with the game's end.
            if (_sessions.Current?.Item.Id != session.Item.Id) await StopCompanionsAsync(session);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[companion] {PluginSettingRules.LogText(session.Item.Name)}: {ex.GetType().Name}");
        }
    }

    /// <summary>A game ended: its companions with "end with the game" are ended, exactly the processes CouchHUD started.</summary>
    private void OnCompanionGameEnded(GameSession session) => _ = StopCompanionsAsync(session);

    private async Task StopCompanionsAsync(GameSession session)
    {
        try
        {
            var still = _sessions.Others.Select(o => o.Item.Id).ToList();
            if (_sessions.Current is { } current) still.Add(current.Item.Id);
            var close = _companions.Ended(session.Item.Id, still, config.Current.Companions);
            if (close.Count == 0) return;
            var outcome = await CompanionStop.StopAsync(close, Win32ProcessControl.Instance, session.Pids.Where(p => p > 0).ToHashSet());
            Console.WriteLine($"[companion] {PluginSettingRules.LogText(session.Item.Name)}: {outcome.Found} companion process(es) found, {outcome.Killed} ended by force");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[companion] ending after {PluginSettingRules.LogText(session.Item.Name)}: {ex.GetType().Name}");
        }
    }

    // -----------------------------------------------------------------------
    // Settings: Library › Companion apps

    private LaunchTarget? SafeEffective(LibraryItem app)
    {
        try
        {
            return library.EffectiveLaunch(app);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            return null;
        }
    }

    /// <summary>The rules, the apps and games they can name, and the state of the Wand offer. Unlocked only (the bridge refuses it on a locked couch).</summary>
    public object GetCompanions()
    {
        RequireProfileChosen();
        RequireUnlocked();
        var areas = Areas;
        var rules = CompanionRules.Sanitize(config.Current.Companions);
        var items = library.Items;
        string Problem(LibraryItem? app)
        {
            if (app is null) return "missing";
            if (!areas.Shows(app)) return "area";
            var start = SafeEffective(app);
            return start is null ? "start" : CompanionRules.Eligible(app, start, WindowsFolder).ToString();
        }
        object View(CompanionRule r)
        {
            var app = library.Find(r.AppId);
            return new
            {
                appId = r.AppId,
                name = app?.Name ?? r.AppId,
                mode = r.Mode,
                games = r.GameIds.Select(id => new { id, name = library.Find(id)?.Name ?? id }).ToList(),
                closeWithGame = r.CloseWithGame,
                minimized = r.Minimized,
                whenLocked = r.WhenLocked,
                problem = _signatureInvalid.ContainsKey(r.AppId) || SignedProgram.IsBroken(r.Sha256) ? "signature" : Problem(app) is var p && p != nameof(CompanionVerdict.Ok) ? p : null
            };
        }
        return new
        {
            max = CompanionRules.MaxRules,
            maxGames = CompanionRules.MaxGames,
            rules = rules.Select(View).ToList(),
            candidates = items.Where(i => i.Type == ItemType.App && !rules.Any(r => r.AppId == i.Id) && Problem(i) == nameof(CompanionVerdict.Ok))
                .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).Select(i => new { id = i.Id, name = i.Name }).ToList(),
            games = items.Where(i => i.Type == ItemType.Game && areas.Shows(i)).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(i => new { id = i.Id, name = i.Name }).ToList(),
            wand = WandState()
        };
    }

    /// <summary>
    /// Creates or changes the rule of one library app. Everything is checked here, whatever the page sent: the app is a library app that shows
    /// and may be a companion, every game named is a library game that shows. With parental controls the parents' code (a fresh one for every
    /// change), without them the unlock. The code is used up only by a change that passed all checks.
    /// </summary>
    public object SetCompanion(string appId, CompanionChange change)
    {
        RefuseWhileConfigFaulted();
        RequireProfileChosen();
        RequireUnlocked();
        var app = library.Find(appId) ?? throw new KeyNotFoundException($"unknown item {appId}");
        if (!Areas.Shows(app) || !Areas.Games) throw new InvalidOperationException(Strings.Get("areas.off"));
        var start = SafeEffective(app) ?? throw new InvalidOperationException(Strings.Get("companion.notEligible"));
        if (CompanionRules.Eligible(app, start, WindowsFolder) != CompanionVerdict.Ok) throw new ArgumentException(Strings.Get("companion.notEligible"));
        List<string>? games = null;
        if (change.GameIds is not null)
        {
            games = [];
            foreach (var id in change.GameIds)
            {
                if (library.Find(id) is not { Type: ItemType.Game } game || !Areas.Shows(game)) throw new ArgumentException(Strings.Get("companion.badGame"));
                games.Add(id);
            }
            if (games.Distinct().Count() > CompanionRules.MaxGames) throw new ArgumentException(Strings.Get("companion.tooManyGames", CompanionRules.MaxGames));
        }
        if (change.Mode is not (null or CompanionRule.ModeAll or CompanionRule.ModeGames)) throw new ArgumentException(Strings.Get("companion.notEligible"));
        var isNew = !config.Current.Companions.Any(r => r.AppId == appId);
        if (isNew && config.Current.Companions.Count >= CompanionRules.MaxRules) throw new InvalidOperationException(Strings.Get("companion.full", CompanionRules.MaxRules));
        if (config.Current.ParentalEnabled) RequireParentGrant("companions", appId);
        // A rule that starts on the locked couch or beside every game is a right, not a convenience: it needs the second factor when it
        // newly becomes one of those (a plain rule, and anything already set so, stays as it was).
        var before = config.Current.Companions.FirstOrDefault(r => r.AppId == appId);
        var nowLocked = change.WhenLocked ?? before?.WhenLocked ?? false;
        var nowAll = (change.Mode ?? before?.Mode ?? CompanionRule.ModeGames) == CompanionRule.ModeAll;
        if ((nowLocked && before?.WhenLocked != true) || (nowAll && before?.Mode != CompanionRule.ModeAll))
            RequireSecondFactorFor(SecondFactorActions.CompanionLocked, true, app.Name, appId, nowLocked ? "locked" : "-", nowAll ? "all" : "games");
        config.UpdateOrThrow(c =>
        {
            var list = CompanionRules.Sanitize(c.Companions);
            var rule = list.FirstOrDefault(r => r.AppId == appId);
            if (rule is null) list.Add(rule = new CompanionRule { AppId = appId });
            if (change.Mode is { } mode) rule.Mode = mode;
            if (games is not null) rule.GameIds = games;
            if (change.CloseWithGame is { } close) rule.CloseWithGame = close;
            if (change.Minimized is { } minimized) rule.Minimized = minimized;
            if (change.WhenLocked is { } locked) rule.WhenLocked = locked;
            c.Companions = CompanionRules.Sanitize(list);
        });
        return GetCompanions();
    }

    /// <summary>Takes a rule away. It narrows what starts, so the unlock is enough.</summary>
    public object RemoveCompanion(string appId)
    {
        RefuseWhileConfigFaulted();
        RequireProfileChosen();
        RequireUnlocked();
        config.UpdateOrThrow(c => c.Companions = CompanionRules.Sanitize(c.Companions).Where(r => r.AppId != appId).ToList());
        return GetCompanions();
    }

    // -----------------------------------------------------------------------
    // Apps in the Guide menu

    /// <summary>
    /// The apps in the Guide menu for the person at the controller, and the saved list. None on a locked couch, none that a child may not
    /// start by their own rules; starting one goes through <see cref="Launch"/>, with the same gate as from the library.
    /// </summary>
    public object GetGuideApps()
    {
        var c = config.Current;
        var rules = c.Profile(Parental.ProfileId).Rules;
        var all = library.Items;
        var visible = ProfileHeld
            ? []
            : GuideAppRules.Visible(c.GuideApps, library.Find, Areas.Shows, i => !rules.Enabled || FavoritesShelf.ChildAllows(rules, i, all, null), Locked);
        return new
        {
            max = GuideAppRules.Max,
            ids = Locked ? [] : GuideAppRules.Clean(c.GuideApps),
            items = visible.Select(i => new { id = i.Id, name = i.Name }).ToList()
        };
    }

    /// <summary>Puts a library app into the Guide menu or takes it out. Unlocked only; what it offers is judged again at each start.</summary>
    public object SetGuideApp(string id, bool on)
    {
        RefuseWhileConfigFaulted();
        RequireProfileChosen();
        RequireUnlocked();
        var item = library.Find(id);
        if (on && (item is null || !Areas.Shows(item))) throw new InvalidOperationException(Strings.Get("areas.off"));
        var next = GuideAppRules.With(config.Current.GuideApps, item, id, on);
        if (next is null) throw new InvalidOperationException(Strings.Get(item is { Type: ItemType.App } ? "guideApps.full" : "guideApps.notAnApp", GuideAppRules.Max));
        config.UpdateOrThrow(c => c.GuideApps = next);
        return GetGuideApps();
    }

    // -----------------------------------------------------------------------
    // Wand: from a plugin to a companion app

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private bool WandPluginInstalled()
    {
        try
        {
            return plugins.Plugins.Any(p => p.Id.Equals(WandSetup.PluginId, StringComparison.OrdinalIgnoreCase))
                || plugins.InstalledPluginIds().Contains(WandSetup.PluginId, StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The one hint, at the start: the Wand plugin is retired, Wand is a companion app. Nothing is removed or set up on its own.</summary>
    private void PostWandHintOnce()
    {
        // Only on an unlocked couch, and marked as shown only then: a guest at the controller never uses the hint up (it comes at the next
        // unlocked look, see GetPluginSuggestionsAsync).
        if (Locked || !WandSetup.HintDue(config.Current.WandCompanionHintShown, WandPluginInstalled())) return;
        PostNotification("CouchHUD", Strings.Ref("wand.hint.title"), Strings.Ref("wand.hint.body"));
        config.Update(c => c.WandCompanionHintShown = true);
    }

    /// <summary>
    /// Whether the file carries a valid Authenticode signature by a known publisher of Wand (WinVerifyTrust on the held file, then the chain to
    /// the machine's own roots with the Code Signing usage, then the publisher name), and its hash. The hash is of the very file that was checked.
    /// </summary>
    private static (bool Verified, string? Hash) WandCheck(string exe)
    {
        using var held = CompanionPath.Hold(exe);
        return held is not null && Authenticode.VerifyPublisher(held) ? (true, held.Sha256Hex()) : (false, null);
    }

    /// <summary>
    /// The host's word from a companion start with a signature rule: the signature held (and the hash to keep when the file changed) or did
    /// not. A rule whose file lost its signature is shown as such; one that is fine again is cleared.
    /// </summary>
    private void OnCompanionSignature(string appId, bool valid, string? newHash)
    {
        var wasInvalid = valid ? _signatureInvalid.TryRemove(appId, out _) : !_signatureInvalid.TryAdd(appId, 0);
        if (!valid && !wasInvalid) Ui(() => PostNotification("CouchHUD", Strings.Ref("companion.signatureInvalid.title"), Strings.Ref("companion.signatureInvalid.body")));
        if (newHash is null) return;
        Ui(() => config.Update(c =>
        {
            var list = CompanionRules.Sanitize(c.Companions);
            if (list.FirstOrDefault(r => r.AppId == appId) is { } rule) rule.Sha256 = newHash;
            c.Companions = list;
        }));
    }

    private object WandState()
    {
        var exe = WandSetup.FindExe(LocalAppData, File.Exists);
        var existing = exe is null ? null : library.Items.FirstOrDefault(i => i.Type == ItemType.App && string.Equals(i.Launch.Target, exe, StringComparison.OrdinalIgnoreCase));
        var rules = CompanionRules.Sanitize(config.Current.Companions);
        return new
        {
            pluginInstalled = WandPluginInstalled(),
            found = exe is not null,
            // The full path is always shown in the confirmation; "verified" means a trusted signature by Wand or WeMod (set up as a companion
            // app); otherwise only the normal add-an-app route is offered, with a warning.
            path = exe,
            verified = exe is not null && WandCheck(exe).Verified,
            appId = existing?.Id,
            hasRule = existing is not null && rules.Any(r => r.AppId == existing.Id)
        };
    }

    /// <summary>
    /// Sets Wand up as a companion app, only from the place the host found Wand.exe at (the page names no path): adds the app (or uses the
    /// entry that is there), puts it into the Guide menu and makes the rule. The same gate as any change of the rules. The plugin is not touched.
    /// </summary>
    public async Task<object> SetupWandAsync(bool withEveryGame, bool acceptUnverified = false)
    {
        RefuseWhileConfigFaulted();
        RequireProfileChosen();
        RequireUnlocked();
        if (!Areas.Games || !Areas.Apps) throw new InvalidOperationException(Strings.Get("areas.off"));
        var exe = WandSetup.FindExe(LocalAppData, File.Exists) ?? throw new InvalidOperationException(Strings.Get("wand.notFound"));
        // Checked again here, whatever the page showed: Wand is set up as a companion only when its file carries a trusted signature by Wand or
        // WeMod. An unverified file is at most added as a plain app (the person confirmed its full path), without a rule and without the Guide menu.
        var (verified, hash) = WandCheck(exe);
        if (!verified && !acceptUnverified) throw new InvalidOperationException(Strings.Get("wand.unverified"));
        var probe = new LibraryItem { Id = "probe", Source = "custom", Name = WandSetup.AppName, Type = ItemType.App, Launch = new LaunchTarget(LaunchKind.Exe, exe) };
        if (CompanionRules.Eligible(probe, probe.Launch, WindowsFolder) != CompanionVerdict.Ok) throw new InvalidOperationException(Strings.Get("companion.notEligible"));
        if (config.Current.ParentalEnabled) RequireParentGrant("companions", "wand");
        if (withEveryGame && verified)
            RequireSecondFactorFor(SecondFactorActions.CompanionLocked, true, WandSetup.AppName, "wand", "-", "all");
        LibraryItem? app = library.Items.FirstOrDefault(i => i.Type == ItemType.App && string.Equals(i.Launch.Target, exe, StringComparison.OrdinalIgnoreCase));
        if (app is null)
        {
            await AddEntriesAsync([new CustomEntry { Name = WandSetup.AppName, Type = "app", Path = exe }]);
            app = library.Items.FirstOrDefault(i => i.Type == ItemType.App && string.Equals(i.Launch.Target, exe, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException(Strings.Get("wand.notFound"));
        }
        if (!verified) return GetCompanions();
        config.UpdateOrThrow(c =>
        {
            var list = CompanionRules.Sanitize(c.Companions);
            if (list.FirstOrDefault(r => r.AppId == app.Id) is { } existing) existing.Sha256 = hash ?? "";
            else if (list.Count < CompanionRules.MaxRules)
                list.Add(new CompanionRule { AppId = app.Id, Mode = withEveryGame ? CompanionRule.ModeAll : CompanionRule.ModeGames, Sha256 = hash ?? "" });
            c.Companions = list;
            if (GuideAppRules.With(c.GuideApps, app, app.Id, true) is { } guide) c.GuideApps = guide;
        });
        return GetCompanions();
    }
}
